using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpireCoach;

// sts2-sim imports the map save from BEFORE entering the combat. The ordinary
// combat replay begins later, after room generation has consumed random draws.
internal static class CombatAnchor
{
    internal static MapAnchor? Current { get; private set; }
    internal static void Install()
    {
        var method=AccessTools.Method(typeof(RunManager), nameof(RunManager.EnterMapCoord), [typeof(MapCoord)]);
        new Harmony("spirecoach.search.map").Patch(method, prefix:new HarmonyMethod(typeof(CombatAnchor),nameof(BeforeTravel)));
    }
    private static void BeforeTravel(RunManager __instance,MapCoord __0)
    {
        Current=null;
        try {
            var state=__instance.DebugOnlyGetState();
            if(state==null || state.CurrentActIndex!=2 || state.Players.Count!=1 ||
               (state.Map.BossMapPoint.coord!=__0 && state.Map.SecondBossMapPoint?.coord!=__0)) return;
            var save=__instance.ToSave(null);
            var json=JsonSerializer.SerializeToNode(save,JsonSerializationUtility.GetTypeInfo(save))!.AsObject();
            // The game omits empty histories. The v0.111 simulator expects arrays.
            json["map_point_history"] ??= new JsonArray();
            json["visited_map_coords"] ??= new JsonArray();
            // Added in v0.113 for out-of-combat generation, unused in combat rollouts.
            json["players"]![0]!["rng"]!["rngs"]!.AsObject().Remove("non_combat_generation");
            if (json["acts"]![2]!["saved_map"] is {} visibleMap)
                json["extra_fields"]!["spirecoach_visible_map"]=visibleMap.DeepClone();
            Current=new(json.ToJsonString(),state.NextRoomId,__0.col,__0.row,"boss");
        } catch(Exception ex) { Godot.GD.PushWarning("[SpireCoach] Map save: "+ex.GetBaseException().Message); }
    }
}
