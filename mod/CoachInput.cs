using Godot;
using Sts2Headless;

namespace SpireCoach;

public partial class CoachInput : Node
{
    internal enum ActionId { Panel, Details }
    internal static readonly (DecisionKind Kind, string Label)[] Stages =
        [(DecisionKind.Combat, "전투"), (DecisionKind.Map, "길찾기"), (DecisionKind.Event, "이벤트"),
         (DecisionKind.CardReward, "카드 선택"), (DecisionKind.Shop, "상점"), (DecisionKind.RestSite, "모닥불"),
         (DecisionKind.Rewards, "포션 보상")];
    private readonly HashSet<DecisionKind> disabledStages = [];
    internal bool HasStageFilters => disabledStages.Count != 0;
    internal bool Allows(DecisionKind kind) =>
        !disabledStages.Contains(kind == DecisionKind.CardSelect ? DecisionKind.CardReward : kind);
    internal void SetStage(DecisionKind kind, bool enabled)
    {
        if (enabled ? disabledStages.Remove(kind) : disabledStages.Add(kind)) Save();
    }
    private const string ConfigPath = "user://spirecoach-controls.cfg";
    private readonly Key[] keys = [Key.F8, Key.F6];
    private readonly JoyButton[] pads = [JoyButton.RightStick, JoyButton.Invalid];
    private readonly HashSet<(bool pad, int device, long code)> held = [];
    private (ActionId action, bool pad)? capture;
    internal bool Hidden { get; private set; }
    internal bool Details { get; private set; } = true;
    internal bool CardNumbers { get; private set; } = true;
    internal void ToggleCardNumbers() { CardNumbers = !CardNumbers; Save(); }
    internal bool CombatSearchEnabled { get; private set; } = true;
    internal int SearchBudgetMs { get; private set; } = SearchLimits.Paper;
    internal void CycleSearchBudget()
    {
        SearchBudgetMs = SearchBudgetMs switch
        {
            SearchLimits.Fast => SearchLimits.Normal,
            SearchLimits.Normal => SearchLimits.Slow,
            SearchLimits.Slow => SearchLimits.Paper,
            _ => SearchLimits.Fast
        };
        Save();
    }
    internal void SetCombatSearch(bool on) { CombatSearchEnabled=on; Save(); }
    internal bool GroupCombat { get; private set; } = true;
    internal void ToggleCombatOrder() { GroupCombat = !GroupCombat; Save(); }
    internal bool SettingsOpen { get; private set; }
    internal string Hint { get; private set; } = "";
    internal event Action? Changed;
    internal Func<bool>? TryDownload, TryNextPage;

    internal void LoadSettings()
    {
        var file = new ConfigFile();
        if (file.Load(ConfigPath) != Error.Ok) return;
        for (int i = 0; i < keys.Length; i++)
        {
            var id = ((ActionId)i).ToString();
            var key = (Key)file.GetValue(id, "key", (long)keys[i]).AsInt64();
            var pad = (JoyButton)file.GetValue(id, "pad", (long)pads[i]).AsInt64();
            if (AllowedKey(key)) keys[i] = key;
            if (AllowedPad(pad) || pad == JoyButton.Invalid) pads[i] = pad;
        }
        Hidden = file.GetValue("view", "hidden", false).AsBool();
        Details = file.GetValue("view", "details", true).AsBool();
        CardNumbers = file.GetValue("view", "card_numbers", true).AsBool();
        CombatSearchEnabled = file.GetValue("search", "combat", true).AsBool();
        long budget = file.GetValue("search", "paper_budget_ms", SearchLimits.Paper).AsInt64();
        SearchBudgetMs = SearchLimits.IsPreset(budget) ? (int)budget : SearchLimits.Paper;
        GroupCombat = file.GetValue("view", "combat_group_total", true).AsBool();
        disabledStages.Clear();
        foreach (var (kind, _) in Stages)
            if (!file.GetValue("stages", kind.ToString(), true).AsBool()) disabledStages.Add(kind);
        if (disabledStages.Count > 0 && !file.HasSectionKey("stages", nameof(DecisionKind.RestSite)))
            disabledStages.Add(DecisionKind.RestSite);
        if (disabledStages.Count > 0 && !file.HasSectionKey("stages", nameof(DecisionKind.Rewards)))
            disabledStages.Add(DecisionKind.Rewards);
    }

