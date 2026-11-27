using System.Text.Json;
using Sts2Sim.Core.Models;

namespace Sts2Sim.Core.Map;

// Layout already shown to the player. The game can generate a different map from
// sts2-sim on another patch, or after a game-specific map modification.
internal sealed class VisibleActMap : ActMap
{
    protected override MapPoint?[,] Grid { get; }
    public override MapPoint StartingMapPoint { get; }
    public override MapPoint BossMapPoint { get; }
    public override MapPoint? SecondBossMapPoint { get; }

    public VisibleActMap(JsonElement map, ActMap generated)
    {
        int width=map.GetProperty("width").GetInt32(), height=map.GetProperty("height").GetInt32();
        if(width is <1 or >128 || height is <1 or >256) throw new InvalidDataException("Invalid map dimensions");
        Grid=new MapPoint?[width,height];
        var nodes=new Dictionary<MapCoord,MapPoint>();
        var links=new List<(MapPoint from, MapCoord to)>();
        MapCoord Coord(JsonElement c) => new(c.GetProperty("col").GetInt32(),c.GetProperty("row").GetInt32());
        MapPoint Read(JsonElement item)
        {
            var pos=Coord(item.GetProperty("coord"));
            if(nodes.TryGetValue(pos,out var existing)) return existing;
            var point=new MapPoint(pos.col,pos.row) {
                PointType=Enum.Parse<MapPointType>(item.GetProperty("type").GetString()!.Replace("_",""),true),
                CanBeModified=item.TryGetProperty("can_modify",out var mod) && mod.GetBoolean()
            };
            nodes.Add(pos,point);
            if(generated.GetPoint(pos) is { } previous)
                foreach(var quest in previous.Quests) point.AddQuest(quest);
            if(item.TryGetProperty("children",out var children))
                foreach(var child in children.EnumerateArray()) links.Add((point,Coord(child)));
            return point;
        }
        StartingMapPoint=Read(map.GetProperty("start"));
        BossMapPoint=Read(map.GetProperty("boss"));
        if(map.TryGetProperty("second_boss",out var second) && second.ValueKind==JsonValueKind.Object)
            SecondBossMapPoint=Read(second);
        foreach(var item in map.GetProperty("points").EnumerateArray())
        {
            var point=Read(item);
            Grid[point.coord.col,point.coord.row]=point;
        }
        foreach(var (from,to) in links) from.AddChildPoint(nodes[to]);
        foreach(var item in map.GetProperty("start_coords").EnumerateArray()) startMapPoints.Add(nodes[Coord(item)]);
    }

    private VisibleActMap(VisibleActMap source,IReadOnlyDictionary<AbstractModel,AbstractModel> models)
    {
        var (grid,points)=source.ClonePointGraph(models);
        Grid=grid; StartingMapPoint=points[source.StartingMapPoint]; BossMapPoint=points[source.BossMapPoint];
        SecondBossMapPoint=source.SecondBossMapPoint is {} second ? points[second] : null;
        foreach(var point in source.startMapPoints) startMapPoints.Add(points[point]);
    }
    internal override ActMap CloneForRun(IReadOnlyDictionary<AbstractModel,AbstractModel> modelMap,
        Random.Rng? replacementRng=null) => new VisibleActMap(this,modelMap);
}
