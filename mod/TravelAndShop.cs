using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2Headless;

namespace SpireCoach;

internal static partial class LiveBoard
{
    private static string RoomName(MapPoint point)
    {
        string? legend = point.PointType switch
        {
            MapPointType.Monster => "ENEMY", MapPointType.Elite => "ELITE", MapPointType.Shop => "MERCHANT",
            MapPointType.Unknown => "UNKNOWN", MapPointType.RestSite => "REST", MapPointType.Treasure => "TREASURE", _ => null
        };
        return legend != null ? new LocString("map", $"LEGEND_{legend}.title").GetFormattedText() :
            point.PointType switch { MapPointType.Boss => "보스", MapPointType.Ancient => "고대의 존재", _ => "다음 방" };
    }

    private static Decision? MapChoices(RunState state, Player player)
    {
        var points = RunSessionMap.Travelable(state).OrderBy(p => p.coord.col).ToArray();
        if (points.Length == 0) return null;
        var decision = new Decision(DecisionKind.Map);
        for (int i = 0; i < points.Length; i++)
        {
            string position = points.Length switch
            {
                1 => "", 2 => i == 0 ? "왼쪽" : "오른쪽",
                3 => i switch { 0 => "왼쪽", 1 => "가운데", _ => "오른쪽" },
                _ => $"왼쪽에서 {i + 1}번째"
            };
            string room = RoomName(points[i]);
            decision.Add(ActionKind.MapMove, position.Length == 0 ? room : $"{position} · {room}", NoInput, points[i]);
        }
        AddCarriedPotions(decision, player);
        return decision;
    }

    private static Decision? ShopChoices(MerchantInventory inventory, Player player)
    {
        var decision = new Decision(DecisionKind.Shop);
        bool canFreeSlot = false;
        foreach (var entry in inventory.AllEntries)
        {
            if (!entry.IsStocked || entry is MerchantCardRemovalEntry { Used: true }) continue;
            object? item = entry switch
            {
                MerchantCardEntry card => card.CreationResult?.Card,
                MerchantRelicEntry relic => relic.Model,
                MerchantPotionEntry potion => potion.Model,
                MerchantCardRemovalEntry removal => removal,
                _ => null
            };
            // Special shop modes may sell choices the released policy has never encoded.
            if (item == null) return null;
            if (!entry.EnoughGold) decision.Locked.Add((item, entry.Cost));
            if (item is PotionModel && !player.HasOpenPotionSlots)
            {
                decision.Shown.Add((item, Zone.Offer, entry.Cost));
                canFreeSlot |= entry.EnoughGold;
                continue;
            }
            if (!entry.EnoughGold) continue;
            string name = item switch
            {
                CardModel card => NameOf(card), RelicModel relic => relic.Title.GetFormattedText(),
                PotionModel potion => potion.Title.GetFormattedText(), _ => "카드 제거"
            };
            decision.Add(ActionKind.Buy, $"{name} · {entry.Cost} G", NoInput, item, f0: entry.Cost);
        }
        decision.Add(ActionKind.Leave, "상점 나가기", NoInput);
        AddCarriedPotions(decision, player);
        if (canFreeSlot) AddPotionDiscards(decision, player);
        return decision;
    }

    private static void AddPotionDiscards(Decision decision, Player player)
    {
        if (!player.CanUseOrRemovePotions) return;
        for (int i = 0; i < player.PotionSlots.Count; i++)
            if (player.PotionSlots[i] is { IsQueued: false } potion)
                decision.Add(ActionKind.DiscardPotion, $"버리기 · {potion.Title.GetFormattedText()} ({i + 1}번 칸)", NoInput, potion);
    }

    private static void AddCarriedPotions(Decision decision, Player player)
    {
        if (!player.CanUseOrRemovePotions || player.Creature.IsDead) return;
        foreach (var potion in player.Potions.Where(p => !p.IsQueued && p.Usage == PotionUsage.AnyTime && p.PassesCustomUsabilityCheck))
            if (potion.IsValidTarget(player.Creature) || potion.IsValidTarget(null))
                decision.Add(ActionKind.UsePotion, $"사용 · {potion.Title.GetFormattedText()}", NoInput, potion);
    }

    private static string ChoiceDetail(Decision decision, int index, Player player)
    {
        var choice = decision.Choices[index];
        if (decision.Kind == DecisionKind.Rewards && !player.HasOpenPotionSlots && RewardsFlow.Pending is { } pending)
            return "새 포션 · " + string.Join(", ", pending.Set.Rewards.OfType<MegaCrit.Sts2.Core.Rewards.PotionReward>()
                .Where(r => !r.SuccessfullySelected && r.Potion != null).Select(r => r.Potion!.Title.GetFormattedText()));
        if (decision.Kind == DecisionKind.Shop)
            return choice.Kind == ActionKind.Buy ? $"보유 {player.Gold} G\n구매 후 {player.Gold - (int)choice.F0} G" : $"보유 {player.Gold} G";
        if (choice.A is MapPoint point && point.Children.Count > 0)
            return "이 방 다음 · " + string.Join(" / ", point.Children.OrderBy(p => p.coord.col).Select(RoomName).Distinct());
        return "";
    }
}
