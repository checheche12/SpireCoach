using Godot;
using MegaCrit.Sts2.Core.Nodes;
using Sts2Headless;

namespace SpireCoach;

public static class Coach
{
    private static CoachView view = null!;
    private static CoachInput controls = null!;
    private static NativePolicy? worker;
    private static Task? download;
    private static bool dataLoaded;
    private static Task<Pick[]>? pending;
    private static Capture? current, requested;
    private static Pick[]? shown;
    private static CombatLookahead? lookahead;
    private static Task<SearchReply?>? search;
    private static Capture? attempted;
    private static SearchReply? searched;
    private static string modDirectory = "";
    private static ulong next, retryAfter, started;
    private static int page;
    private static string? fault, lastLog;

    public static void Attach(SceneTree tree, string dir)
    {
        modDirectory = dir;
        var layer = new CanvasLayer { Name = "SpireCoachHUD", Layer = 110 };
        tree.Root.AddChild(layer);
        controls = new CoachInput { Name = "CoachControls", ProcessMode = Node.ProcessModeEnum.Always };
        layer.AddChild(controls);
        controls.LoadSettings();
        view = new CoachView(layer, controls, () => StartDownload(), () => NextPage());
        controls.Changed += () => {
            if (!controls.CombatSearchEnabled || controls.Hidden || controls.SettingsOpen || !controls.Allows(DecisionKind.Combat)) CancelSearch();
            if (!controls.CombatSearchEnabled && lookahead != null) { lookahead.Dispose(); lookahead=null; }
            if (current?.Kind == DecisionKind.Combat && shown != null)
                shown = SortCombat(shown);
            view.UpdateSettings(worker?.Checkpoint); next = 0; Render();
        };
        controls.TryDownload = StartDownload;
        controls.TryNextPage = NextPage;
        try
        {
            LiveBoard.ObserveHandSelections();
#if STS_114
            Observation.RawPreview.Install(new HarmonyLib.Harmony("spirecoach.observation"));
#endif
            CombatAnchor.Install();
            if (LocalData.Ready) LoadVocabulary();
            Observation.RouteSummary = true;
            worker = new(dir);
            GD.Print("[SpireCoach] 준비 완료");
        }
        catch (Exception ex) { fault = "코치 설정을 확인하세요 · " + ex.Message; GD.PushError("[SpireCoach] " + ex); }
        tree.ProcessFrame += Tick;
        layer.TreeExiting += () => { tree.ProcessFrame -= Tick; lookahead?.Dispose(); worker?.Dispose(); };
    }

