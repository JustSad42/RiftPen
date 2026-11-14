using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RiftPen;

public partial class ToolbarWindow : Window
{
    static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
    static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x78));
    static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xFF));
    static readonly Brush DimBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));

    readonly AppState S = AppState.I;
    readonly OverlayWindow overlay;
    readonly Dictionary<ToolKind, ToggleButton> tools = new();
    readonly List<(Color Color, Border Ring)> swatches = new();
    SettingsWindow? settings;
    ChampionRangesWindow? champions;
    bool refreshing, collapsed, lastDrawMode;

    static readonly ToolKind[] Shapes = { ToolKind.Line, ToolKind.Arrow, ToolKind.Rectangle, ToolKind.Ellipse };

    static UIElement ShapeIconFor(ToolKind kind) => kind switch
    {
        ToolKind.Line => new TextBlock { Text = "╱", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 16, Foreground = Brushes.White },
        ToolKind.Arrow => new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Foreground = Brushes.White },
        ToolKind.Rectangle => new System.Windows.Shapes.Rectangle { Width = 16, Height = 12, Stroke = Brushes.White, StrokeThickness = 1.6, RadiusX = 1, RadiusY = 1, Margin = new Thickness(0), Fill = Brushes.Transparent },
        _ => new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 1.6 },
    };

    static string ShapeName(ToolKind k) => k == ToolKind.Ellipse ? "Circle" : k.ToString();

    void BuildShapeList()
    {
        ShapeList.Children.Clear();
        foreach (var kind in Shapes)
        {
            var row = new DockPanel { LastChildFill = true };
            var key = S.BindingFor("tool:" + kind)?.Display;
            if (key != null)
            {
                var k = new TextBlock { Text = key, Foreground = DimBrush, FontSize = 11, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(k, Dock.Right);
                row.Children.Add(k);
            }
            var icon = new Border { Width = 24, Child = ShapeIconFor(kind), Margin = new Thickness(0, 0, 8, 0) };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = ShapeName(kind), FontFamily = new FontFamily("Segoe UI"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var b = MenuButton(row);
            b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            b.Click += (_, _) => { S.SelectTool(kind); ShapePopup.IsOpen = false; };
            ShapeList.Children.Add(b);
        }
    }

    void ShapeBtn_Click(object sender, RoutedEventArgs e)
    {
        S.SelectTool(AppState.IsShape(S.Tool) ? S.Tool : S.LastShape);
        ShapePopup.IsOpen = true;
        Refresh();
    }

    public ToolbarWindow(OverlayWindow overlay)
    {
        this.overlay = overlay;
        InitializeComponent();

        foreach (var child in Expanded.Children)
        {
            if (child is ToggleButton tb && tb.Tag is string tag && Enum.TryParse<ToolKind>(tag, out var kind))
            {
                tools[kind] = tb;
                tb.Click += (_, _) => SelectTool(kind);
            }
        }
        BuildSwatches();
        BuildMarkerList();
        BuildShapeList();
        BuildRangeList();
        collapsed = S.Settings.AutoCollapseToolbar ? !S.DrawMode : S.Settings.ToolbarCollapsed;
        lastDrawMode = S.DrawMode;
        S.Settings.RangePresets.CollectionChanged += (_, _) => BuildRangeList();

        S.Changed += Refresh;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => UpdateApiStatus();
        timer.Start();

        SourceInitialized += (_, _) =>
        {
            // Clicking the toolbar shouldn't steal focus from the game.
            var h = new WindowInteropHelper(this).Handle;
            Native.SetExStyle(h, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW, 0);
        };
        Loaded += (_, _) => PlaceInitially();
        LocationChanged += (_, _) =>
        {
            S.Settings.ToolbarLeft = Left;
            S.Settings.ToolbarTop = Top;
            S.SaveSettingsSoon();
        };

        ApplyCollapsed();
        Refresh();
        UpdateApiStatus();
    }

    // ---------------------------------------------------------------- building

    void BuildSwatches()
    {
        for (int i = 0; i < AppState.Palette.Length; i++)
        {
            var c = AppState.Palette[i];
            var ring = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Margin = new Thickness(1), Cursor = Cursors.Hand,
                Child = new System.Windows.Shapes.Ellipse
                {
                    Width = 20, Height = 20, Fill = new SolidColorBrush(c),
                    Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), StrokeThickness = 1,
                },
            };
            string id = $"color:{i}";
            ring.MouseLeftButtonDown += (_, e) => { S.Color = c; e.Handled = true; };
            ring.MouseEnter += (_, _) => ring.ToolTip = WithKey("Color", id);
            Swatches.Children.Add(ring);
            swatches.Add((c, ring));
        }
    }

    void BuildMarkerList()
    {
        // Stamps in three columns: vision + spells, objectives, calls.
        MarkerList.Orientation = Orientation.Horizontal;
        var columns = new Dictionary<string, StackPanel>();
        StackPanel Column(string group)
        {
            string col = group is "Vision" or "Spells" ? "a" : group == "Objectives" ? "b" : "c";
            if (!columns.TryGetValue(col, out var panel))
            {
                panel = new StackPanel { MinWidth = 180, Margin = new Thickness(0, 0, col == "c" ? 0 : 6, 0) };
                columns[col] = panel;
                MarkerList.Children.Add(panel);
            }
            return panel;
        }
        string? lastGroup = null;
        foreach (var (kind, name) in MarkerRenderer.All)
        {
            var group = MarkerRenderer.Group(kind);
            var target = Column(group);
            if (group != lastGroup)
            {
                target.Children.Add(new TextBlock
                {
                    Text = group.ToUpperInvariant(), FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), Margin = new Thickness(10, target.Children.Count == 0 ? 4 : 10, 0, 3),
                });
                lastGroup = group;
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new MarkerIcon { Kind = kind, Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI"), FontSize = 13 });
            var b = MenuButton(row);
            b.Click += (_, _) =>
            {
                S.Marker = kind;
                S.SelectTool(ToolKind.Marker);
                MarkerPopup.IsOpen = false;
            };
            target.Children.Add(b);
        }
    }

    /// <summary>Rebuilds the range-circle menu (also called after presets are edited).</summary>
    public void BuildRangeList()
    {
        RangeList.Children.Clear();

        // Follow switch: new circles ride along with the camera-locked champion.
        var followRow = new StackPanel { Orientation = Orientation.Horizontal };
        followRow.Children.Add(new TextBlock
        {
            Text = S.Settings.RangeFollow ? "" : "", // checked / unchecked box
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = S.Settings.RangeFollow ? LinkBrush : Brushes.White,
        });
        followRow.Children.Add(new TextBlock
        {
            Text = "Follow champions (click one, or lock the camera)" + (S.BindingFor("rangeFollow") is { } fk ? $"   [{fk.Display}]" : ""),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        });
        var follow = MenuButton(followRow);
        follow.ToolTip = "On: click a champion to attach the circle to them (positions come from the Rift Board's minimap tracking),\nor lock the replay camera onto one and click anywhere. Arrows drawn from a champion keep starting from them too.\nOff: circles and arrows stay where you put them.";
        follow.Click += (_, _) => { S.RunAction("rangeFollow", true); BuildRangeList(); };
        RangeList.Children.Add(follow);
        RangeList.Children.Add(MenuSeparator());

        var presets = S.Settings.RangePresets;
        for (int i = 0; i < presets.Count; i++)
        {
            int index = i;
            var p = presets[i];
            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(p.Color); } catch { c = Colors.White; }

            var row = new DockPanel { LastChildFill = true };
            var keyText = S.BindingFor("range:" + p.Id)?.Display;
            if (keyText != null)
            {
                var k = new TextBlock { Text = keyText, Foreground = DimBrush, FontSize = 11, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(k, Dock.Right);
                row.Children.Add(k);
            }
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, Stroke = new SolidColorBrush(c), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(60, c.R, c.G, c.B)), Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new TextBlock { Text = $"{p.Name}  ·  {p.Radius:0}", FontFamily = new FontFamily("Segoe UI"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });

            var b = MenuButton(row);
            b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            b.Click += (_, _) =>
            {
                S.RangeIndex = index;
                S.SelectTool(ToolKind.Range);
                RangePopup.IsOpen = false;
            };
            RangeList.Children.Add(b);
        }

        RangeList.Children.Add(MenuSeparator());
        var champ = MenuButton(MenuText("Champion ability ranges…", LinkBrush));
        champ.Click += (_, _) => { RangePopup.IsOpen = false; OpenChampionRanges(); };
        RangeList.Children.Add(champ);
        var edit = MenuButton(MenuText("Edit sizes and shortcuts…", LinkBrush));
        edit.Click += (_, _) => { RangePopup.IsOpen = false; OpenSettings(SettingsWindow.RangesTab); };
        RangeList.Children.Add(edit);
        Refresh();
    }

    void BuildBoardsList()
    {
        BoardsList.Children.Clear();
        var save = MenuButton(MenuText("Save what's on screen as a board…"));
        save.Click += (_, _) => { BoardsPopup.IsOpen = false; SaveBoard(); };
        var load = MenuButton(MenuText("Load a board…"));
        load.Click += (_, _) => { BoardsPopup.IsOpen = false; LoadBoard(null); };
        BoardsList.Children.Add(save);
        BoardsList.Children.Add(load);

        var notesItem = MenuButton(MenuText("Notes for this replay…"));
        notesItem.Click += (_, _) => { BoardsPopup.IsOpen = false; S.RequestUi("notes"); };
        BoardsList.Children.Add(notesItem);

        var recent = Persistence.RecentBoards();
        if (recent.Count > 0)
        {
            BoardsList.Children.Add(MenuSeparator());
            BoardsList.Children.Add(new TextBlock { Text = "Recent boards", Foreground = DimBrush, FontSize = 11, Margin = new Thickness(8, 2, 8, 2), FontFamily = new FontFamily("Segoe UI") });
            foreach (var path in recent)
            {
                var b = MenuButton(MenuText(Path.GetFileNameWithoutExtension(path)));
                b.Click += (_, _) => { BoardsPopup.IsOpen = false; LoadBoard(path); };
                BoardsList.Children.Add(b);
            }
        }

        BoardsList.Children.Add(MenuSeparator());
        var key = S.ReplayKey;
        int notes = key == null ? 0 : S.Items.Count(i => i.ReplayKey == key);
        var clearNotes = MenuButton(MenuText(key == null ? "Replay notes: open a replay" : $"Delete this replay's notes ({notes})", key == null ? DimBrush : null));
        clearNotes.IsEnabled = notes > 0;
        clearNotes.Click += (_, _) =>
        {
            BoardsPopup.IsOpen = false;
            int n = S.DeleteReplayNotes(key);
            S.Say($"Deleted {n} replay note{(n == 1 ? "" : "s")}. Undo brings them back.");
        };
        BoardsList.Children.Add(clearNotes);
    }

    Button MenuButton(object content) => new() { Content = content, Style = (Style)FindResource("MenuBtn") };

    static TextBlock MenuText(string text, Brush? brush = null) =>
        new() { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Foreground = brush ?? Brushes.White };

    static Separator MenuSeparator() => new() { Margin = new Thickness(4), Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)) };

    // ---------------------------------------------------------------- boards

    void SaveBoard()
    {
        var visible = S.Items.Where(i => S.Visible(i, out _) && (i.Space == Space.Screen || S.Replay.Connected)).ToList();
        if (visible.Count == 0) { S.Say("Nothing on screen to save."); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save board",
            Filter = "RiftPen board (*.rpboard)|*.rpboard",
            InitialDirectory = Persistence.BoardsFolder,
            FileName = "My board",
        };
        if (dlg.ShowDialog() != true) return;
        // Boards are reusable layouts, so they don't keep replay timing.
        var copies = visible.Select(i => { var c = i.Clone(); c.ReplayKey = null; c.TimeStart = c.TimeEnd = null; return c; });
        try
        {
            Persistence.Save(dlg.FileName, copies);
            S.Say($"Saved board \"{Path.GetFileNameWithoutExtension(dlg.FileName)}\" ({visible.Count} drawings).");
        }
        catch (Exception ex) { S.Say("Couldn't save: " + ex.Message); }
    }

    void LoadBoard(string? path)
    {
        if (path == null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load board",
                Filter = "RiftPen board (*.rpboard)|*.rpboard|All files|*.*",
                InitialDirectory = Persistence.BoardsFolder,
            };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }
        try
        {
            var items = Persistence.Load(path);
            foreach (var it in items) S.Stamp(it); // in a replay, a loaded board becomes notes at this moment
            S.AddItems(items);
            File.SetLastWriteTime(path, DateTime.Now); // keeps "recent" ordering useful
            int map = items.Count(i => i.Space == Space.World);
            S.Say($"Loaded \"{Path.GetFileNameWithoutExtension(path)}\" ({items.Count} drawings{(map > 0 && !S.Replay.Connected ? $"; {map} show once a replay is open" : "")}).");
        }
        catch (Exception ex) { S.Say("Couldn't load: " + ex.Message); }
    }

    // ---------------------------------------------------------------- state

    void SelectTool(ToolKind kind)
    {
        S.SelectTool(kind);
        if (kind == ToolKind.Marker) MarkerPopup.IsOpen = true;
        if (kind == ToolKind.Range) { BuildRangeList(); RangePopup.IsOpen = true; }
        Refresh();
    }

    static readonly Dictionary<ToolKind, string> ToolNames = new()
    {
        [ToolKind.Select] = "Select: drag to move, scroll to resize, pick a color to recolor",
        [ToolKind.Pen] = "Pen",
        [ToolKind.Highlighter] = "Highlighter",
        [ToolKind.Laser] = "Laser pointer: fades after a moment",
        [ToolKind.Ping] = "Ping: click for a ripple, like a League ping",
        [ToolKind.Eraser] = "Eraser: removes whole strokes",
        [ToolKind.Line] = "Line (Shift snaps to 45°)",
        [ToolKind.Arrow] = "Arrow (Shift snaps to 45°)",
        [ToolKind.Rectangle] = "Rectangle",
        [ToolKind.Ellipse] = "Circle. On the map, drag outward from the center",
        [ToolKind.Text] = "Text: click, type, press Enter",
        [ToolKind.Marker] = "Stamps: wards, spells, objectives and calls",
        [ToolKind.Range] = "Range circles: vision, XP, tower, spells, champion abilities…",
    };

    string WithKey(string text, string id) => S.BindingFor(id) is { } b ? $"{text}   [{b.Display}]" : text;

    void UpdateTooltips()
    {
        foreach (var (kind, tb) in tools) tb.ToolTip = WithKey(ToolNames[kind], "tool:" + kind);
        var hold = S.BindingFor("holdDraw");
        DrawToggle.ToolTip = WithKey("Draw mode on/off. When off, clicks go through to the game and other apps.", "toggleDraw") +
                             (hold != null ? $"\nHold {hold.Display} to draw while held." : "");
        UndoBtn.ToolTip = WithKey("Undo", "undo");
        RedoBtn.ToolTip = WithKey("Redo", "redo");
        ClearBtn.ToolTip = WithKey("Clear what's on screen", "clear");
        HideBtn.ToolTip = WithKey("Hide / show drawings", "hide");
        SpotBtn.ToolTip = WithKey("Spotlight: dim everything except around the cursor", "spotlight");
        ShotBtn.ToolTip = WithKey("Screenshot with drawings (saved to Pictures\\RiftPen)", "screenshot");
        BoardBtn.ToolTip = WithKey("Rift Board: the whole map with every champion, synced to the replay", "board");
        BoardsBtn.ToolTip = "Boards: save and load drawing layouts";
        ColorBtn.ToolTip = $"Color and size   [{S.BindingFor("sizeDown")?.Display ?? "–"} / {S.BindingFor("sizeUp")?.Display ?? "–"} changes size]";
        TimelineBtn.ToolTip = WithKey(S.Settings.TimelineEnabled
            ? $"Timeline notes ON: in a replay, new drawings appear at this moment and stay {(S.Settings.NoteDurationSec > 0 ? $"{S.Settings.NoteDurationSec:0} seconds" : "until the end")}"
            : "Timeline notes OFF: new drawings stay up regardless of replay time", "timeline") +
            $"\nRight-click: {(S.Settings.ShowNotesTimeline ? "hide" : "show")} the replay notes bar{(S.BindingFor("notesBar") is { } nb ? $" [{nb.Display}]" : "")}";
        if (TimelineBtn.ContextMenu == null)
        {
            var barItem = new MenuItem { Header = "Show the replay notes bar", IsCheckable = true };
            barItem.Click += (_, _) => S.RunAction("notesBar", true);
            var listItem = new MenuItem { Header = "Notes list…" };
            listItem.Click += (_, _) => S.RequestUi("notes");
            var menu = new ContextMenu();
            menu.Items.Add(barItem);
            menu.Items.Add(listItem);
            menu.Opened += (_, _) => barItem.IsChecked = S.Settings.ShowNotesTimeline;
            TimelineBtn.ContextMenu = menu;
        }
        var shapeKey = S.BindingFor("tool:" + S.LastShape)?.Display;
        ShapeBtn.ToolTip = $"Shapes: line, arrow, rectangle, circle (last used: {ShapeName(S.LastShape)}{(shapeKey != null ? $" [{shapeKey}]" : "")}). Shift snaps; on the map, circles grow from the center.";
        CollapseBtn.ToolTip = collapsed ? "Expand toolbar" : "Collapse toolbar";
    }

    void Refresh()
    {
        refreshing = true;
        if (S.DrawMode != lastDrawMode)
        {
            lastDrawMode = S.DrawMode;
            if (S.Settings.AutoCollapseToolbar && collapsed == S.DrawMode) { collapsed = !S.DrawMode; ApplyCollapsed(); }
        }
        DrawToggle.IsChecked = S.DrawMode;
        DrawLabel.Text = S.DrawMode ? "✎ Drawing" : "✎ Draw";
        foreach (var (kind, tb) in tools) tb.IsChecked = S.DrawMode && S.Tool == kind;
        ShapeBtn.IsChecked = S.DrawMode && AppState.IsShape(S.Tool);
        var shownShape = AppState.IsShape(S.Tool) ? S.Tool : S.LastShape;
        if (ShapeIcon.Tag is not ToolKind k || k != shownShape) { ShapeIcon.Content = ShapeIconFor(shownShape); ShapeIcon.Tag = shownShape; }
        foreach (var (c, ring) in swatches) ring.BorderBrush = c == S.Color ? Brushes.White : Brushes.Transparent;
        if (Math.Abs(SizeSlider.Value - S.Thickness) > 0.01) SizeSlider.Value = S.Thickness;
        SizeLabel.Text = $"Size  {S.Thickness:0}";
        var brush = new SolidColorBrush(S.Color);
        ColorDot.Fill = brush;
        ColorDot.Width = ColorDot.Height = Math.Clamp(6 + S.Thickness * 0.6, 6, 20);
        LaserDot.Fill = brush;
        LaserGlow.Fill = new SolidColorBrush(Color.FromArgb(85, S.Color.R, S.Color.G, S.Color.B));
        SpaceLabel.Text = S.SpaceMode switch { SpaceMode.Auto => "AUTO", SpaceMode.Map => "MAP", _ => "SCREEN" };
        TimelineBtn.IsChecked = S.Settings.TimelineEnabled;
        HideBtn.IsChecked = S.Hidden;
        SpotBtn.IsChecked = S.Spotlight;
        MarkerPreview.Kind = S.Marker;
        try { RangeDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(S.CurrentRange?.Color ?? "#FFFFFF")); } catch { }
        UpdateTooltips();
        refreshing = false;
    }

    void UpdateApiStatus()
    {
        bool on = S.Replay.Connected;
        ApiDot.Fill = on ? OnBrush : OffBrush;
        string clock = S.ReplayTime is double t && S.Replay.Playback is { } pb
            ? $"\nReplay at {AppState.FormatTime(t)} / {AppState.FormatTime(pb.Length)}{(pb.Paused ? " (paused)" : "")}"
            : "";
        SpaceBtn.ToolTip =
            "Where new drawings go (click to switch)\n" +
            "AUTO: stick to the map when a replay is open, otherwise on screen\n" +
            "MAP: always stick to the map\n" +
            "SCREEN: always fixed on screen\n\n" +
            (on ? "● Replay API connected: map drawings available" : "○ Replay API: " + S.Replay.LastError) + clock;
    }

    void ApplyCollapsed()
    {
        Expanded.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseBtn.Content = collapsed ? "" : "";
        if (IsInitialized) UpdateTooltips();
    }

    void PlaceInitially()
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (S.Settings.ToolbarLeft is double x && S.Settings.ToolbarTop is double y && vs.Contains(new Point(x + 30, y + 10)))
        {
            Left = x;
            Top = y;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 12;
        }
    }

    // ---------------------------------------------------------------- windows

    public void OpenSettings(int tab)
    {
        S.DrawMode = false;
        if (settings is not { IsLoaded: true })
        {
            settings = new SettingsWindow(overlay, this);
            CaptureGuard.Guard(settings);
            settings.Closed += (_, _) => settings = null;
        }
        settings.Show();
        settings.SelectTab(tab);
        settings.Activate();
    }

    public void HideSettings() => settings?.Hide();

    public void OpenChampionRanges()
    {
        S.DrawMode = false;
        if (champions is not { IsLoaded: true })
        {
            champions = new ChampionRangesWindow();
            CaptureGuard.Guard(champions);
            champions.Closed += (_, _) => champions = null;
            champions.Show();
        }
        champions.Activate();
    }

    NotesWindow? notes;

    public void OpenNotes()
    {
        if (notes is not { IsLoaded: true })
        {
            notes = new NotesWindow();
            CaptureGuard.Guard(notes);
            notes.Closed += (_, _) => notes = null;
            notes.Show();
        }
        notes.Activate();
    }

    public void ToggleVisible()
    {
        if (IsVisible) Hide();
        else { Show(); Refresh(); }
    }

    // ---------------------------------------------------------------- handlers

    void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void DrawToggle_Click(object sender, RoutedEventArgs e) => S.DrawMode = DrawToggle.IsChecked == true;

    void Size_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (refreshing || !IsInitialized) return;
        S.Thickness = e.NewValue;
    }

    void ColorBtn_Click(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = !ColorPopup.IsOpen;

    void Boards_Click(object sender, RoutedEventArgs e)
    {
        BuildBoardsList();
        BoardsPopup.IsOpen = true;
    }

    void Collapse_Click(object sender, RoutedEventArgs e)
    {
        collapsed = !collapsed;
        if (!S.Settings.AutoCollapseToolbar) S.Settings.ToolbarCollapsed = collapsed;
        S.SaveSettingsSoon();
        ApplyCollapsed();
    }

    /// <summary>Re-reads the auto-collapse setting (after Settings changes it).</summary>
    public void ApplyCollapseSetting()
    {
        collapsed = S.Settings.AutoCollapseToolbar ? !S.DrawMode : S.Settings.ToolbarCollapsed;
        ApplyCollapsed();
    }

    void Timeline_Click(object sender, RoutedEventArgs e) => S.RunAction("timeline", true);
    void Space_Click(object sender, RoutedEventArgs e) => S.CycleSpaceMode();
    void Undo_Click(object sender, RoutedEventArgs e) => S.Undo();
    void Redo_Click(object sender, RoutedEventArgs e) => S.Redo();
    void Clear_Click(object sender, RoutedEventArgs e) => S.Clear();
    void Hide_Click(object sender, RoutedEventArgs e) => S.Hidden = HideBtn.IsChecked == true;
    void Spot_Click(object sender, RoutedEventArgs e) => S.Spotlight = SpotBtn.IsChecked == true;
    void Shot_Click(object sender, RoutedEventArgs e) => S.RequestUi("screenshot");

    void Board_Click(object sender, RoutedEventArgs e) => S.RequestUi("board");
    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(0);
    void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
