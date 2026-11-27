using Godot;
using MegaCrit.Sts2.Core.Nodes;
using static SpireCoach.CoachInput;

namespace SpireCoach;

internal sealed class CoachView
{
    private const string Ink = "3d2c25", Muted = "6f5643", Wine = "733b46", Cream = "fff3d8";
    private readonly Font font = ResourceLoader.Load<Font>("res://fonts/kor/GyeonggiCheonnyeonBatangBold.ttf");
    private readonly CoachInput input;
    private readonly CanvasLayer layer;
    private readonly PanelContainer panel, settings;
    private readonly Label title, status, note, hint, legend;
    private readonly TextureRect crest;
    private readonly Button download, next, hide, launcher, panelToggle, detailToggle;
    private readonly Button[,] bindings = new Button[2, 2];
    private readonly CheckBox[] stageChecks = new CheckBox[Stages.Length];
    private readonly Button searchToggle, searchBudget, combatOrder, cardNumbers;
    private readonly Label searchPick;
    private readonly (PanelContainer box, VBoxContainer caption, Label name, Label value, TextureRect? portrait, Label rank, Label copyTotal)[] rows =
        new (PanelContainer, VBoxContainer, Label, Label, TextureRect?, Label, Label)[4];
    private readonly StyleBoxTexture paper;
    private string? currentCrest, currentPortrait, screen;
    private bool Compact => screen is "상점" or "카드 선택";
    internal static string Heading(string screen) => screen switch
    {
        "전투" => "지금 할 행동",
        "카드 선택" => "카드 선택 추천",
        _ => screen + " 추천"
    };

    private static StyleBoxFlat Box(string fill, string edge, int radius, int x, int y)
    {
        var s = new StyleBoxFlat { BgColor = new Color(fill), BorderColor = new Color(edge),
            ContentMarginLeft = x, ContentMarginRight = x, ContentMarginTop = y, ContentMarginBottom = y };
        s.SetCornerRadiusAll(radius); s.SetBorderWidthAll(1);
        return s;
    }