    private static bool StartDownload()
    {
        if (dataLoaded || download != null || controls.Hidden) return false;
        download = LocalData.Download(); fault = null; Render();
        return true;
    }
    private static bool NextPage()
    {
        if (shown == null || shown.Length <= 4 || controls.Hidden || controls.SettingsOpen) return false;
        page = (page + 1) % ((shown.Length + 3) / 4);
        Render();
        return true;
    }
    private static void LoadVocabulary()
    {
        Vocab.Load(Path.Combine(LocalData.Folder, "vocab.txt"));
        dataLoaded = Vocab.Count == LocalData.VocabSize;
        if (!dataLoaded) throw new InvalidDataException("모델 어휘 파일 누락");
    }
    private static void Tick()
    {
        ulong now = Time.GetTicksMsec();
        if (attempted != null && (search != null || searched != null) && lookahead?.StateChanged() == true) { CancelSearch(); next=0; }
        if (now < next && search?.IsCompleted != true && pending?.IsCompleted != true && download?.IsCompleted != true) return;
        next = now + 250;
        try
        {
            view.Layout(NGame.Instance?.MainMenu?.IsVisibleInTree() == true);
            if (download?.IsCompleted == true)
            {
                try { download.GetAwaiter().GetResult(); LoadVocabulary(); }
                catch (Exception ex) { fault = ex.Message; }
                download = null;
                Render();
            }
            if (!dataLoaded) { Render(); return; }
            var observed = controls.Hidden || controls.SettingsOpen ? null : LiveBoard.ReadEnabled(controls.Allows);
            if (observed == null || current == null || observed.Character != current.Character || !observed.Frame.AsSpan().SequenceEqual(current.Frame)
                || !observed.Names.SequenceEqual(current.Names) || !observed.CardPositions.SequenceEqual(current.CardPositions))
            {
                CancelSearch(); current = observed; shown = null; page = 0;
                if (worker != null) fault = null;
                Render();
            }
            if (pending?.IsCompleted == true)
            {
                try
                {
                    var picks = pending.GetAwaiter().GetResult();
                    if (ReferenceEquals(requested, current) && current != null)
                    {
                        if (picks.Length == 0 || picks.Any(p => p.Index < 0 || p.Index >= current.Names.Length))
                            throw new InvalidDataException("모델 응답의 선택지 불일치");
                        bool combined = current.Kind == DecisionKind.Combat && controls.GroupCombat;
                        shown = current.Kind == DecisionKind.Combat ? SortCombat(picks) : picks;
                        var best = shown[0];
                        fault = null;
                        GD.Print($"[SpireCoach] {current.Character} | {current.Screen} | {current.Names[best.Index]} | {(combined ? best.TotalProbability : best.DisplayProbability):P1} | {now - started} ms");
                    }
                }
                catch (Exception ex) { Report(ex); fault = "추론 연결을 확인하세요. 잠시 후 다시 시도합니다."; retryAfter = now + 12000; }
                pending = null; requested = null; Render();
            }
            if (pending == null && current != null && shown == null && worker != null && now >= retryAfter)
            {
                requested = current; started = now;
                var snapshot = current;
                int? perCopyAction = snapshot.Kind switch
                {
                    DecisionKind.Combat => (int)ActionKind.PlayCard,
                    DecisionKind.CardSelect when snapshot.IsUpgrade => (int)ActionKind.SelectCard,
                    _ => null
                };
                pending = Task.Run(() => worker.Rank(snapshot.Character, snapshot.Frame, perCopyAction, snapshot.IsUpgrade, snapshot.Names));
            }
            if (search?.IsCompleted == true) {
                var result = search.GetAwaiter().GetResult(); search = null;
                if (result != null && ReferenceEquals(attempted,current) && attempted != null && lookahead?.StateChanged()==false) {
                    searched=result;
                    GD.Print($"[SpireCoach] 탐색 | {attempted.Names[result.Index]} | {result.Status} | {result.Trials}");
                    Render();
                }
            }
            if (controls.CombatSearchEnabled && search==null && current?.Kind is (DecisionKind.Combat or DecisionKind.CardSelect) && shown != null
                && !ReferenceEquals(attempted,current)) {
                lookahead ??= new(modDirectory);
                attempted=current; search=lookahead.Start(current,controls.SearchBudgetMs);
            }
        }
        catch (Exception ex)
        {
            CancelSearch(); current = null; shown = null;
            view.Message("첨탑 코치", "게임 상태를 읽지 못했어요. 게임과 모드의 업데이트를 확인해 주세요.");
            Report(ex); next = now + 1500;
        }
    }
    private static Pick[] SortCombat(Pick[] picks) => picks
        .OrderByDescending(p => controls.GroupCombat ? p.TotalProbability : p.DisplayProbability)
        .ThenBy(p => p.Index).ToArray();

    private static void Report(Exception ex)
    {
        if (lastLog != ex.Message) { GD.PushError("[SpireCoach] " + ex); lastLog = ex.Message; }
    }
    private static void CancelSearch()
    {
        lookahead?.Invalidate(); attempted=null; searched=null;
    }
    private static void Render()
    {
        if (controls.Hidden || controls.SettingsOpen) { view.Hide(); return; }
        if (!dataLoaded)
        {
            view.Message("첨탑 코치 설정", fault ?? (download != null ? "어휘 파일을 받는 중…" : "첫 실행에는 모델 어휘가 필요해요."),
                setup: true, busy: download != null); return;
        }
        if (current == null || !controls.Allows(current.Kind)) { view.Hide(); return; }
        if (shown == null) view.Message(CoachView.Heading(current.Screen), fault ?? "추천 계산 중…", currentScreen: current.Screen);
        else view.Rankings(current, shown, page, searched);
    }
}
