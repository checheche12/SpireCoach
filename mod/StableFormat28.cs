using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;

namespace Sts2Headless;

// Upgrades Stable (v0.111.1) Format 22 Ironclad/Necrobinder frames to Format 28
// by populating hand glow flags (tf[20..21]) and immediate energy/draw gains (cf[12..13])
// to match the Format 28 checkpoints trained in AlphaDefect.
public sealed partial class Observation
{
    private static readonly HashSet<Type> StableEnergyCards =
    [
        typeof(Adrenaline), typeof(Alignment), typeof(BigBang), typeof(Bloodletting), typeof(BorrowedTime),
        typeof(BrightestFlame), typeof(Fuel), typeof(Luminesce), typeof(Neurosurge), typeof(Offering),
        typeof(Production), typeof(Supercritical), typeof(Tactician), typeof(Turbo), typeof(Wisp),
    ];

    private static readonly HashSet<Type> StableDrawCards =
    [
        typeof(Acrobatics), typeof(Adrenaline), typeof(Backflip), typeof(BattleTrance), typeof(BigBang),
        typeof(BrightestFlame), typeof(BurningPact), typeof(Constellation), typeof(Coolheaded),
        typeof(DecisionsDecisions), typeof(DrumOfBattle), typeof(Expertise), typeof(Finesse), typeof(FlashOfSteel),
        typeof(Glimmer), typeof(Glow), typeof(MasterOfStrategy), typeof(MinionStrike), typeof(Modded),
        typeof(Neurosurge), typeof(Offering), typeof(Overclock), typeof(Parse), typeof(PhotonCut), typeof(PommelStrike),
        typeof(Prepared), typeof(Prophesize), typeof(Reboot), typeof(Reflex), typeof(RocketPunch), typeof(Scourge),
        typeof(Scrape), typeof(ShrugItOff), typeof(Skim), typeof(Slimed), typeof(Soul), typeof(SpoilsOfBattle),
        typeof(SweepingBeam), typeof(ThinkingAhead),
    ];