    // Tile paper grain at its original density. The corner outlines stay intact as a card grows.
    private static StyleBoxTexture Paper()
    {
        const int n = 80, border = 10;
        using var im = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        var rng = new Random(2803);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int dx = Math.Min(x, n - 1 - x), dy = Math.Min(y, n - 1 - y);
            int edge = Math.Min(Math.Min(dx, dy), (dx + dy - 5) / 2);
            float grain = (float)rng.NextDouble() * .032f;
            var color = edge < 0 ? Colors.Transparent : edge <= 1 ? new Color("674633") :
                edge == 2 ? new Color("c19355") : edge == 5 ? new Color("c6a370") : new Color("f1dfba").Darkened(grain + .012f * Math.Max(0, border - edge));
            im.SetPixel(x, y, color);
        }
        var s = new StyleBoxTexture { Texture = ImageTexture.CreateFromImage(im),
            AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 12, ContentMarginBottom = 12 };
        foreach (var side in new[] { Side.Left, Side.Right, Side.Top, Side.Bottom }) s.SetTextureMargin(side, border);
        return s;
    }

    private static T Passive<T>(T c) where T : Control { c.MouseFilter = Control.MouseFilterEnum.Ignore; return c; }
    private Label Text(string name, int size, string color)
    {
        var l = Passive(new Label { Name = name, VerticalAlignment = VerticalAlignment.Center });
        if (font != null) l.AddThemeFontOverride("font", font);
        l.AddThemeFontSizeOverride("font_size", size); l.AddThemeColorOverride("font_color", new Color(color));
        return l;
    }
    private Button Button(string name, string label, Action pressed)
    {
        var b = new Button { Name = name, Text = label, FocusMode = Control.FocusModeEnum.Click,
            CustomMinimumSize = new Vector2(54, 42), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        if (font != null) b.AddThemeFontOverride("font", font);
        b.AddThemeFontSizeOverride("font_size", 17);
        foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            b.AddThemeColorOverride(state, new Color(Ink));
        b.AddThemeStyleboxOverride("normal", Box("ecdbb9", "bfa176", 5, 9, 3));
        b.AddThemeStyleboxOverride("hover", Box("fff1cc", "a77a49", 5, 9, 3));
        b.AddThemeStyleboxOverride("pressed", Box("d6ba91", "946c45", 5, 9, 3));
        b.Pressed += pressed;
        HandleTouch(b, pressed);
        return b;
    }
    private static void HandleTouch(BaseButton b, Action pressed)
    {
        int finger = -1;
        b.GuiInput += e =>
        {
            if (Input.EmulateMouseFromTouch || b.Disabled || e is not InputEventScreenTouch touch) return;
            if (touch.Pressed) finger = touch.Index;
            else if (finger == touch.Index)
            {
                finger = -1;
                if (!touch.Canceled && new Rect2(Vector2.Zero, b.Size).HasPoint(touch.Position)) pressed();
            }
            b.AcceptEvent();
        };
    }
    private PanelContainer Card(string name, out VBoxContainer body)
    {
        var card = Passive(new PanelContainer { Name = name, Visible = false });
        card.AddThemeStyleboxOverride("panel", paper); layer.AddChild(card);
        body = Passive(new VBoxContainer()); body.AddThemeConstantOverride("separation", 5); card.AddChild(body);
        return card;
    }
    private static HBoxContainer Line(Control parent, int gap = 8)
    {
        var line = Passive(new HBoxContainer()); line.AddThemeConstantOverride("separation", gap); parent.AddChild(line);
        return line;
    }
    private static void Expand(Control c) => c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
    private static TextureRect Picture(int size)
    {
        return Passive(new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, CustomMinimumSize = new Vector2(size, size),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ClipContents = true });
    }
    private static void Rule(Control parent)
    {
        var line = Passive(new ColorRect { Color = new Color("bca075"), CustomMinimumSize = new Vector2(0, 1) });
        parent.AddChild(line);
    }

    internal CoachView(CanvasLayer canvas, CoachInput controls, Action startDownload, Action nextPage)
    {
        layer = canvas; input = controls; paper = Paper();
        panel = Card("Recommendations", out var body);
        var header = Line(body);
        crest = Picture(33); header.AddChild(crest);
        title = Text("Title", 25, Ink); Expand(title); header.AddChild(title);
        header.AddChild(Button("Settings", "설정", () => input.OpenSettings(true)));
        Rule(body);
        status = Text("Status", 21, Ink); status.AutowrapMode = TextServer.AutowrapMode.WordSmart; body.AddChild(status);
        searchPick = Text("SearchPick", 18, Wine); searchPick.AutowrapMode = TextServer.AutowrapMode.WordSmart; body.AddChild(searchPick); searchPick.Hide();
        for (int i = 0; i < rows.Length; i++)
        {
            bool first = i == 0;
            var box = Passive(new PanelContainer { Name = $"Choice{i}" });
            box.AddThemeStyleboxOverride("panel", first ? Box(Wine, "aa7953", 7, 9, 6) : Box("ffffff00", "ffffff00", 0, 4, 1));
            body.AddChild(box);
            var line = Line(box, first ? 8 : 7);
            var rank = Text("Rank", first ? 20 : 17, first ? Cream : Muted); line.AddChild(rank);
            var portrait = first ? Picture(44) : null;
            if (portrait != null) line.AddChild(portrait);
            var column = Passive(new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center });
            column.AddThemeConstantOverride("separation", 0); Expand(column); line.AddChild(column);
            var name = Text("ChoiceName", first ? 22 : 20, first ? Cream : Ink);
            name.AutowrapMode = TextServer.AutowrapMode.WordSmart; column.AddChild(name);
            var copyTotal = Text("CopyTotal", 16, first ? "ffe4a5" : Muted);
            copyTotal.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            column.AddChild(copyTotal); copyTotal.Hide();
            var percent = Text("Probability", first ? 22 : 18, first ? "ffe4a5" : Muted); line.AddChild(percent);
            rows[i] = (box, column, name, percent, portrait, rank, copyTotal);
        }
        note = Text("Preview", 18, Muted); note.AutowrapMode = TextServer.AutowrapMode.WordSmart; body.AddChild(note);
        download = Button("Download", "모델 어휘 받기", startDownload); body.AddChild(download);
        Rule(body);
        var footer = Line(body); footer.Alignment = BoxContainer.AlignmentMode.End;
        legend = Text("Legend", 16, Muted); Expand(legend); footer.AddChild(legend);
        next = Button("NextPage", "", nextPage); footer.AddChild(next);
        hide = Button("Hide", "숨기기", () => input.Toggle(ActionId.Panel)); footer.AddChild(hide);

        launcher = Button("CoachSettings", "코치 설정", () => input.OpenSettings(true));
        launcher.Hide(); layer.AddChild(launcher);
        settings = Card("SettingsPanel", out var options);
        settings.MouseFilter = Control.MouseFilterEnum.Stop;
        var top = Line(options); var caption = Text("Title", 25, Ink); caption.Text = "코치 설정"; Expand(caption); top.AddChild(caption);
