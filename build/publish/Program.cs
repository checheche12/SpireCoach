using System.Text.Json;
using Steamworks;

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--publish"))
{
    Console.Error.WriteLine("Usage: dotnet run --project build/publish -- workshop [--publish]");
    return 2;
}

const uint gameId = 2868840;
const ulong itemId = 3845542964;
var workspace = Path.GetFullPath(args[0]);
var content = Path.Combine(workspace, "content");
var note = File.ReadAllText(Path.Combine(workspace, "change-note.txt")).TrimEnd();
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(content, "SpireCoach.json")));
if (File.ReadAllText(Path.Combine(workspace, "mod_id.txt")).Trim() != itemId.ToString()
    || manifest.RootElement.GetProperty("id").GetString() != "SpireCoach"
    || !File.Exists(Path.Combine(content, "SpireCoach.dll")))
    throw new InvalidDataException("Expected the existing SpireCoach Workshop item and its packaged mod.");

Environment.SetEnvironmentVariable("SteamAppId", gameId.ToString());
if (!SteamAPI.Init()) throw new InvalidOperationException("Could not connect to the Steam client.");
try
{
    var app = SteamUtils.GetAppID();
    if (app.m_AppId != gameId) throw new InvalidOperationException("Unexpected Steam app ID.");
    var item = new PublishedFileId_t(itemId);
    var update = SteamUGC.StartItemUpdate(app, item);
    if (update == UGCUpdateHandle_t.Invalid || !SteamUGC.SetItemContent(update, content))
        throw new InvalidOperationException("Steam could not stage the content folder.");

    if (args.Length == 1)
    {
        Console.WriteLine($"Checked item {itemId} and local content. No update submitted.");
        return 0;
    }

    SubmitItemUpdateResult_t? result = null;
    bool ioFailure = false;
    using var callback = CallResult<SubmitItemUpdateResult_t>.Create((value, failed) =>
    {
        result = value;
        ioFailure = failed;
    });
    var call = SteamUGC.SubmitItemUpdate(update, note);
    if (call == SteamAPICall_t.Invalid) throw new InvalidOperationException("Steam rejected the upload.");
    callback.Set(call);
    while (!result.HasValue)
    {
        SteamAPI.RunCallbacks();
        Thread.Sleep(100);
    }
    var reply = result.Value;
    if (ioFailure || reply.m_eResult != EResult.k_EResultOK || reply.m_nPublishedFileId != item)
        throw new InvalidOperationException($"Steam upload failed ({reply.m_eResult}, I/O failure: {ioFailure}).");
    Console.WriteLine($"Updated the mod files for item {itemId}.");
    if (reply.m_bUserNeedsToAcceptWorkshopLegalAgreement)
        Console.WriteLine("Steam requires acceptance of the Workshop legal agreement.");
    return 0;
}
finally
{
    SteamAPI.Shutdown();
}