    private void Save()
    {
        var file = new ConfigFile();
        for (int i = 0; i < keys.Length; i++)
        {
            var id = ((ActionId)i).ToString();
            file.SetValue(id, "key", (long)keys[i]);
            file.SetValue(id, "pad", (long)pads[i]);
        }
        file.SetValue("view", "hidden", Hidden);
        file.SetValue("view", "details", Details);
        file.SetValue("view", "card_numbers", CardNumbers);
        file.SetValue("search", "combat", CombatSearchEnabled);
        file.SetValue("search", "paper_budget_ms", SearchBudgetMs);
        file.SetValue("view", "combat_group_total", GroupCombat);
        foreach (var (kind, _) in Stages) file.SetValue("stages", kind.ToString(), Allows(kind));
        var path = ProjectSettings.GlobalizePath(ConfigPath);
        var temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            if (file.Save(temp) != Error.Ok) throw new IOException("설정 파일 쓰기 실패");
            File.Move(temp, path, true);
        }
        catch (Exception ex) { Hint = "이번 실행에만 적용됐어요. 설정 저장에 실패했습니다."; GD.PushWarning("[SpireCoach] " + ex.Message); }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException ex) { GD.PushWarning("[SpireCoach] " + ex.Message); }
            catch (UnauthorizedAccessException ex) { GD.PushWarning("[SpireCoach] " + ex.Message); }
        }
        Changed?.Invoke();
    }

    internal string KeyName(ActionId id) => OS.GetKeycodeString(keys[(int)id]);
    internal string PadName(ActionId id) => pads[(int)id] switch
    {
        JoyButton.Invalid => "미지정", JoyButton.RightStick => "R3", JoyButton.LeftStick => "L3",
        JoyButton.LeftShoulder => "LB", JoyButton.RightShoulder => "RB", var pad => pad.ToString()
    };
    internal string PanelBinding => pads[0] == JoyButton.Invalid ? KeyName(ActionId.Panel) :
        $"{KeyName(ActionId.Panel)} / {PadName(ActionId.Panel)}";
    internal void OpenSettings(bool open)
    {
        SettingsOpen = open; capture = null; Hint = ""; Changed?.Invoke();
    }
    internal void Bind(ActionId id, bool pad)
    {
        capture = (id, pad);
        Hint = pad ? "새 패드 버튼을 누르세요.  B 취소" : "새 단축키를 누르세요.  Esc 취소";
        Changed?.Invoke();
    }
    internal void Restore()
    {
        keys[0] = Key.F8; keys[1] = Key.F6;
        pads[0] = JoyButton.RightStick; pads[1] = JoyButton.Invalid;
        capture = null; Hint = "기본 단축키로 돌아왔어요."; Save();
    }
    internal void Toggle(ActionId action)
    {
        if (action == ActionId.Panel) Hidden = !Hidden; else Details = !Details;
        Save();
    }
    private static Key BaseKey(Key key) => (Key)((long)key & (long)KeyModifierMask.CodeMask);
    private static bool AllowedKey(Key key) => Enum.IsDefined(BaseKey(key)) &&
        BaseKey(key) is not (Key.None or Key.Unknown or Key.F7 or Key.F9 or Key.F10 or Key.Escape or Key.Ctrl or Key.Shift or Key.Alt or Key.Meta);
    private static bool AllowedPad(JoyButton pad) => pad >= JoyButton.A && pad < JoyButton.SdlMax &&
        pad is not (JoyButton.B or JoyButton.Guide or JoyButton.Start);

    public override void _Input(InputEvent input)
    {
        bool isPad = input is InputEventJoypadButton;
        if (!isPad && input is not InputEventKey) return;
        var keyEvent = input as InputEventKey;
        var key = keyEvent?.GetPhysicalKeycodeWithModifiers() ?? Key.None;
        if (keyEvent?.PhysicalKeycode == Key.None) key = keyEvent.GetKeycodeWithModifiers();
        var pad = (input as InputEventJoypadButton)?.ButtonIndex ?? JoyButton.Invalid;
        var token = (isPad, input.Device, isPad ? (long)pad : (long)BaseKey(key));
        if (!input.IsPressed())
        {
            if (held.Remove(token)) GetViewport().SetInputAsHandled();
            return;
        }
        if (input.IsEcho() || held.Contains(token))
        {
            if (capture != null || held.Contains(token)) GetViewport().SetInputAsHandled();
            return;
        }
        if (capture is { } target)
        {
            if (BaseKey(key) == Key.Escape || pad == JoyButton.B) { capture = null; Hint = ""; }
            else if (isPad == target.pad && (isPad || BaseKey(key) is not (Key.Ctrl or Key.Shift or Key.Alt or Key.Meta)))
            {
                int index = (int)target.action;
                if (isPad ? !AllowedPad(pad) : !AllowedKey(key)) Hint = "이 입력은 예약되어 있어요. 다른 입력을 눌러 주세요.";
                else if (isPad ? pads[1 - index] == pad : keys[1 - index] == key)
                    Hint = "다른 코치 기능에서 쓰는 입력이에요. 다른 입력을 눌러 주세요.";
                else
                {
                    if (isPad) pads[index] = pad; else keys[index] = key;
                    capture = null; Hint = "단축키를 저장했어요."; Save();
                }
            }
            Changed?.Invoke();
        }
        else if (key == Key.F10 || (SettingsOpen && (key == Key.Escape || pad == JoyButton.B))) OpenSettings(!SettingsOpen);
        else if (SettingsOpen) return;
        else if (key == Key.F7) { if (TryDownload?.Invoke() != true) return; }
        else if (key == Key.F9) { if (TryNextPage?.Invoke() != true) return; }
        else
        {
            int index = isPad ? Array.IndexOf(pads, pad) : Array.IndexOf(keys, key);
            if (index < 0) return;
            Toggle((ActionId)index);
        }
        held.Add(token);
        GetViewport().SetInputAsHandled();
    }
    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut) held.Clear();
    }
}