    public byte[] SerializeLive(Decision d)
    {
        if (Header[0] != 22) return Serialize();

        int n = NumTokens, m = NumCands;
        var header = (int[])Header.Clone();
        header[0] = 28;

        var tf28 = new float[n * 22];
        var tfSrc = CollectionsMarshal.AsSpan(TF);
        for (int t = 0; t < n; t++)
            tfSrc.Slice(t * TokFeats, TokFeats).CopyTo(tf28.AsSpan(t * 22, TokFeats));

        var cf28 = new float[m * 14];
        var cfSrc = CollectionsMarshal.AsSpan(CF);
        for (int i = 0; i < m; i++)
            cfSrc.Slice(i * CandFeats, CandFeats).CopyTo(cf28.AsSpan(i * 14, CandFeats));

        var state = RunSession.State;
        var me = state?.Players.Count > 0 ? state.Players[0] : null;
        if (me != null && CombatManager.Instance.IsInProgress && me.PlayerCombatState is { } pcs)
        {
            foreach (var c in pcs.Hand.Cards)
            {
                if (!_index.TryGetValue(c, out int idx)) continue;
                try { tf28[idx * 22 + 20] = c.ShouldGlowGold ? 1f : 0f; } catch { }
                try { tf28[idx * 22 + 21] = c.ShouldGlowRed ? 1f : 0f; } catch { }
            }
            for (int i = 0; i < m && i < d.Choices.Count; i++)
            {
                var (g12, g13) = StableCandGains(d.Choices[i], me, CF[i * CandFeats + 7] > 0);
                cf28[i * 14 + 12] = g12;
                cf28[i * 14 + 13] = g13;
            }
        }

        byte[] labelBytes = Labels.Count > 0 ? Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Labels)) : Array.Empty<byte>();
        byte[]? extraBytes = Extra != null ? Encoding.UTF8.GetBytes(Extra) : null;
        int size = 4 * (HeaderInts + TI.Count + tf28.Length + TP.Count + CI.Count + cf28.Length + 1)
            + labelBytes.Length + (extraBytes != null ? 4 + extraBytes.Length : 0);
        var buf = new byte[size];
        int off = 0;
        void Copy<T>(ReadOnlySpan<T> xs) where T : unmanaged
        {
            var bytes = MemoryMarshal.AsBytes(xs);
            bytes.CopyTo(buf.AsSpan(off));
            off += bytes.Length;
        }
        Copy<int>(header);
        Copy<int>(CollectionsMarshal.AsSpan(TI));
        Copy<float>(tf28);
        Copy<float>(CollectionsMarshal.AsSpan(TP));
        Copy<int>(CollectionsMarshal.AsSpan(CI));
        Copy<float>(cf28);
        Copy<int>(stackalloc int[] { labelBytes.Length });
        labelBytes.CopyTo(buf, off);
        off += labelBytes.Length;
        if (extraBytes != null)
        {
            Copy<int>(stackalloc int[] { extraBytes.Length });
            extraBytes.CopyTo(buf, off);
        }
        return buf;
    }

    private static (float energy, float cards) StableCandGains(Choice ch, Player me, bool targetKilled)
    {
        try
        {
            var pcs = me.PlayerCombatState!;
            float en = 0, cards = 0;
            if (ch.Kind == ActionKind.PlayCard && ch.A is CardModel c)
            {
                var tgt = ch.B as Creature;
                int plays = PlayCount(c, tgt);
                float Var(string name) => c.DynamicVars.TryGetValue(name, out var v) ? (float)v.BaseValue : 0;
                if (StableEnergyCards.Contains(c.GetType())) en += Var("Energy") * plays;
                if (StableDrawCards.Contains(c.GetType())) cards += Var("Cards") * plays;
                switch (c)
                {
                    case DoubleEnergy:
                    {
                        int cost = c.EnergyCost.CostsX ? 0 : Math.Max(0, c.EnergyCost.GetWithModifiers(CostModifiers.All));
                        float left = Math.Max(0, pcs.Energy - cost);
                        en += left * (MathF.Pow(2, plays) - 1);
                        break;
                    }
                    case Sunder when tgt != null && targetKilled:
                        en += Var("Energy");
                        break;
                    case Restlessness when pcs.Hand.Cards.Count == 1:
                        en += Var("Energy");
                        cards += Var("Cards");
                        break;
                    case ForgottenRitual when CombatManager.Instance.History.Entries.OfType<CardExhaustedEntry>()
                        .Any(e => e.HappenedThisTurn(me.Creature.CombatState!)):
                        en += Var("Energy");
                        break;
                    case CompileDriver:
                        if (c.DynamicVars.TryGetValue("CalculatedCards", out var cc) && cc is CalculatedVar cv)
                            cards += (float)cv.Calculate(tgt) * plays;
                        break;
                    case DaggerThrow or EscapePlan:
                        cards += plays;
                        break;
                }
                foreach (var f in me.Creature.CombatState!.Creatures)
                    foreach (var wp in f.GetPowerInstances<WitheringPresencePower>())
                        if (wp.DisplayAmount >= 1 && wp.DisplayAmount <= plays)
                            cards += 1 + (plays - wp.DisplayAmount) / 6;
            }
            else if (ch.Kind == ActionKind.UsePotion && ch.A is PotionModel p)
            {
                if (p is EnergyPotion && p.DynamicVars.TryGetValue("Energy", out var ev)) en = (float)ev.BaseValue;
                if (p is SwiftPotion && p.DynamicVars.TryGetValue("Cards", out var cv)) cards = (float)cv.BaseValue;
            }
            else return (0, 0);

            if (me.Creature.HasPower<NoEnergyGainPower>()) en = 0;
            if (me.Creature.HasPower<NoDrawPower>()) cards = 0;
            int handAfter = pcs.Hand.Cards.Count + (ch.Kind == ActionKind.PlayCard ? -1 : 0);
            cards = Math.Min(cards, Math.Max(0, CardPile.MaxCardsInHand - handAfter));
            return (en, cards);
        }
        catch { return (0, 0); }
    }
}