#if STS_114
        caption.Text = "코치 설정 · v0.114.0";
#endif
        top.AddChild(Button("Close", "닫기", () => input.OpenSettings(false))); Rule(options);
        var toggles = Line(options);
        panelToggle = Button("PanelToggle", "", () => input.Toggle(ActionId.Panel)); Expand(panelToggle); toggles.AddChild(panelToggle);
        detailToggle = Button("DetailToggle", "", () => input.Toggle(ActionId.Details)); Expand(detailToggle); toggles.AddChild(detailToggle);
        var combatSettings = Line(options);
        searchToggle = Button("CombatSearch", "", () => input.SetCombatSearch(!input.CombatSearchEnabled)); combatSettings.AddChild(searchToggle); Expand(searchToggle);
        searchBudget = Button("SearchBudget", "", input.CycleSearchBudget); combatSettings.AddChild(searchBudget); Expand(searchBudget);
        combatOrder = Button("CombatOrder", "", input.ToggleCombatOrder); options.AddChild(combatOrder);
        cardNumbers = Button("CardNumbers", "", input.ToggleCardNumbers); options.AddChild(cardNumbers);
        var stageTitle = Text("StagesTitle", 17, Muted); stageTitle.Text = "추천을 볼 단계"; options.AddChild(stageTitle);
        var stages = Passive(new GridContainer { Columns = 3 }); options.AddChild(stages);
        for (int i = 0; i < Stages.Length; i++)
        {
            var (kind, label) = Stages[i];
            var check = new CheckBox { Name = "Stage" + kind, Text = label, CustomMinimumSize = new Vector2(0, 42),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand };
            if (font != null) check.AddThemeFontOverride("font", font);
            check.AddThemeFontSizeOverride("font_size", 17);
            foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color", "font_hover_pressed_color" })
                check.AddThemeColorOverride(state, new Color(Ink));
            check.Toggled += on => input.SetStage(kind, on);
            HandleTouch(check, () => check.ButtonPressed = !check.ButtonPressed);
            Expand(check); stages.AddChild(check); stageChecks[i] = check;
        }
        var cols = Line(options); var action = Text("Action", 16, Muted); action.Text = "단축키"; Expand(action); cols.AddChild(action);
        foreach (string device in new[] { "키보드", "패드" }) { var l = Text(device == "패드" ? "Pad" : "Keyboard", 16, Muted); l.Text = device; l.HorizontalAlignment = HorizontalAlignment.Center; l.CustomMinimumSize = new Vector2(83, 0); cols.AddChild(l); }
        for (int i = 0; i < 2; i++)
        {
            var id = (ActionId)i; var line = Line(options); var l = Text("Action", 20, Ink);
            l.Text = i == 0 ? "추천 창" : "상세 설명"; Expand(l); line.AddChild(l);
            for (int j = 0; j < 2; j++)
            {
                bool pad = j == 1; var b = Button(id + (pad ? "Pad" : "Key"), "", () => input.Bind(id, pad));
                b.CustomMinimumSize = new Vector2(83, 42); bindings[i, j] = b; line.AddChild(b);
            }
        }
        hint = Text("Hint", 17, Muted); hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; options.AddChild(hint);
        options.AddChild(Button("Defaults", "기본 단축키 복원", input.Restore));
        UpdateSettings();
    }

    internal void UpdateSettings(string? checkpoint = null)
    {
        settings.Visible = input.SettingsOpen;
        if (input.SettingsOpen) { panel.Hide(); launcher.Hide(); }
        panelToggle.Text = input.Hidden ? "추천 창 켜기" : "추천 창 끄기";
        detailToggle.Text = input.Details ? "상세 설명 끄기" : "상세 설명 켜기";
        cardNumbers.Text = input.CardNumbers ? "카드 번호 끄기" : "카드 번호 켜기";
        searchToggle.Text = "AlphaDefect 탐색 " + (input.CombatSearchEnabled ? "켬" : "끔");
        string speed = input.SearchBudgetMs switch { SearchLimits.Normal => "보통", SearchLimits.Slow => "느림", _ => "빠름" };
        searchBudget.Text = input.SearchBudgetMs == SearchLimits.Paper ? "논문 설정 (가장 느림)  ›" : $"{speed} ({input.SearchBudgetMs}ms)  ›";
        searchBudget.TooltipText = "탐색 최대 시간 변경";
        searchBudget.Disabled = !input.CombatSearchEnabled;
        combatOrder.Text = input.GroupCombat ? "전투 정렬 · 행동 합계" : "전투 정렬 · 카드 한 장";
        for (int i = 0; i < Stages.Length; i++)
            stageChecks[i].SetPressedNoSignal(input.Allows(Stages[i].Kind));
        for (int i = 0; i < 2; i++)
        {
            bindings[i, 0].Text = input.KeyName((ActionId)i);
            bindings[i, 1].Text = input.PadName((ActionId)i);
        }
        hint.Text = input.Hint.Length > 0 ? input.Hint : checkpoint != null ? "가중치 · " + checkpoint
            : "바꿀 키나 패드 버튼을 선택하세요.";
        hide.TooltipText = "추천 창 표시 전환 · " + input.PanelBinding;
        settings.ResetSize();
    }
    internal void Layout(bool inMenu)
    {
        var viewport = layer.GetViewport();
        var visible = viewport.GetVisibleRect();
        int windowWidth = DisplayServer.WindowGetSize().X;
        float scale = windowWidth > 0 && windowWidth <= 1440 && DisplayServer.GetName() != "headless"
            ? Math.Clamp(visible.Size.X / windowWidth, 1f, 1.24f) : 1f;
        layer.Scale = Vector2.One * scale;
        visible = layer.Transform.AffineInverse() * visible;
        float width = Math.Clamp(visible.Size.X * .218f, 310, 378);
        float top = Math.Clamp(visible.Size.Y * .115f, 82, 152);
        var inventory = NRun.Instance?.GlobalUi?.RelicInventory;
        if (inventory?.IsVisibleInTree() == true && inventory.RelicNodes.Count > 0)
        {
            var bottom = inventory.GetGlobalTransformWithCanvas() * (inventory.GetBottomOfInventory() - inventory.GetDefaultPosition());
            top = Math.Max(top, (panel.GetCanvasTransform().AffineInverse() * bottom).Y + 12);
        }
        foreach (var card in new[] { panel, settings })
        {
            float cardWidth = card == panel && Compact ? Math.Clamp(visible.Size.X * (screen == "카드 선택" ? .133f : .145f), 200, 284) : width;
            card.CustomMinimumSize = new Vector2(cardWidth, 0);
            card.Size = new Vector2(cardWidth, card.GetCombinedMinimumSize().Y);
            float y = visible.Position.Y + top;
            if (card == panel && screen == "지도") y = Math.Max(y, visible.End.Y - card.Size.Y - 85);
            float x = card == panel && screen == "이벤트" ? visible.Position.X + 16 : visible.End.X - card.Size.X - 16;
            card.Position = new Vector2(x, y);
        }
        launcher.Visible = !input.SettingsOpen && !panel.Visible && (inMenu || input.Hidden || input.HasStageFilters);
        launcher.Position = new Vector2(visible.End.X - launcher.Size.X - 16, visible.Position.Y + top);
    }
    internal void Hide() => panel.Hide();
    internal void Message(string heading, string message, bool setup = false, bool busy = false, string? currentScreen = null)
    {
        if (input.Hidden || input.SettingsOpen) { panel.Hide(); return; }
        screen = currentScreen; title.Text = heading; crest.Hide(); status.Text = message; status.Show(); searchPick.Hide();
        legend.Hide();
        foreach (var row in rows) row.box.Hide();
        note.Hide(); next.Hide(); download.Visible = setup; download.Disabled = busy; panel.ResetSize(); panel.Show(); launcher.Hide();
        if (currentScreen != null) Layout(false);
    }
    internal void Rankings(Capture current, Pick[] picks, int page, SearchReply? searched = null)
    {
        if (input.Hidden || input.SettingsOpen) { panel.Hide(); return; }
        screen = current.Screen;
        title.Text = Heading(screen);
        bool combined = screen == "전투" && input.GroupCombat;
        legend.Visible = !combined;
        legend.Text = screen == "전투" || current.IsUpgrade ? "카드 1장 기준" : Compact ? "선호도" : "모델 선호도";
        var icon = $"res://images/ui/top_panel/character_icon_{current.Character.ToLowerInvariant()}.png";
        SetPicture(crest, icon, ref currentCrest);
        if (Compact) crest.Hide();
        status.Hide(); download.Hide();
        int start = page * rows.Length;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; int rank = start + i; row.box.Visible = rank < picks.Length;
            if (!row.box.Visible) continue;
            var pick = picks[rank];
            Node percentParent = Compact ? row.caption : row.caption.GetParent();
            if (row.value.GetParent() != percentParent)
            {
                row.value.Reparent(percentParent, false);
                if (Compact) row.caption.MoveChild(row.value, row.name.GetIndex() + 1);
            }
            row.rank.Text = (rank + 1).ToString();
            row.name.Text = current.NameAt(pick.Index, input.CardNumbers); row.value.Text = $"{(combined ? pick.TotalProbability : pick.DisplayProbability) * 100:F1}%";
            row.copyTotal.Visible = pick.CardCopies > 1;
            if (row.copyTotal.Visible) row.copyTotal.Text = combined
                ? $"{pick.CardCopies}장 · 한 장당 {pick.DisplayProbability * 100:F1}%"
                : $"{pick.CardCopies}장 합계 {pick.TotalProbability * 100:F1}%";
            if (row.portrait != null)
                SetPicture(row.portrait, Compact ? null : current.Portraits[pick.Index], ref currentPortrait);
        }
        note.Text = current.Details[picks[0].Index];
        searchPick.Visible = searched != null;
        if (searched != null) searchPick.Text = (searched.Status == "tree" ? "트리 탐색" : "롤아웃")
            + $" {searched.Trials}회 추천 · " + current.NameAt(searched.Index, input.CardNumbers);
        note.Visible = input.Details && note.Text.Length > 0;
        next.Visible = picks.Length > rows.Length; next.Text = $"{page + 1}/{(picks.Length + 3) / 4}  ›";
        next.TooltipText = "다음 추천 · F9";
        panel.ResetSize(); panel.Show(); launcher.Hide();
        Layout(false);
    }
    private static void SetPicture(TextureRect view, string? path, ref string? previous)
    {
        if (previous != path)
        {
            view.Texture = path != null && ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
            previous = path;
        }
        view.Visible = view.Texture != null;
    }
}
