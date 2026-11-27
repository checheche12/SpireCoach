using System.Text.Json;
namespace SpireCoach;

internal sealed partial class CombatSearch
{
    partial void RecordWorld(uint seed,List<int> actions,int steps,byte[] frame)
    {
        var path=Environment.GetEnvironmentVariable("COACH_QA_WORLD_TRACE");
        if(path!=null) File.AppendAllText(path,JsonSerializer.Serialize(new {seed,actions,steps,frame})+"\n");
    }
}

internal sealed partial class RolloutSearch
{
    partial void RecordRollout(int index, uint seed, double score)
    {
        var path=Environment.GetEnvironmentVariable("COACH_QA_WORLD_TRACE");
        if(path!=null) File.AppendAllText(path,JsonSerializer.Serialize(new {index,seed,score})+"\n");
    }
}
