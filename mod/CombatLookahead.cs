using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace SpireCoach;

internal sealed class CombatLookahead(string directory) : IDisposable
{
    private static readonly FieldInfo replayField = typeof(CombatReplayWriter).GetField("_replay", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly object pipeLock = new();
    private Process? process;
    private int sequence;
    private CancellationTokenSource? cancel;
    private CombatReplay? tape;
    private byte[]? initial;
    private int eventCount;
    private bool disposed, selection;
    private readonly bool trace = MegaCrit.Sts2.Core.Helpers.CommandLineHelper.HasArg("coach-search-check");
    private string? diagnostic;
    private DateTime retryAt;
    private static CombatReplay? Tape() => RunManager.Instance?.CombatReplayWriter is { IsRecordingReplay: true } writer
        ? replayField?.GetValue(writer) as CombatReplay : null;
    internal bool StateChanged() => tape == null || !CombatManager.Instance.IsInProgress || (!selection && RunManager.Instance.ActionExecutor.IsRunning)
        || (!selection && (NPlayerHand.Instance?.IsInCardSelection == true || NOverlayStack.Instance?.Peek() != null)) || NMapScreen.Instance?.IsOpen == true
        || !ReferenceEquals(tape,Tape()) || tape.events.Count != eventCount;

    internal Task<SearchReply?>? Start(Capture capture, int deadlineMs)
    {
        var state=RunManager.Instance?.DebugOnlyGetState();
        if(disposed || DateTime.UtcNow<retryAt || state==null || capture.Names.Length<2
           || !CombatManager.Instance.IsInProgress || BitConverter.ToInt32(capture.Frame,44)==0) return null;
        bool isRollout=SearchLimits.Rollout(capture.Character);
        var room=state.CurrentRoom?.RoomType;
        if(isRollout ? (state.CurrentActIndex!=2 || room!=MegaCrit.Sts2.Core.Rooms.RoomType.Boss || CombatAnchor.Current==null)
                     : room is not (MegaCrit.Sts2.Core.Rooms.RoomType.Monster or MegaCrit.Sts2.Core.Rooms.RoomType.Elite or MegaCrit.Sts2.Core.Rooms.RoomType.Boss)) return null;
        var replay=Tape();
        if(replay?.serializableRun.SerializableRng.Seed is not { } runSeed) return null;
        if (tape!=replay || initial==null) {
            var sw=new PacketWriter(); sw.Write(replay.serializableRun);
            initial=sw.Buffer.AsSpan(0,sw.BytePosition).ToArray();
        }
        tape=replay; eventCount=replay.events.Count; selection=capture.Kind==Sts2Headless.DecisionKind.CardSelect;
        var ew=new PacketWriter(); ew.WriteList(replay.events);
        int played=replay.events.Count(e=>e.action is MegaCrit.Sts2.Core.GameActions.NetPlayCardAction
            or MegaCrit.Sts2.Core.GameActions.NetUsePotionAction or MegaCrit.Sts2.Core.GameActions.NetEndPlayerTurnAction
            || e.eventType==CombatReplayEventType.PlayerChoice);
        var seed=DecisionSeed(runSeed,(long)replay.nextActionId+played);
        if(isRollout) seed=unchecked(seed+7919u*(uint)(played+1));
        var pos=new SearchPosition(initial,ew.Buffer.AsSpan(0,ew.BytePosition).ToArray(),capture.Frame,capture.Character,seed,
            room==MegaCrit.Sts2.Core.Rooms.RoomType.Boss && state.CurrentActIndex==2 && BitConverter.ToInt32(capture.Frame,96)==0,
            isRollout ? CombatAnchor.Current : null);
        deadlineMs=SearchLimits.Deadline(deadlineMs,capture.Character);
        var game = typeof(RunManager).Assembly;
        string gameDir = Path.GetDirectoryName(game.Location)!;
        string branch = typeof(Coach).Assembly.GetName().Name!["SpireCoach.".Length..];
        var version = game.ManifestModule.ModuleVersionId;
        cancel?.Dispose(); cancel=new();
        var token=cancel.Token;
        int id=++sequence;
        return Task.Run<SearchReply?>(async () => {
            try {
                Process? proc;
                lock(pipeLock) proc=process;
                if(proc == null || proc.HasExited) {
                    proc=await SearchRuntime.Start(directory,gameDir,branch,version,token);
                    lock(pipeLock) {
                        if(disposed || token.IsCancellationRequested) { Kill(proc); return null; }
                        process=proc;
                    }
                    proc.ErrorDataReceived += (_, e) => { if(e.Data != null) { diagnostic=e.Data; if(trace) Godot.GD.Print("[SearchHost] "+e.Data); } };
                    proc.BeginErrorReadLine();
                    var ready=await Read(proc,TimeSpan.FromSeconds(15));
                    if(ready?.Status!="online") throw new IOException("Search startup: "+diagnostic);
                }
                token.ThrowIfCancellationRequested();
                Send(proc,new(id,"prepare",pos));
                using var registration=token.Register(() => { try { Send(proc,new(id,"cancel")); } catch { } });
                var prep=await Read(proc,TimeSpan.FromSeconds(8));
                if(trace) Godot.GD.Print($"[SearchCheck] prepare {id} {prep?.Status}");
                if(prep?.Id!=id || prep.Status!="ready" || token.IsCancellationRequested) return null;
                var elapsed=Stopwatch.StartNew();
                Send(proc,new(id,"search",BudgetMs:deadlineMs));
                var replyTask=ReadReply(proc);
                SearchReply? reply;
                try { reply=await replyTask.WaitAsync(TimeSpan.FromMilliseconds(deadlineMs+3000)); }
                catch (TimeoutException) {
                    lock(pipeLock) { StopProcess(); retryAt=DateTime.UtcNow.AddSeconds(30); }
                    try { await replyTask; } catch (IOException) { } catch (ObjectDisposedException) { }
                    if(trace) Godot.GD.Print($"[SearchCheck] expired {id}");
                    return null;
                }
                if(token.IsCancellationRequested || reply==null) return null;
                if(trace) Godot.GD.Print($"[SearchCheck] search {id} {reply.Status} {elapsed.ElapsedMilliseconds} ms, budget {deadlineMs} ms, {reply.Trials} trials");
                if (reply.Id != id) return null;
                return reply.Status==(isRollout ? "rollout" : "tree") && reply.Trials is >0 and <=32
                    && (uint)reply.Index<(uint)capture.Names.Length ? reply : null;
            } catch(OperationCanceledException) when(token.IsCancellationRequested) { return null; }
              catch(Exception ex) { lock(pipeLock) { StopProcess(); retryAt=DateTime.UtcNow.AddSeconds(30); }
                  Godot.GD.PushWarning("[SpireCoach] 전투 탐색 · "+ex.GetBaseException().Message); return null; }
        });
    }

    private static uint DecisionSeed(string run,long action)
    {
        uint crc=uint.MaxValue;
        foreach(byte b in System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant($"{run}:{action}:0"))) {
            crc^=b;
            for(int bit=0;bit<8;bit++) crc=(crc>>1)^((crc&1)!=0 ? 0xedb88320u : 0u);
        }
        return ~crc & 0x3fffffffu;
    }
    private static async Task<SearchReply?> Read(Process p,TimeSpan timeout)
    {
        return await ReadReply(p).WaitAsync(timeout);
    }
    private static async Task<SearchReply?> ReadReply(Process p)
    {
        while(await p.StandardOutput.ReadLineAsync() is { } line)
            if(line.StartsWith(SearchLimits.ReplyPrefix,StringComparison.Ordinal))
                return JsonSerializer.Deserialize<SearchReply>(line.AsSpan(SearchLimits.ReplyPrefix.Length));
        return null;
    }
    private void Send(Process p,SearchCommand cmd)
    {
        string line=JsonSerializer.Serialize(cmd);
        lock(pipeLock) { p.StandardInput.WriteLine(line); p.StandardInput.Flush(); }
    }
    // Signal from the UI without blocking it on a pipe write.
    internal void Invalidate() { if(cancel is { IsCancellationRequested:false } c) _=c.CancelAsync(); }
    private static void Kill(Process p) { try { if(!p.HasExited) p.Kill(); } catch(InvalidOperationException) { } finally {p.Dispose();} }
    private void StopProcess() { if(process!=null) Kill(process); process=null; }
    public void Dispose() { disposed=true; Invalidate(); lock(pipeLock) StopProcess(); }
}
