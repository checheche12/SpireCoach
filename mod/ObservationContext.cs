using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Headless;

// Supply the live run context expected by the upstream encoder.
public sealed class RunSession
{
    public static RunState State => RunManager.Instance.DebugOnlyGetState()!;
    public bool CombatOnly => false;
    public bool IsVictory => false;
    public int Steps { get; set; }
    public PendingCardChoice? Selecting { get; set; }
    public IReadOnlyList<int> SelectedIndices { get; set; } = Array.Empty<int>();
    internal static (int actual, int nominal, int extra) RestHealInfo(Player player)
    {
        var creature = player.Creature;
        int nominal = (int)HealRestSiteOption.GetHealAmount(player);
        int actual = Math.Clamp(nominal, 0, Math.Max(0, creature.MaxHp - creature.CurrentHp));
        int extra = 0;
        foreach (var listener in player.RunState.IterateHookListeners(null))
        {
            int count = listener.ModifyExtraRestSiteHealText(player, Array.Empty<LocString>()).Count;
            if (count > 0 && listener.ModifyRestSiteHealAmount(creature, 100m) == 100m) extra += count;
        }
        return (actual, nominal, extra);
    }
}

public static class RunSessionMap
{
    public static IEnumerable<MapPoint> Travelable(RunState s)
    {
        var map = s.Map;
        if (s.VisitedMapCoords.Count == 0) return [map.StartingMapPoint];
        var last = s.VisitedMapCoords[^1];
        if (map.SecondBossMapPoint != null && last == map.BossMapPoint.coord) return [map.SecondBossMapPoint];
        if (last.row == map.GetRowCount() - 1) return [map.BossMapPoint];
        var point = map.GetPoint(last);
        return point == null ? [map.StartingMapPoint] : MapTravel.GetTravelablePointsFrom(s, point);
    }
}

internal static class FakeScreens
{
    internal static CrystalSphereMinigame? CrystalSphere => null;
}
internal static class UiFlows
{
    public static int CombatsWon { get; set; }
}
internal sealed record PendingRewards(RewardsSet Set, List<Reward> Available);
internal static class RewardsFlow
{
    internal static PendingRewards? Pending { get; set; }
    internal static RewardsSet? ActiveSet { get; set; }
    internal static Reward? ActiveChoice { get; set; }
    internal static IReadOnlySet<Reward>? ActiveAttempted => null;
    internal static IEnumerable<Reward> Unclaimed(RewardsSet set, IReadOnlySet<Reward>? attempted, Reward? except) =>
        set.Rewards.Where(r => !r.SuccessfullySelected && r != except && (attempted == null || !attempted.Contains(r)));
}
