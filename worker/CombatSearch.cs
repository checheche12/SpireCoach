using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;

using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2Headless;

namespace SpireCoach;

internal sealed partial class CombatSearch(string models,string vocab) : IDisposable
{
    partial void RecordWorld(uint seed, List<int> actions, int steps, byte[] frame);
    private readonly NativePolicy policy = new(models, 1);
    private RunSession? session;
    private RolloutSearch? rollout;
    private (double[] p,double v) Evaluate(SearchFrame f,int mode=1)
    {
        var actions=policy.Evaluate(position!.Character,f.Bytes,true,out float v,individual:true,mode:mode);
        return (actions.Select(x=>x.TotalProbability).ToArray(),v);
    }
    private SearchPosition? position;
    private HeapSnapshot? snapshot;
    private byte[]? lastSave;
    private byte[]? lastEvents;
    private readonly List<CombatReplayEvent> synced = [];
    private static readonly MethodInfo begin = typeof(RunSession).GetMethod("Begin", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static PacketReader Reader(byte[] bytes) { var r = new PacketReader(); r.Reset(bytes); return r; }

    private static int BuildFormat(byte[] frame)
    {
        int fmt = BinaryPrimitives.ReadInt32LittleEndian(frame);
#if STS_STABLE
        return fmt == 28 ? 22 : fmt;
#else
        return fmt;
#endif
    }

    public bool Prepare(SearchPosition next, Func<bool> cancelled)
    {
        snapshot = null;
        position = null;
        Observation.FormatOverride = BuildFormat(next.Frame);
        var events = Reader(next.Events).ReadList<CombatReplayEvent>();
        bool matches = lastSave != null && lastSave.AsSpan().SequenceEqual(next.Save) && events.Count >= synced.Count;
        if (matches && synced.Count > 0) {
            var prefix = new PacketWriter(); prefix.WriteList(events.Take(synced.Count).ToList());
            matches = prefix.Buffer.AsSpan(0, prefix.BytePosition).SequenceEqual(lastEvents);
        }
        if (!matches)
        {
            session?.Dispose(); session = null; lastSave = null; synced.Clear();
            var saved = Reader(next.Save).Read<SerializableRun>();
            var playerId = saved.Players.Single().NetId;
            saved.Players[0].NetId = 1;
            foreach (var floor in saved.MapPointHistory.SelectMany(a => a))
                foreach (var stats in floor.PlayerStats.Where(st => st.PlayerId == playerId)) stats.PlayerId = 1;
            async Task Load()
            {
                var state = RunState.FromSerializable(saved);
                var manager = RunManager.Instance;
                await manager.SetUpSavedSingleplayer(state, saved);
                manager.Launch();
                await manager.GenerateMap();
                await manager.LoadIntoLatestMapCoord(AbstractRoom.FromSerializable(saved.PreFinishedRoom, state));
            }
            lastSave=null;
            session = (RunSession)begin.Invoke(null, [(Func<Task>)Load, false])!;
            rollout=null;
            if(SearchLimits.Rollout(next.Character) && next.Anchor!=null) {
                rollout=new(next.Anchor,Frame(session.GetDecision()),vocab,Path.Combine(models,"runtime","sim_tables.json"));
                rollout.Initialize();
            }
        }
        // Invalidate the incremental cursor on exceptions or a divergent tape.
        lastSave = null;
        for (int i = synced.Count; i < events.Count; i++) {
            if (cancelled() || !Replay(session!, events[i])) return false;
            synced.Add(events[i]);
        }
        lastSave = next.Save; lastEvents = next.Events;
        Observation.FormatOverride = BuildFormat(next.Frame);
        var root = session!.GetDecision();
        if (root.Kind is not (DecisionKind.Combat or DecisionKind.CardSelect) || root.Choices.Count<2) return false;
        var simulated = Frame(root);
        if (!SamePublicState(simulated, next.Frame) || cancelled()) { lastSave = null; return false; }
        // Load and warm the model before starting a timed request.
        policy.Evaluate(next.Character, simulated, true, out _);
        if (cancelled()) return false;
        if (!SearchLimits.Rollout(next.Character)) snapshot = HeapSnapshot.Capture(session, root);
        position = next;
        return !cancelled();
    }

    private bool Replay(RunSession game, CombatReplayEvent e)
    {
        if (e.eventType == CombatReplayEventType.PlayerChoice && e.playerChoiceResult is { } choice) {
            var decision = game.GetDecision();
            if (decision.Kind != DecisionKind.CardSelect) return false;
            if (choice.combatCards == null && choice.indexes == null) return false;
            var cards = choice.combatCards?.Select(c => c.ToCardModel()).ToList();
            if (cards == null) {
                var options = decision.Choices.Where(c => c.Kind == ActionKind.SelectCard).ToList();
                cards = choice.indexes!.Select(i => (MegaCrit.Sts2.Core.Models.CardModel)options[i].A!).ToList();
            }
            foreach (var card in cards) {
                decision = game.GetDecision();
                int i = decision.Choices.FindIndex(c => c.Kind == ActionKind.SelectCard && c.A == card);
                if (i < 0) return false;
                Choose(game,decision,i);
            }
            decision = game.GetDecision();
            if (decision.Kind == DecisionKind.CardSelect && game.Selector.Pending != null && cards.Count < game.Selector.Pending.Max) {
                int i = decision.Choices.FindIndex(c => c.Kind == ActionKind.ConfirmSelection);
                if (i >= 0) Choose(game,decision,i);
            }
            return true;
        }
        if (e.action == null || e.action is NetReadyToBeginEnemyTurnAction) return true;
        var d = game.GetDecision();
        int index = e.action switch {
            NetPlayCardAction play => d.Choices.FindIndex(c => c.Kind == ActionKind.PlayCard &&
                c.A == play.card.ToCardModel() && (c.B as Creature)?.CombatId == play.targetId),
            NetEndPlayerTurnAction => d.Choices.FindIndex(c => c.Kind == ActionKind.EndTurn),
            NetUsePotionAction potion => d.Choices.FindIndex(c => c.Kind == ActionKind.UsePotion &&
                c.A == RunSession.State.Players[0].PotionSlots[(int)potion.potionIndex] && (c.B as Creature)?.CombatId == potion.targetId),
            _ => -1
        };
        if (index < 0) return false;
        Choose(game,d,index);
        return true;
    }

    private byte[] Frame(Decision d) =>
#if STS_STABLE
        Observation.Build(session!, d, false).SerializeLive(d);
#else
        Observation.Build(session!, d, false).Serialize();
#endif

    internal static bool SamePublicState(byte[] a, byte[] b)
    {
        if (a.Length < 136 || b.Length < 136) return false;
        // Global totals for past combats, historical action counts, and unseen vocab count
        // (Header[12..14]) differ between the in-game adapter and replay. All combat tokens,
        // candidate features and targets must match.
        if (!a.AsSpan(0,48).SequenceEqual(b.AsSpan(0,48)) || !a.AsSpan(60,76).SequenceEqual(b.AsSpan(60,76))) return false;
        int format = BitConverter.ToInt32(a), n = BitConverter.ToInt32(a,8), m = BitConverter.ToInt32(a,12);
        int tokens = checked(n * (5 + (format >= 27 ? 22 : 20)) * 4);
        int candidates = checked(m * (5 + (format >= 26 ? 14 : 12)) * 4);
        int offset = checked(136 + tokens + n * 8);
        return a.Length >= offset+candidates && b.Length >= offset+candidates &&
            a.AsSpan(136,tokens).SequenceEqual(b.AsSpan(136,tokens)) &&
            a.AsSpan(offset,candidates).SequenceEqual(b.AsSpan(offset,candidates));
    }

    private void Choose(RunSession game,Decision d,int action)
    {
        if(rollout!=null && !rollout.Follow(new(Frame(d)),action)) rollout=null;
        game.Choose(d,action);
    }

    public SearchReply Search(int id,int budgetMs,Func<bool> cancelled)
    {
        var pos=position!;
        var root=new SearchFrame(pos.Frame);
        var clock=Stopwatch.StartNew();
        void CheckTime() { if(cancelled() || clock.ElapsedMilliseconds>=budgetMs) throw new OperationCanceledException(); }
        var game=session!;
        (SearchFrame,int) Simulate(uint seed,List<int> path)
        {
            RestoreAndReseed(seed);
            var d=game.GetDecision();
            int steps=0;
            foreach(int action in path) {
                CheckTime();
                game.Choose(d,action); steps++;
                d=game.GetDecision();
                while(d.Kind is not (DecisionKind.GameOver or DecisionKind.Stuck) && d.Choices.Count==1) {
                    CheckTime(); game.Choose(d,0); steps++; d=game.GetDecision();
                }
            }
            var frame=Frame(d);
            RecordWorld(seed,path,steps,frame);
            return (new(frame),steps);
        }
        try {
            var (p,_)=Evaluate(root);
            if(p.Max()>=.9 || p.Count(v=>v>=.02)<2) return new(id,"confident");
            if(SearchLimits.Rollout(pos.Character))
                return rollout?.Decide(id,root,p,pos.Seed,pos.Character=="DEFECT",Evaluate,CheckTime) ?? new(id,"unmatched");
            return new InformationSetTree(Simulate,f=>Evaluate(f),CheckTime,policy.VictoryValue,pos.FirstActThreeBoss,
                    pos.Character=="NECROBINDER" ? .5 : .1)
                .Decide(id,root,p,pos.Seed);
        } catch(OperationCanceledException) { return new(id,cancelled() ? "cancelled" : "expired"); }
          finally { snapshot?.Restore(); snapshot=null; }
    }

    private void RestoreAndReseed(uint seed)
    {
        snapshot!.Restore();
        RunSession.LoopCut=true; RunSession.StallTurns=20;
        RunSession.Reseed(seed);
#if STS_STABLE
        ReseedStableRewards(seed);
#else
        RunSession.ReseedRewards(seed);
#endif
    }

    public void Dispose() { policy.Dispose(); session?.Dispose(); }
#if STS_STABLE
    // Backport the upstream reward RNG resampling to the older stable search engine.
    private static void ReseedStableRewards(uint seed)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var player = RunSession.State.Players[0];
        var rngs = (Dictionary<MegaCrit.Sts2.Core.Entities.Rngs.PlayerRngType,MegaCrit.Sts2.Core.Random.Rng>)
            player.PlayerRng.GetType().GetField("_rngs",flags)!.GetValue(player.PlayerRng)!;
        foreach (var key in rngs.Keys.ToArray())
            rngs[key]=new(unchecked((ulong)seed*0x9E3779B97F4A7C15UL+(ulong)(int)key*0x632BE59BD9B4E019UL+0x5EEDUL));
        var rewards=rngs[MegaCrit.Sts2.Core.Entities.Rngs.PlayerRngType.Rewards];
        var field=typeof(MegaCrit.Sts2.Core.Odds.AbstractOdds).GetField("_rng",flags)!;
        field.SetValue(player.PlayerOdds.CardRarity,rewards); field.SetValue(player.PlayerOdds.PotionReward,rewards);
        var bags=(Dictionary<MegaCrit.Sts2.Core.Entities.Relics.RelicRarity,List<MegaCrit.Sts2.Core.Models.RelicModel>>)
            player.RelicGrabBag.GetType().GetField("_deques",flags)!.GetValue(player.RelicGrabBag)!;
        uint group=0;
        foreach (var kv in bags.OrderBy(kv=>(int)kv.Key))
            new MegaCrit.Sts2.Core.Random.Rng(unchecked((ulong)seed^(0xA5A5A5A5UL+7919UL*++group))).Shuffle(kv.Value);
    }
#endif
}
