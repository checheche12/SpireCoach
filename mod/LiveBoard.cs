using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Sts2Headless;
using Sts2CardReward = MegaCrit.Sts2.Core.Rewards.CardReward;

namespace SpireCoach;

internal sealed record Capture(string Character, byte[] Frame, string[] Names, string[] Details, DecisionKind Kind, bool IsUpgrade, string Screen, ActionKind[] Actions, string?[] Portraits, string?[] CardPositions)
{
    internal string NameAt(int index, bool numbered) => numbered && CardPositions[index] is { } position
        ? $"{position} · {Names[index]}" : Names[index];
}

internal static partial class LiveBoard
{
    private const BindingFlags PrivateField = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo Offers = Field(typeof(NCardRewardSelectionScreen), "_options");
    private static readonly FieldInfo Alternatives = Field(typeof(NCardRewardSelectionScreen), "_extraOptions");
    private static readonly FieldInfo RewardSet = Field(typeof(NRewardsScreen), "_rewardsSet");
    private static readonly FieldInfo SkippedRewards = Field(typeof(NRewardsScreen), "_skippedRewardButtons");
    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, PrivateField) ?? throw new MissingFieldException(type.FullName, name);

    private static readonly Action NoInput = () => { };
    private static string NameOf(CardModel card)
    {
        if (card.Enchantment is not { } enchantment) return card.Title;
        string name = enchantment.Title.GetFormattedText();
        string amount = enchantment.ShowAmount ? $" {enchantment.DisplayAmount}" : "";
        return $"{card.Title} [{name}{amount}]";
    }
    private static string NameOf(Creature? target)
    {
        if (target == null) return "";
        if (target.IsPlayer) return " → 자신";
        if (!target.IsEnemy) return " → " + target.Name;
        var enemies = target.CombatState!.Enemies.ToList();
        return $" → {target.Name} ({enemies.IndexOf(target) + 1})";
    }

    private static Decision Combat(Player me)
    {
        var d = new Decision(DecisionKind.Combat);
        var targets = new List<Creature?> { null };
        targets.AddRange(me.Creature.CombatState!.Creatures.Where(c => c.IsAlive));
        foreach (var card in me.PlayerCombatState!.Hand.Cards)
        {
            if (!card.CanPlay(out _, out _)) continue;
            foreach (var target in targets.Where(card.CanPlayTargeting))
                d.Add(ActionKind.PlayCard, NameOf(card) + NameOf(target), NoInput, card, target);
        }
        if (me.CanUseOrRemovePotions)
        {
            foreach (var potion in me.Potions.Where(p => !p.IsQueued && p.PassesCustomUsabilityCheck &&
                         p.Usage is PotionUsage.AnyTime or PotionUsage.CombatOnly))
                foreach (var target in targets.Where(potion.IsValidTarget))
                    d.Add(ActionKind.UsePotion, "포션 · " + potion.Title.GetFormattedText() + NameOf(target), NoInput, potion, target);
        }
        return d.Add(ActionKind.EndTurn, "턴 종료", NoInput);
    }

    private static Decision? CardReward(NCardRewardSelectionScreen screen)
    {
        if (Offers.GetValue(screen) is not IReadOnlyList<CardCreationResult> cards || cards.Count == 0) return null;
        if (Alternatives.GetValue(screen) is not IReadOnlyList<CardRewardAlternative> alternatives) return null;
        var d = new Decision(DecisionKind.CardReward);
        foreach (var card in cards)
            d.Add(ActionKind.PickCardReward, NameOf(card.Card), NoInput, card.Card);
        foreach (var alt in alternatives)
            d.Add(ActionKind.CardRewardAlt, alt.Title.GetFormattedText(), NoInput, key: Vocab.Alt(alt.OptionId));

        var rewardsScreen = NOverlayStack.Instance?.GetChildren().OfType<NRewardsScreen>().LastOrDefault();
        if (rewardsScreen != null && RewardSet.GetValue(rewardsScreen) is RewardsSet set)
        {
            var current = set.Rewards.OfType<Sts2CardReward>().FirstOrDefault(r => r.Cards.Any(c => ReferenceEquals(c, cards[0].Card)));
            if (current != null)
            {
                RewardsFlow.ActiveSet = set;
                RewardsFlow.ActiveChoice = current;
                foreach (var other in RewardsFlow.Unclaimed(set, null, current))
                {
                    d.Shown.Add((other, Zone.Offer, 0));
                    if (other is Sts2CardReward cr)
                        foreach (var c in cr.Cards) d.Shown.Add((c, Observation.RewardZone(set, cr), 0));
                }
            }
        }
        return d;
    }

    private static Decision? PotionRewards(NRewardsScreen screen, Player player)
    {
        if (RewardSet.GetValue(screen) is not RewardsSet set || !screen.IsNodeReady()) return null;
        // Failed attempts to take a potion while full leave it on the screen.
        var skipped = ((IEnumerable<Control>)SkippedRewards.GetValue(screen)!).OfType<NRewardButton>()
            .Select(b => b.Reward).OfType<Sts2CardReward>().ToHashSet();
        var remaining = set.Rewards.Where(r => !r.SuccessfullySelected && !(r is Sts2CardReward c && skipped.Contains(c))).ToList();
        var offers = remaining.OfType<PotionReward>().Where(r => r.Potion != null).ToList();
        if (offers.Count == 0) return null;
        bool full = !player.HasOpenPotionSlots;
        var available = remaining.Where(r => r is not PotionReward || !full).ToList();
        RewardsFlow.Pending = new(set, available);
        var decision = new Decision(DecisionKind.Rewards);
        foreach (var reward in available)
        {
            string label = reward switch
            {
                PotionReward p => "받기 · " + p.Potion!.Title.GetFormattedText(),
                GoldReward g => $"골드 {g.Amount} 받기",
                RelicReward r when r.Relic != null => "받기 · " + r.Relic.Title.GetFormattedText(),
                Sts2CardReward => "카드 보상 보기",
                _ => reward.Description.GetFormattedText()
            };
            decision.Add(ActionKind.TakeReward, label, NoInput, reward);
            if (reward is Sts2CardReward cr)
                foreach (var card in cr.Cards) decision.Shown.Add((card, Observation.RewardZone(set, cr), 0));
        }
        if (screen.GetNodeOrNull<NProceedButton>("ProceedButton") is { IsEnabled: true } proceed && proceed.IsVisibleInTree())
            decision.Add(ActionKind.Leave, remaining.Count == offers.Count ? "새 포션 두고 가기" : "남은 보상 두고 가기", NoInput);
        AddCarriedPotions(decision, player);
        if (full)
        {
            AddPotionDiscards(decision, player);
            foreach (var offer in offers) decision.Shown.Add((offer, Zone.Offer, 0));
        }
        return decision;
    }

    private static Decision? RestChoices(NRestSiteRoom room, Player player)
    {
        var options = room.Options.Where(option => option.IsEnabled &&
            room.GetButtonForOption(option) is { IsEnabled: true } button && button.IsVisibleInTree()
            && !button.IsQueuedForDeletion() && button.MouseFilter != Control.MouseFilterEnum.Ignore).ToList();
        if (options.Count == 0) return null;
        // AlphaDefect masks a rest with no benefit when smithing is available.
        if (options.Any(o => o is SmithRestSiteOption) && options.Any(o => o is HealRestSiteOption)
            && RunSession.RestHealInfo(player) is { actual: 0, extra: 0 })
            options.RemoveAll(o => o is HealRestSiteOption);
        var decision = new Decision(DecisionKind.RestSite);
        foreach (var option in options)
            decision.Add(ActionKind.RestOption, option.Title.GetFormattedText(), NoInput, option);
        if (room.ProceedButton.IsEnabled && room.ProceedButton.IsVisibleInTree())
            decision.Add(ActionKind.Leave, "떠나기", NoInput);
        AddCarriedPotions(decision, player);
        return decision;
    }

    private static Decision? EventChoices(NEventRoom room, Player me)
    {
        if (room.Layout is not { } layout || !layout.IsVisibleInTree()
            || RunManager.Instance.EventSynchronizer.GetLocalEvent() is not { IsFinished: false } ev) return null;
        if (layout is NAncientEventLayout && layout.GetNodeOrNull<Control>("%DialogueHitbox")?.IsVisibleInTree() == true) return null;
        var decision = new Decision(DecisionKind.Event) { Context = ev.Id.Entry };
        foreach (var button in layout.OptionButtons)
        {
            var option = button.Option;
            if (!button.IsNodeReady() || !button.IsVisibleInTree() || !button.IsEnabled || option.IsLocked
                || !ev.CurrentOptions.Contains(option)) continue;
            if (button.MouseFilter == Control.MouseFilterEnum.Ignore) return null;
            // The released policy excludes the run-abandon confirmation in Trial.
            if (option.TextKey == "TRIAL.pages.REJECT.options.DOUBLE_DOWN") continue;
            var label = ev is AncientEventModel && option.Relic != null ? option.Relic.Title.GetFormattedText()
                : button.GetNodeOrNull<RichTextLabel>("%Text")?.GetParsedText().Split('\n')[0];
            if (string.IsNullOrWhiteSpace(label)) label = option.Title.GetFormattedText();
            if (string.IsNullOrWhiteSpace(label)) label = option.Description.GetFormattedText();
            object? tip = option.HoverTips?.Select(t => t switch
            {
                CardHoverTip card => (object?)card.Card, HoverTip other => other.CanonicalModel, _ => null
            }).FirstOrDefault(model => model is CardModel or PotionModel || model is RelicModel relic && relic.Id != option.Relic?.Id);
            decision.Add(ActionKind.EventOption, label, NoInput, option, tip);
        }
        if (decision.Choices.Count == 0) return null;
        AddCarriedPotions(decision, me);
        return decision;
    }

    internal static Capture? Read() => ReadEnabled(null);

    internal static Capture? ReadEnabled(Func<DecisionKind, bool>? enabled)
    {
        if (RunManager.Instance?.IsInProgress != true) return null;
        var state = RunManager.Instance.DebugOnlyGetState();
        if (state?.Players.Count != 1 || state.IsGameOver) return null;
        var me = state.Players[0];
        var character = me.Character.Id.Entry;
        int format = character switch
        {
            "DEFECT" => 19, "REGENT" => 20, "SILENT" => 23,
#if STS_STABLE
            "IRONCLAD" or "NECROBINDER" => 22,
#else
            "IRONCLAD" or "NECROBINDER" => 28, "FOOL" => 27,
#endif
            _ => 0
        };
        if (format == 0) return null;
        if (NModalContainer.Instance?.OpenModal != null) return null;
        var overlay = NOverlayStack.Instance?.Peek();
        var active = ActiveScreenContext.Instance.GetCurrentScreen();
        var hand = NPlayerHand.Instance;
        var session = new RunSession();
        Decision? decision;
        RewardsFlow.ActiveSet = null;
        RewardsFlow.ActiveChoice = null;
        RewardsFlow.Pending = null;
        if (NMapScreen.Instance?.IsOpen == true)
        {
            if (active is not NMapScreen { IsTravelEnabled: true, IsTraveling: false }) return null;
            decision = MapChoices(state, me);
        }
        else if (active is NMerchantInventory { IsOpen: true, Inventory: { } inventory } shop && shop.IsVisibleInTree())
            decision = ShopChoices(inventory, me);
        else if (active is NCardRewardSelectionScreen screen && screen.IsVisibleInTree()) decision = CardReward(screen);
        else if (active is NRewardsScreen rewards && rewards.IsVisibleInTree() && enabled?.Invoke(DecisionKind.Rewards) != false)
            decision = PotionRewards(rewards, me);
        else if (active is NCardGridSelectionScreen grid && grid.IsVisibleInTree())
        {
            if (enabled?.Invoke(DecisionKind.CardSelect) == false) return null;
            decision = GridChoices(grid, session);
        }
        else if (active is NChooseACardSelectionScreen generated)
        {
            if (enabled?.Invoke(DecisionKind.CardSelect) == false) return null;
            decision = GeneratedChoices(generated, session);
        }
        else if (overlay == null && CombatManager.Instance.IsInProgress && hand is { IsInCardSelection: true })
        {
            if (enabled?.Invoke(DecisionKind.CardSelect) == false) return null;
            decision = HandChoices(hand, me, session);
        }
        else if (active is NEventRoom room && !CombatManager.Instance.IsInProgress && room.IsVisibleInTree())
            decision = EventChoices(room, me);
        else if (active is NRestSiteRoom rest && rest.IsNodeReady() && rest.IsVisibleInTree() && enabled?.Invoke(DecisionKind.RestSite) != false)
            decision = RestChoices(rest, me);
        else if (overlay == null && CombatManager.Instance.IsInProgress && me.PlayerCombatState?.Phase == PlayerTurnPhase.Play
            && me.Creature.CombatState?.CurrentSide == CombatSide.Player && !me.Creature.IsDead
            && NPlayerHand.Instance?.IsInCardSelection != true && !RunManager.Instance.ActionExecutor.IsRunning)
            decision = Combat(me);
        else return null;
        if (decision == null || decision.Choices.Count == 0 || enabled?.Invoke(decision.Kind) == false) return null;
        bool combat = decision.Kind == DecisionKind.Combat;
        int entered = state.MapPointHistory.SelectMany(act => act).SelectMany(e => e.Rooms)
            .Count(r => r.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss);
        UiFlows.CombatsWon = Math.Max(0, entered - (CombatManager.Instance.IsInProgress ? 1 : 0));
        Observation.FormatOverride = format;
        var obs = Observation.Build(session, decision, labels: false);
        var details = new string[obs.NumCands];
        for (int i = 0; i < details.Length; i++)
        {
            if (!combat) { details[i] = ChoiceDetail(decision, i, me); continue; }
            int j = i * (obs.CF.Count / obs.NumCands);
            var facts = new List<string>();
            if (obs.CF[j + 6] > 0) facts.Add($"피해 {obs.CF[j + 6]:0.#}");
            if (obs.CF[j + 8] > 0) facts.Add($"방어도 +{obs.CF[j + 8]:0.#}");
            var loss = $"행동 후 턴 종료 시 예상 체력 손실 {obs.CF[j + 10]:0.#}";
            details[i] = facts.Count == 0 ? loss : string.Join(" · ", facts) + "\n" + loss;
        }
        string heading = decision.Kind switch { DecisionKind.Map => "지도", DecisionKind.Shop => "상점", DecisionKind.Event => "이벤트", DecisionKind.RestSite => "모닥불", DecisionKind.Rewards => "포션 보상", DecisionKind.Combat => "전투", DecisionKind.CardSelect => "카드 선택", _ => "카드 보상" };
        bool upgrade = decision.Kind == DecisionKind.CardSelect && (active is NDeckUpgradeSelectScreen || (overlay == null && hand?.CurrentMode == NPlayerHand.Mode.UpgradeSelect));
#if STS_STABLE
        var frame = obs.SerializeLive(decision);
#else
        var frame = obs.Serialize();
#endif
        return new(character, frame, CardLabels(obs, decision), details, decision.Kind, upgrade, heading,
            decision.Choices.Select(c => c.Kind).ToArray(),
            decision.Choices.Select(c => c.A switch { CardModel card => card.PortraitPath, RelicModel relic => relic.IconPath,
                PotionModel potion => potion.ImagePath, EventOption option => option.Relic?.IconPath,
                RestSiteOption option => option.Icon?.ResourcePath, _ => null }).ToArray(),
            CardPositions());

        string?[] CardPositions()
        {
            IEnumerable<CardModel?> cards;
            string pile = "카드";
            if (active is NCardGridSelectionScreen grid && grid.GetNodeOrNull<NCardGrid>("%CardGrid") is { } layout)
                cards = layout.CurrentlyDisplayedCards;
            else if (active is NChooseACardSelectionScreen generated)
                cards = (IReadOnlyList<CardModel>)Field(generated.GetType(), "_cards").GetValue(generated)!;
            else if (active is NCardRewardSelectionScreen reward && Offers.GetValue(reward) is IReadOnlyList<CardCreationResult> offers)
                cards = offers.Select(c => c.Card);
            else if (active is NMerchantInventory { Inventory: { } inventory })
                cards = inventory.CharacterCardEntries.Concat(inventory.ColorlessCardEntries).Select(e => e.CreationResult?.Card);
            else if (hand != null && (combat || (decision.Kind == DecisionKind.CardSelect && hand.IsInCardSelection)))
            {
                cards = hand.ActiveHolders.Select(h => h.CardNode?.Model);
                pile = "손패";
            }
            else return new string?[decision.Choices.Count];
            var order = cards.ToArray();
            return decision.Choices.Select(c => {
                int index = c.A is CardModel card ? Array.FindIndex(order, item => ReferenceEquals(item, card)) : -1;
                return index >= 0 ? $"{pile} {index + 1}번" : null;
            }).ToArray();
        }
    }

    private static string[] CardLabels(Observation obs, Decision decision)
    {
        var names = decision.Choices.Select(c => c.Label).ToArray();
        if (decision.Kind is not (DecisionKind.Combat or DecisionKind.CardSelect)) return names;
        int width = obs.TF.Count / obs.NumTokens;
        int Token(int candidate) => obs.CI[candidate * Observation.CandInts + 1];
        int Feature(int t, int column) => (int)obs.TF[t * width + column];
        (int, int) Identity(int t) => (obs.TI[t * Observation.TokInts + 1], Feature(t, 3));
        (int energy, int stars) Cost(int t) => (Feature(t, 1) == 1 ? -1 : Math.Max(0, Feature(t, 0)),
            Feature(t, 1) == 2 ? -1 : Math.Max(0, Feature(t, 2)));
        var candidates = Enumerable.Range(0, names.Length)
            .Where(i => decision.Choices[i].A is CardModel && Token(i) >= 0).ToArray();
        // Include unplayable hand copies too. The only playable copy might be the discounted one.
        var hand = Enumerable.Range(0, obs.NumTokens).Where(t =>
            obs.TI[t * Observation.TokInts] == (int)TokenKind.Card && obs.TI[t * Observation.TokInts + 2] == (int)Zone.Hand);
        var different = candidates.Select(Token).Concat(hand).Distinct().GroupBy(Identity)
            .Select(g => (g.Key, costs: g.Select(Cost).Distinct().ToArray())).Where(g => g.costs.Length > 1)
            .ToDictionary(g => g.Key, g => g.costs.Any(c => c.stars != 0));
        foreach (int i in candidates)
        {
            int t = Token(i);
            var choice = decision.Choices[i];
            var card = (CardModel)choice.A!;
            var parts = new List<string>();
            if (different.TryGetValue(Identity(t), out bool showStars))
            {
                var (energy, stars) = Cost(t);
                string Number(int value) => value == -1 ? "X" : value.ToString();
                parts.Add(Number(energy) + "코스트");
                if (showStars) parts.Add("별 " + Number(stars));
            }
            var keywords = card.Keywords;
            static string Title(CardKeyword k) => new MegaCrit.Sts2.Core.Localization.LocString("card_keywords",
                MegaCrit.Sts2.Core.Helpers.StringHelper.Slugify(k.ToString()) + ".title").GetFormattedText();
            if (card.ShouldRetainThisTurn && !keywords.Contains(CardKeyword.Retain)) parts.Add("이번 턴 " + Title(CardKeyword.Retain));
            if (card.IsSlyThisTurn && !keywords.Contains(CardKeyword.Sly)) parts.Add("이번 턴 " + Title(CardKeyword.Sly));
            if (card.ExhaustOnNextPlay && !keywords.Contains(CardKeyword.Exhaust)) parts.Add("다음 사용 시 " + Title(CardKeyword.Exhaust));
            foreach (var k in keywords.Except(card.CanonicalKeywords).Order()) parts.Add(Title(k));
            foreach (var k in card.CanonicalKeywords.Except(keywords).Order()) parts.Add(Title(k) + " 해제");
            if (card.Affliction is { } affliction) parts.Add(affliction.Title.GetFormattedText() + $" {affliction.Amount}");
            if (parts.Count == 0) continue;
            names[i] = NameOf(card) + $" ({string.Join(", ", parts)})" + NameOf(choice.B as Creature);
        }
        return names;
    }
}
