namespace SpireCoach;

internal sealed record MapAnchor(string SaveJson,int NextRoom,int Col,int Row,string Room);
internal sealed record SearchPosition(byte[] Save, byte[] Events, byte[] Frame, string Character, uint Seed, bool FirstActThreeBoss, MapAnchor? Anchor);
internal sealed record SearchCommand(int Id, string Op, SearchPosition? Position = null, int BudgetMs = SearchLimits.Fast);
internal sealed record SearchReply(int Id, string Status, int Index = -1, int Trials = 0);

internal static class SearchLimits
{
    public const int Fast = 200, Normal = 350, Slow = 500;
    public const int Paper = 0;
    public static bool IsPreset(long ms) => ms is Paper or Fast or Normal or Slow;
    public static bool Rollout(string c) => c is "SILENT" or "DEFECT" or "REGENT";
    public static int Deadline(int setting,string c) => setting == Paper ? (Rollout(c) ? 4000 : 30000) : setting;
    public const string ReplyPrefix = "@spirecoach ";
}
