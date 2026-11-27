using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using Sts2Headless;

namespace SpireCoach;

internal static partial class LiveBoard
{
    // The hand keeps the source only in its async state machine after opening the prompt.
    private static WeakReference<AbstractModel>? handSource;
    internal static void ObserveHandSelections()
    {
        new Harmony("SpireCoach.HandSelection").Patch(
            AccessTools.Method(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards)),
            prefix: new HarmonyMethod(typeof(LiveBoard), nameof(HandSelectionStarted)));
    }

    private static void HandSelectionStarted(AbstractModel? source) =>
        handSource = source == null ? null : new(source);

    private static Decision? HandChoices(NPlayerHand hand, Player player, RunSession session)
    {
        if (!hand.IsVisibleInTree() || hand.PeekButton.IsPeeking || player.PlayerCombatState == null) return null;
        var type = typeof(NPlayerHand);
        var prefs = (CardSelectorPrefs)Field(type, "_prefs").GetValue(hand)!;
        var selected = (List<CardModel>)Field(type, "_selectedCards").GetValue(hand)!;
        var visible = hand.ActiveHolders.Select(holder => holder.CardNode?.Model).OfType<CardModel>().ToHashSet();
        var cards = player.PlayerCombatState.Hand.Cards.Where(c => visible.Contains(c) || selected.Contains(c)).ToArray();
        bool upgrade = hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect;
        var source = handSource != null && handSource.TryGetTarget(out var model) ? model : null;
        var prompt = upgrade ? CardSelectorPrefs.UpgradeSelectionPrompt : prefs.Prompt;
        var info = new SelectInfo(prompt?.LocTable, prompt?.LocEntryKey,
            upgrade ? "FromHandForUpgrade" : "FromHand", null, source);
        var confirm = (NConfirmButton)Field(type, "_selectModeConfirmButton").GetValue(hand)!;
        return Selection(session, cards, selected, prefs.MinSelect, prefs.MaxSelect, prefs.Cancelable, info,
            confirm.IsEnabled && confirm.IsVisibleInTree(), upgrade: upgrade);
    }

    private static Decision? GeneratedChoices(NChooseACardSelectionScreen screen, RunSession session)
    {
        var type = typeof(NChooseACardSelectionScreen);
        if (!screen.IsNodeReady() || screen.IsQueuedForDeletion() || !screen.IsVisibleInTree()
            || screen.GetNodeOrNull<NPeekButton>("%PeekButton")?.IsPeeking == true
            || (bool)Field(type, "_screenComplete").GetValue(screen)!) return null;
        var cards = (IReadOnlyList<CardModel>)Field(type, "_cards").GetValue(screen)!;
        bool skip = (bool)Field(type, "_canSkip").GetValue(screen)!;
        var button = screen.GetNodeOrNull<NChoiceSelectionSkipButton>("SkipButton");
        return Selection(session, cards, [], skip ? 0 : 1, 1, false, new(null, null, "FromChooseACardScreen", null),
            skip && button is { IsEnabled: true } && button.IsVisibleInTree());
    }

    private static Decision? GridChoices(NCardGridSelectionScreen screen, RunSession session)
    {
        string? method = screen switch
        {
            NDeckUpgradeSelectScreen => "FromDeckForUpgrade",
            NDeckEnchantSelectScreen => "FromDeckForEnchantment",
            NDeckTransformSelectScreen => "FromDeckForTransformation",
            NDeckCardSelectScreen => "FromDeckGeneric",
            NCombatPileCardSelectScreen => "FromCombatPile",
            NSimpleCardSelectScreen => "FromSimpleGrid",
            _ => null
        };
        if (method == null || !screen.IsNodeReady() || screen.IsQueuedForDeletion()) return null;
        var grid = screen.GetNodeOrNull<NCardGrid>("%CardGrid");
        if (grid?.IsVisibleInTree() != true || grid.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Disabled
            || screen.GetNodeOrNull<NPeekButton>("%PeekButton")?.IsPeeking == true) return null;
        IReadOnlyList<CardModel> cards = screen is NCombatPileCardSelectScreen ? grid.CurrentlyDisplayedCards.ToArray()
            : (IReadOnlyList<CardModel>)Field(typeof(NCardGridSelectionScreen), "_cards").GetValue(screen)!;
        var type = screen.GetType();
        var prefs = (CardSelectorPrefs)Field(type, "_prefs").GetValue(screen)!;
        var selected = (HashSet<CardModel>)Field(type, "_selectedCards").GetValue(screen)!;
        var enchantment = screen is NDeckEnchantSelectScreen ? (EnchantmentModel?)Field(type, "_enchantment").GetValue(screen) : null;
        var info = new SelectInfo(prefs.Prompt?.LocTable, prefs.Prompt?.LocEntryKey, method, enchantment);
        var confirm = screen.GetNodeOrNull<NConfirmButton>("%Confirm") ?? screen.GetNodeOrNull<NConfirmButton>("Confirm");
        var close = screen.GetNodeOrNull<NBackButton>("%Close");
        bool upgrade = screen is NDeckUpgradeSelectScreen;
        return Selection(session, cards, selected, prefs.MinSelect, prefs.MaxSelect, prefs.Cancelable, info,
            confirm is { IsEnabled: true } && confirm.IsVisibleInTree(),
            !upgrade && prefs.Cancelable && close is { IsEnabled: true } && close.IsVisibleInTree(), upgrade);
    }

    private static Decision? Selection(RunSession session, IReadOnlyList<CardModel> cards, IReadOnlyCollection<CardModel> selected,
        int min, int max, bool cancelable, SelectInfo info, bool canConfirm, bool canCancel = false, bool upgrade = false)
    {
        if (cards.Count == 0) return null;
        session.Selecting = new PendingCardChoice(cards, min, max, cancelable, info);
        session.SelectedIndices = Enumerable.Range(0, cards.Count).Where(i => selected.Contains(cards[i])).ToArray();
        var decision = new Decision(DecisionKind.CardSelect) { Selected = selected.Count };
        if (selected.Count < max)
            foreach (var card in cards)
                if (!selected.Contains(card))
                    decision.Add(ActionKind.SelectCard, NameOf(card), NoInput, card, upgrade ? Observation.UpgradedCopy(card) : null);
        if (canConfirm && selected.Count >= Math.Min(min, cards.Count))
            decision.Add(ActionKind.ConfirmSelection, selected.Count == 0 ? "건너뛰기" : "선택 완료", NoInput);
        if (canCancel && selected.Count == 0) decision.Add(ActionKind.Leave, "취소", NoInput);
        return decision;
    }
}
