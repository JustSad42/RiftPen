using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RiftPen;

/// <summary>
/// The Rift Board: a big Summoner's Rift with every champion where they are in the open replay, a timeline to move
/// through the game (linked to the replay, or on its own as a sandbox), drawing, dragging champions to explain
/// ideas, and the scoreboard and objectives at the board's moment.
/// </summary>
public sealed class RiftBoardWindow : Window
{
    static RiftBoardWindow? instance;

    public static void Open()
    {
        instance ??= new RiftBoardWindow();
        instance.Show();
        if (instance.WindowState == WindowState.Minimized) instance.WindowState = WindowState.Normal;
        instance.Activate();
    }

    static readonly Color BlueTeam = Color.FromRgb(0, 153, 224), RedTeam = Color.FromRgb(232, 61, 61);
    static readonly Brush Fg = Frozen(new SolidColorBrush(Color.FromRgb(225, 228, 235)));
    static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromRgb(140, 146, 158)));
    static readonly Brush PanelBack = Frozen(new SolidColorBrush(Color.FromRgb(22, 24, 30)));
    static readonly Brush BarBack = Frozen(new SolidColorBrush(Color.FromRgb(28, 31, 38)));
    static readonly Brush ButtonBack = Frozen(new SolidColorBrush(Color.FromRgb(42, 46, 56)));
    static readonly Brush ButtonOn = Frozen(new SolidColorBrush(Color.FromRgb(58, 111, 247)));
    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    readonly AppState S = AppState.I;
    readonly RiftSession R = RiftSession.I;
    readonly BoardMapView map = new();
    readonly DrawCanvas ink = new();
    readonly Canvas editLayer = new();
    readonly Grid stage = new() { ClipToBounds = true };
    readonly BoardTimeline timeline = new();
    readonly TextBlock title = new(), status = new(), timeText = new();
    readonly StackPanel side = new() { Margin = new Thickness(12, 10, 12, 16) };
    readonly StackPanel drawTools = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    readonly ToggleButton linkBtn = new(), drawBtn = new(), cleanBtn = new(), trailBtn = new();
    readonly Button playBtn = new();
    readonly ToggleButton timersBtn = new(), wardsBtn = new(), jungleBtn = new();
    readonly Button pathsBtn = new();
    readonly ComboBox speedBox = new();
    readonly Button colorBtn = new();
    readonly DispatcherTimer tick;

    // Board clock when not linked to the replay ("sandbox").
    bool linked = AppState.I.Settings.BoardLinked;
    double sandboxTime, sandboxSpeed = 1;
    bool sandboxPlaying;
    long sandboxStamp;
    double? scrubTime;

    double Length => R.Game?.Length ?? S.Replay.Playback?.Length ?? 0;

    double SandboxNow
    {
        get
        {
            if (!sandboxPlaying) return sandboxTime;
            double t = sandboxTime + (Stopwatch.GetTimestamp() - sandboxStamp) / (double)Stopwatch.Frequency * sandboxSpeed;
            return Length > 0 ? Math.Min(t, Length) : t;
        }
    }

    /// <summary>The moment the board shows.</summary>
    double Now()
    {
        if (scrubTime is double s) return s;
        if (linked && S.Replay.CurrentTime is double rt) return rt;
        return SandboxNow;
    }

    bool Playing => linked ? S.Replay.Playback is { Paused: false } : sandboxPlaying;

    RiftBoardWindow()
    {
        Title = "Rift Board";
        Background = PanelBack;
        Foreground = Fg;
        FontFamily = new FontFamily("Segoe UI");
        MinWidth = 900;
        MinHeight = 600;
        Icon = Application.Current.MainWindow?.Icon;
        var w = S.Settings.BoardWindow;
        if (w is { Length: >= 4 } && w[2] > 300 && w[3] > 300)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = w[0]; Top = w[1]; Width = w[2]; Height = w[3];
            if (w.Length >= 5 && w[4] > 0) WindowState = WindowState.Maximized;
        }
        else { Width = 1500; Height = 950; WindowStartupLocation = WindowStartupLocation.CenterScreen; }

        SourceInitialized += (_, _) => AvoidLeagueScreen();

        map.TimeSource = Now;
        map.CleanMap = S.Settings.BoardCleanMap;
        map.Trails = S.Settings.BoardTrails;
        map.ShowObjectives = S.Settings.BoardTimers;
        map.ShowWards = S.Settings.BoardWards;
        map.ShowJungle = S.Settings.BoardJungle;
        map.PathMode = Enum.TryParse<BoardPathMode>(S.Settings.BoardPathMode, out var pm) ? pm : BoardPathMode.Off;
        map.PathWindow = S.Settings.BoardPathWindow;
        timeline.TimeSource = Now;
        timeline.Scrub += OnScrub;
        ink.Board = map;
        ink.EditLayer = editLayer;
        ink.IsHitTestVisible = false;

        Content = BuildLayout();
        WireMapInput();

        tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        tick.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            // Same view and mode as last time.
            if (S.Settings.BoardCenter is { Length: 2 } c) map.SetView(S.Settings.BoardZoom, new Point(c[0], c[1]));
            if (!linked) sandboxTime = S.Replay.CurrentTime ?? 0;
            map.ViewChanged += () =>
            {
                var w = map.CenterWorld;
                S.Settings.BoardZoom = map.Zoom;
                S.Settings.BoardCenter = new[] { Math.Round(w.X), Math.Round(w.Y) };
                S.SaveSettingsSoon();
            };
            tick.Start();
            RefreshAll();
        };
        R.Changed += OnSessionChanged;
        S.Changed += OnAppChanged;
        Closed += (_, _) =>
        {
            tick.Stop();
            R.Changed -= OnSessionChanged;
            S.Changed -= OnAppChanged;
            ink.CommitText();
            S.Settings.BoardWindow = new[] { RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height, WindowState == WindowState.Maximized ? 1 : 0 };
            if (S.SettingsAutosave) S.Settings.Save(); // self-tests leave your settings alone
            instance = null;
        };
        PreviewKeyDown += OnKey;
    }

    /// <summary>
    /// With "open on a screen without League" on: if the board would open on League's screen (covering the
    /// minimap it reads positions from) and there's another screen, it opens maximized there instead.
    /// </summary>
    void AvoidLeagueScreen()
    {
        if (!S.Settings.BoardAvoidLeagueScreen) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!Native.TryGetClientScreenRect(Native.FindLeagueWindow(), out var league) || !Native.GetWindowRect(hwnd, out var me)) return;
        var screens = Native.GetMonitors();
        Native.RECT? ScreenOf(int x, int y) => screens.Cast<Native.RECT?>().FirstOrDefault(m => m!.Value.Contains(x, y));
        var leagueScreen = ScreenOf(league.Left + league.Width / 2, league.Top + league.Height / 2);
        var myScreen = ScreenOf(me.Left + me.Width / 2, me.Top + me.Height / 2);
        if (leagueScreen == null || (myScreen != null && !myScreen.Value.Equals(leagueScreen.Value))) return;
        var other = screens.Where(m => !m.Equals(leagueScreen.Value)).OrderByDescending(m => (long)m.Width * m.Height).Cast<Native.RECT?>().FirstOrDefault();
        if (other is not Native.RECT target) return;
        WindowState = WindowState.Normal;
        Native.SetWindowPos(hwnd, IntPtr.Zero, target.Left + 40, target.Top + 40, target.Width - 80, target.Height - 80, 0x4 /* NOZORDER */ | Native.SWP_NOACTIVATE);
        WindowState = WindowState.Maximized;
    }

    // ---------------------------------------------------------------- layout

    static Button MakeButton(string glyph, string tip, RoutedEventHandler click, string? text = null)
    {
        var b = new Button { ToolTip = tip, Margin = new Thickness(2, 0, 2, 0) };
        b.Content = ButtonContent(glyph, text);
        Flat(b);
        b.Click += click;
        return b;
    }

    static object ButtonContent(string glyph, string? text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph.Length > 0) sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        if (text != null) sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(glyph.Length > 0 ? 6 : 0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    static void Flat(ButtonBase b)
    {
        b.Foreground = Fg;
        b.Background = ButtonBack;
        b.BorderThickness = new Thickness(0);
        b.Padding = new Thickness(9, 5, 9, 5);
        b.MinHeight = 30;
        b.Cursor = Cursors.Hand;
        b.FocusVisualStyle = null;
        // Flat look without the default chrome.
        var tpl = new ControlTemplate(b.GetType());
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        tpl.VisualTree = border;
        b.Template = tpl;
        if (b is ToggleButton tb)
        {
            tb.Checked += (_, _) => tb.Background = ButtonOn;
            tb.Unchecked += (_, _) => tb.Background = ButtonBack;
        }
        b.MouseEnter += (_, _) => { if (b is not ToggleButton { IsChecked: true }) b.Background = new SolidColorBrush(Color.FromRgb(56, 61, 74)); };
        b.MouseLeave += (_, _) => { if (b is not ToggleButton { IsChecked: true }) b.Background = ButtonBack; };
    }

    static void SetupToggle(ToggleButton b, string glyph, string? text, string tip, bool on)
    {
        b.Content = ButtonContent(glyph, text);
        b.ToolTip = tip;
        b.Margin = new Thickness(2, 0, 2, 0);
        Flat(b);
        b.IsChecked = on;
        b.Background = on ? ButtonOn : ButtonBack;
    }

    UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(370) });

        // Top bar
        var bar = new DockPanel { Background = BarBack, LastChildFill = true };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 6) };
        title.FontSize = 15;
        title.FontWeight = FontWeights.SemiBold;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(0, 0, 14, 0);
        left.Children.Add(title);

        SetupToggle(drawBtn, "", "Draw", "Draw on the board (D). Drawings are map drawings: they also show in the game, at this moment of the replay.", false);
        drawBtn.Click += (_, _) => SetDrawing(drawBtn.IsChecked == true);
        left.Children.Add(drawBtn);

        foreach (var (glyph, tip, tool) in new[]
        {
            ("", "Pen", ToolKind.Pen), ("", "Highlighter", ToolKind.Highlighter), ("", "Arrow", ToolKind.Arrow),
            ("", "Circle (drag out from the centre)", ToolKind.Ellipse), ("", "Range circle (current preset)", ToolKind.Range),
            ("", "Ward / marker", ToolKind.Marker), ("", "Text", ToolKind.Text), ("", "Laser (fades away)", ToolKind.Laser),
            ("", "Eraser", ToolKind.Eraser), ("", "Select and move drawings", ToolKind.Select),
        })
        {
            var tb = new ToggleButton();
            SetupToggle(tb, glyph, null, tip, S.Tool == tool);
            tb.Tag = tool;
            tb.Click += (_, _) => { S.SelectTool(tool); SyncTools(); };
            drawTools.Children.Add(tb);
        }
        colorBtn.Margin = new Thickness(2, 0, 2, 0);
        Flat(colorBtn);
        colorBtn.ToolTip = "Colour";
        colorBtn.Click += (_, _) => ColorMenu().IsOpen = true;
        drawTools.Children.Add(colorBtn);
        drawTools.Children.Add(MakeButton("", "Undo (Ctrl+Z)", (_, _) => S.Undo()));
        drawTools.Children.Add(MakeButton("", "Clear the drawings shown at this moment", (_, _) => ClearBoard()));
        left.Children.Add(drawTools);
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Right };
        SetupToggle(cleanBtn, "", "Clean map", "Dim, grey map art so drawings and champions stand out", map.CleanMap);
        cleanBtn.Click += (_, _) => { map.CleanMap = cleanBtn.IsChecked == true; S.Settings.BoardCleanMap = map.CleanMap; S.SaveSettingsSoon(); map.InvalidateVisual(); };
        SetupToggle(trailBtn, "", "Trails", "Show where each champion walked in the last 6 seconds", map.Trails);
        trailBtn.Click += (_, _) => { map.Trails = trailBtn.IsChecked == true; S.Settings.BoardTrails = map.Trails; S.SaveSettingsSoon(); map.InvalidateVisual(); };
        SetupToggle(timersBtn, "", "Timers", "Dragon, Void Grubs, Herald and Baron spawn timers on the map, and inhibitor respawns", map.ShowObjectives);
        timersBtn.Click += (_, _) => { map.ShowObjectives = timersBtn.IsChecked == true; S.Settings.BoardTimers = map.ShowObjectives; S.SaveSettingsSoon(); map.InvalidateVisual(); RefreshSide(); };
        SetupToggle(wardsBtn, "", "Wards", "Wards out at this moment, with their vision and time left.\nRiot's timeline says who placed a ward and when, not where, so each sits where its owner was standing (dashed when that spot is only estimated).", map.ShowWards);
        wardsBtn.Click += (_, _) => { map.ShowWards = wardsBtn.IsChecked == true; S.Settings.BoardWards = map.ShowWards; S.SaveSettingsSoon(); map.InvalidateVisual(); };
        SetupToggle(jungleBtn, "", "Jungle", "Each jungler's first clear: numbered camps with the time they got there, and their path (from the recorded minimap, first 4 minutes).\nSelect a jungler to see only them.", map.ShowJungle);
        jungleBtn.Click += (_, _) => { map.ShowJungle = jungleBtn.IsChecked == true; S.Settings.BoardJungle = map.ShowJungle; S.SaveSettingsSoon(); map.InvalidateVisual(); };
        pathsBtn.Margin = new Thickness(2, 0, 2, 0);
        Flat(pathsBtn);
        pathsBtn.ToolTip = "Paths and heatmap: where champions went, or where they spent their time.\nSelect a champion to see only them; otherwise everyone is shown.";
        pathsBtn.Click += (_, _) => PathsMenu().IsOpen = true;
        UpdatePathsButton();
        right.Children.Add(cleanBtn);
        right.Children.Add(trailBtn);
        right.Children.Add(timersBtn);
        right.Children.Add(wardsBtn);
        right.Children.Add(jungleBtn);
        right.Children.Add(pathsBtn);
        right.Children.Add(MakeButton("", "Put dragged champions back where they really are", (_, _) => { map.Moved.Clear(); map.MovedAt = double.NaN; map.InvalidateVisual(); }, "Reset"));
        right.Children.Add(MakeButton("", "Fit the whole map in the window (double-click with the middle button does it too)", (_, _) => map.ResetView(), "Fit"));
        right.Children.Add(MakeButton("", "Rift Board settings: Riot API key, minimap tracking", (_, _) => OpenSettings()));
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);

        status.Foreground = Dim;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Margin = new Thickness(6, 0, 6, 0);
        bar.Children.Add(status);
        Grid.SetColumnSpan(bar, 2);
        root.Children.Add(bar);

        // Map, with the drawing layer and text-entry layer on top
        stage.Children.Add(map);
        stage.Children.Add(ink);
        stage.Children.Add(editLayer);
        stage.Background = Brushes.Transparent;
        Grid.SetRow(stage, 1);
        root.Children.Add(stage);

        // Side panel
        var scroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = PanelBack };
        Grid.SetRow(scroll, 1);
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);

        // Timeline + transport
        var bottom = new DockPanel { Background = BarBack };
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 6, 8), VerticalAlignment = VerticalAlignment.Center };
        transport.Children.Add(MakeButton("", "Back 10 seconds (Left arrow: 5 s)", (_, _) => Jump(-10)));
        playBtn.Margin = new Thickness(2, 0, 2, 0);
        Flat(playBtn);
        playBtn.MinWidth = 44;
        playBtn.ToolTip = "Play / pause (Space)";
        playBtn.Click += (_, _) => TogglePlay();
        transport.Children.Add(playBtn);
        transport.Children.Add(MakeButton("", "Forward 10 seconds (Right arrow: 5 s)", (_, _) => Jump(10)));
        foreach (var sp in new[] { 0.5, 1, 2, 4, 8 }) speedBox.Items.Add(new ComboBoxItem { Content = $"{sp:0.#}×", Tag = sp });
        speedBox.SelectedIndex = 1;
        speedBox.Width = 64;
        speedBox.Margin = new Thickness(6, 0, 6, 0);
        speedBox.VerticalContentAlignment = VerticalAlignment.Center;
        speedBox.SelectionChanged += (_, _) => { if (speedBox.SelectedItem is ComboBoxItem { Tag: double s }) SetSpeed(s); };
        transport.Children.Add(speedBox);
        timeText.Foreground = Fg;
        timeText.FontSize = 14;
        timeText.MinWidth = 110;
        timeText.VerticalAlignment = VerticalAlignment.Center;
        timeText.FontFeatures();
        transport.Children.Add(timeText);
        SetupToggle(linkBtn, "", "Linked", "Linked: the board and the replay move together (scrubbing here moves the replay).\nUnlinked: a sandbox — move through the game on the board without touching the replay.", linked);
        linkBtn.Click += (_, _) => SetLinked(linkBtn.IsChecked == true);
        transport.Children.Add(linkBtn);
        DockPanel.SetDock(transport, Dock.Left);
        bottom.Children.Add(transport);
        timeline.Margin = new Thickness(4, 4, 10, 4);
        bottom.Children.Add(timeline);
        Grid.SetRow(bottom, 2);
        Grid.SetColumnSpan(bottom, 2);
        root.Children.Add(bottom);
        return root;
    }

    void UpdatePathsButton()
    {
        string window = map.PathWindow > 0 ? $"{map.PathWindow / 60:0} min" : "whole game";
        string text = map.PathMode switch { BoardPathMode.Path => $"Path · {window}", BoardPathMode.Heatmap => $"Heatmap · {window}", _ => "Paths" };
        pathsBtn.Content = ButtonContent("", text);
        pathsBtn.Background = map.PathMode == BoardPathMode.Off ? ButtonBack : ButtonOn;
    }

    ContextMenu PathsMenu()
    {
        var menu = new ContextMenu { PlacementTarget = pathsBtn, Placement = PlacementMode.Bottom };
        void Add(string header, BoardPathMode mode, double window)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = map.PathMode == mode && (mode == BoardPathMode.Off || map.PathWindow == window) };
            item.Click += (_, _) =>
            {
                map.PathMode = mode;
                if (mode != BoardPathMode.Off) map.PathWindow = window;
                S.Settings.BoardPathMode = mode.ToString();
                S.Settings.BoardPathWindow = map.PathWindow;
                S.SaveSettingsSoon();
                UpdatePathsButton();
                map.InvalidateVisual();
            };
            menu.Items.Add(item);
        }
        Add("Off", BoardPathMode.Off, map.PathWindow);
        menu.Items.Add(new Separator());
        Add("Path: last 1 minute", BoardPathMode.Path, 60);
        Add("Path: last 2 minutes", BoardPathMode.Path, 120);
        Add("Path: last 5 minutes", BoardPathMode.Path, 300);
        menu.Items.Add(new Separator());
        Add("Heatmap: last 5 minutes", BoardPathMode.Heatmap, 300);
        Add("Heatmap: last 10 minutes", BoardPathMode.Heatmap, 600);
        Add("Heatmap: whole game so far", BoardPathMode.Heatmap, 0);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Paths use positions recorded from the minimap; parts not yet watched are empty.", IsEnabled = false });
        return menu;
    }

    ContextMenu ColorMenu()
    {
        var menu = new ContextMenu { PlacementTarget = colorBtn, Placement = PlacementMode.Bottom };
        var wrap = new WrapPanel { Width = 170 };
        foreach (var c in AppState.Palette)
        {
            var sw = new Border { Width = 26, Height = 26, Margin = new Thickness(3), CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(c), Cursor = Cursors.Hand,
                BorderBrush = Brushes.White, BorderThickness = new Thickness(c == S.Color ? 2 : 0) };
            sw.MouseLeftButtonUp += (_, _) => { S.Color = c; menu.IsOpen = false; SyncTools(); };
            wrap.Children.Add(sw);
        }
        menu.Items.Add(wrap);
        return menu;
    }

    void SyncTools()
    {
        foreach (var child in drawTools.Children)
            if (child is ToggleButton { Tag: ToolKind k } tb)
            {
                tb.IsChecked = S.Tool == k;
                tb.Background = tb.IsChecked == true ? ButtonOn : ButtonBack;
            }
        colorBtn.Content = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(S.Color), BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
        ink.UpdateCursor();
    }

    void OnAppChanged()
    {
        if (map.Drawing) SyncTools();
        ink.InvalidateVisual();
    }

    void SetDrawing(bool on)
    {
        map.Drawing = on;
        drawBtn.IsChecked = on;
        drawBtn.Background = on ? ButtonOn : ButtonBack;
        ink.IsHitTestVisible = on;
        drawTools.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) ink.CommitText();
        SyncTools();
        ink.InvalidateVisual();
    }

    void ClearBoard()
    {
        double t = Now();
        var shown = S.ContextItems.Where(i => i.Space == Space.World && S.Visible(i, t, out _)).ToList();
        if (shown.Count > 0) S.RemoveItems(shown);
    }

    // ---------------------------------------------------------------- time

    void SetLinked(bool on)
    {
        if (on == linked) return;
        if (!on)
        {
            sandboxTime = S.Replay.CurrentTime ?? sandboxTime;
            sandboxPlaying = false;
        }
        else if (S.Replay.Connected) S.Replay.Seek(SandboxNow); // bring the replay to where the board is
        linked = on;
        S.Settings.BoardLinked = on;
        S.SaveSettingsSoon();
        linkBtn.IsChecked = on;
        linkBtn.Background = on ? ButtonOn : ButtonBack;
        S.Say(on ? "Linked: the board and the replay move together." : "Sandbox: the board moves on its own; the replay stays put.");
    }

    void SetTime(double t)
    {
        t = Math.Clamp(t, 0, Math.Max(0, Length));
        if (linked && S.Replay.Connected) S.Replay.Seek(t);
        sandboxTime = t;
        sandboxStamp = Stopwatch.GetTimestamp();
    }

    void Jump(double seconds) => SetTime(Now() + seconds);

    void TogglePlay()
    {
        if (linked && S.Replay.Playback is { } pb) S.Replay.SetPaused(!pb.Paused);
        else
        {
            sandboxTime = SandboxNow;
            sandboxStamp = Stopwatch.GetTimestamp();
            sandboxPlaying = !sandboxPlaying;
        }
    }

    void Pause()
    {
        if (linked && S.Replay.Playback is { Paused: false }) S.Replay.SetPaused(true);
        if (sandboxPlaying) { sandboxTime = SandboxNow; sandboxPlaying = false; }
    }

    void SetSpeed(double s)
    {
        if (linked && S.Replay.Connected) S.Replay.SetSpeed(s);
        sandboxTime = SandboxNow;
        sandboxStamp = Stopwatch.GetTimestamp();
        sandboxSpeed = s;
    }

    void OnScrub(double t, bool done)
    {
        if (!done) { scrubTime = t; return; }
        scrubTime = null;
        SetTime(t);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Jump(-5); break;
            case Key.Right: Jump(5); break;
            case Key.D when !ctrl: SetDrawing(!map.Drawing); break;
            case Key.Escape when map.Drawing: SetDrawing(false); break;
            case Key.Z when ctrl: S.Undo(); break;
            case Key.Y when ctrl: S.Redo(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- map input: pan, zoom, drag champions

    Point? panFrom;
    int? dragging;

    void WireMapInput()
    {
        stage.PreviewMouseWheel += (_, e) =>
        {
            if (map.Drawing && S.Tool == ToolKind.Select && S.Selected != null) return; // the drawing layer resizes the selection
            map.ZoomAt(e.GetPosition(map), e.Delta > 0 ? 1.18 : 1 / 1.18);
            ink.InvalidateVisual();
            e.Handled = true;
        };
        stage.PreviewMouseDown += (_, e) =>
        {
            bool pan = e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Right && !map.Drawing);
            if (!pan) return;
            if (e.ChangedButton == MouseButton.Middle && e.ClickCount == 2) { map.ResetView(); ink.InvalidateVisual(); }
            panFrom = e.GetPosition(stage);
            stage.CaptureMouse();
            e.Handled = true;
        };
        map.MouseLeftButtonDown += (_, e) =>
        {
            var p = e.GetPosition(map);
            if (map.TokenAt(p) is int id)
            {
                map.SelectedId = id;
                if (e.ClickCount == 2 && linked && map.StateOf(id) is { } st) { map.LookAt(st.Pos); return; }
                Pause(); // moving champions by hand only makes sense on a still moment
                dragging = id;
                stage.CaptureMouse();
                RefreshSide();
            }
            else if (e.ClickCount == 2)
            {
                if (linked && S.Replay.Connected) map.LookAt(map.ViewToWorld(p));
            }
            else
            {
                if (map.SelectedId != null) { map.SelectedId = null; RefreshSide(); }
                panFrom = e.GetPosition(stage);
                stage.CaptureMouse();
            }
            map.InvalidateVisual();
            e.Handled = true;
        };
        stage.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(stage);
            if (panFrom is Point from)
            {
                map.PanBy(p - from);
                ink.InvalidateVisual();
                panFrom = p;
                return;
            }
            if (dragging is int id)
            {
                map.Moved[id] = map.ViewToWorld(e.GetPosition(map));
                map.MovedAt = Now();
                map.InvalidateVisual();
                return;
            }
            var hover = map.Drawing ? null : map.TokenAt(e.GetPosition(map));
            if (hover != map.HoverId) { map.HoverId = hover; map.InvalidateVisual(); }
            map.Cursor = hover != null ? Cursors.Hand : null;
        };
        stage.MouseLeave += (_, _) => { if (map.HoverId != null) { map.HoverId = null; map.InvalidateVisual(); } };
        MouseButtonEventHandler end = (_, _) =>
        {
            if (panFrom == null && dragging == null) return;
            panFrom = null;
            dragging = null;
            if (stage.IsMouseCaptured) stage.ReleaseMouseCapture();
        };
        stage.MouseUp += end;
        stage.LostMouseCapture += (_, _) => { panFrom = null; dragging = null; };
    }

    // ---------------------------------------------------------------- refresh

    double lastT = double.NaN;
    int lastVersion = -1;
    CameraState? lastCam;
    DateTime lastSide;
    GameTimeline? builtFor;

    void Tick()
    {
        double t = Now();
        bool showCam = linked && scrubTime == null && S.Replay.Connected;
        var cam = S.Replay.Latest;
        bool changed = t != lastT || R.Version != lastVersion || showCam != map.ShowCamera ||
                       (showCam && !CameraState.SameView(cam, lastCam));
        map.ShowCamera = showCam;
        if (changed)
        {
            map.InvalidateVisual();
            if (t != lastT) { ink.InvalidateVisual(); timeline.InvalidateVisual(); }
        }
        lastT = t;
        lastVersion = R.Version;
        lastCam = cam;
        ink.TickBoard();

        if ((DateTime.Now - lastSide).TotalMilliseconds > 400) RefreshAll();
    }

    void OnSessionChanged()
    {
        map.InvalidateVisual();
        timeline.InvalidateVisual();
        RefreshAll();
    }

    void RefreshAll()
    {
        lastSide = DateTime.Now;
        var g = R.Game;
        title.Text = g == null ? "Rift Board" : $"Rift Board · {g.Platform}-{g.GameId}";
        var bits = new List<string>();
        if (R.Status.Length > 0) bits.Add(R.Status);
        if (g != null && R.TrackStatus.Length > 0) bits.Add(R.TrackStatus);
        if (g != null && !linked) bits.Add("Sandbox (not moving the replay)");
        status.Text = string.Join("  ·  ", bits);
        status.ToolTip = status.Text;

        double t = Now();
        timeText.Text = $"{AppState.FormatTime(t)} / {AppState.FormatTime(Length)}";
        playBtn.Content = ButtonContent(Playing ? "" : "", null);
        double speed = linked ? S.Replay.Playback?.Speed ?? 1 : sandboxSpeed;
        if (speedBox.SelectedItem is ComboBoxItem { Tag: double cur } && Math.Abs(cur - speed) > 0.01 && !speedBox.IsDropDownOpen)
            foreach (ComboBoxItem it in speedBox.Items)
                if (it.Tag is double v && Math.Abs(v - speed) < 0.01) { speedBox.SelectedItem = it; break; }
        if (colorBtn.Content == null) SyncTools();
        RefreshSide();
    }

    // ---------------------------------------------------------------- side panel: scoreboard, objectives, details

    sealed class Row
    {
        public required GameParticipant P;
        public required Border Root;
        public required Image Icon;
        public required TextBlock Name, Level, Kda, Cs, Gold;
        public required WrapPanel Items;
    }

    readonly List<Row> rows = new();
    readonly TextBlock scoreText = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock goldText = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
    readonly StackPanel objectives = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly StackPanel details = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock itemsNote = new() { Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel timersPanel = new() { Margin = new Thickness(0, 10, 0, 0) };

    // ---------------------------------------------------------------- objective timers (side panel)

    void RefreshTimers(GameTimeline g, double t)
    {
        timersPanel.Children.Clear();
        timersPanel.Visibility = map.ShowObjectives ? Visibility.Visible : Visibility.Collapsed;
        if (!map.ShowObjectives) return;
        timersPanel.Children.Add(new TextBlock { Text = "TIMERS", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(2, 0, 0, 4) });
        var wrap = new WrapPanel();
        foreach (var o in Objectives.At(g, t))
            wrap.Children.Add(Chip(o.Up ? $"{o.Name} · up" : $"{o.Name}  {Objectives.Clock(o.SpawnsIn)}", o.Up ? Color.FromRgb(255, 196, 60) : Color.FromRgb(150, 156, 170)));
        timersPanel.Children.Add(wrap);
    }

    static Border Chip(string text, Color c) => new()
    {
        Child = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = new SolidColorBrush(c) },
        Background = new SolidColorBrush(Color.FromRgb(32, 35, 44)), CornerRadius = new CornerRadius(5),
        Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 6),
    };

    // ---------------------------------------------------------------- lane matchup

    static readonly (string Lane, string Label)[] Lanes = { ("TOP", "Top"), ("JUNGLE", "Jungle"), ("MIDDLE", "Mid"), ("BOTTOM", "Bot"), ("UTILITY", "Support") };
    static readonly (string Name, string Unit, Func<ParticipantFrame, double> Pick)[] Metrics =
    {
        ("Gold", "gold", f => f.TotalGold), ("CS", "CS", f => f.MinionsKilled + f.JungleMinionsKilled), ("XP", "XP", f => f.Xp),
    };

    readonly StackPanel matchupPanel = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly WrapPanel laneButtons = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly WrapPanel metricButtons = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock matchupTitle = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 4) };
    readonly Grid matchupStats = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly MatchupGraph matchupGraph = new();

    /// <summary>The blue and red champion in a lane (Riot's position tag, or team order if it's missing).</summary>
    (GameParticipant Blue, GameParticipant Red)? LanePair(GameTimeline g, string lane)
    {
        var blue = g.Participants.Where(p => p.Team == 100).ToList();
        var red = g.Participants.Where(p => p.Team == 200).ToList();
        if (blue.Count == 0 || red.Count == 0) return null;
        int idx = Array.FindIndex(Lanes, l => l.Lane == lane);
        var b = blue.FirstOrDefault(p => p.Lane == lane) ?? blue[Math.Clamp(idx, 0, blue.Count - 1)];
        var r = red.FirstOrDefault(p => p.Lane == lane) ?? red[Math.Clamp(idx, 0, red.Count - 1)];
        return (b, r);
    }

    void BuildMatchup()
    {
        matchupPanel.Children.Clear();
        matchupPanel.Children.Add(new TextBlock { Text = "LANE MATCHUP", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(2, 0, 0, 4) });
        laneButtons.Children.Clear();
        foreach (var (lane, label) in Lanes)
        {
            var b = new ToggleButton();
            SetupToggle(b, "", label, $"Compare the two {label.ToLowerInvariant()} players", S.Settings.BoardMatchupLane == lane);
            b.Padding = new Thickness(8, 3, 8, 3);
            b.MinHeight = 24;
            b.Margin = new Thickness(0, 0, 4, 4);
            b.Tag = lane;
            b.Click += (_, _) => { S.Settings.BoardMatchupLane = lane; S.SaveSettingsSoon(); SyncChoiceButtons(); RefreshMatchup(); };
            laneButtons.Children.Add(b);
        }
        matchupPanel.Children.Add(laneButtons);
        matchupPanel.Children.Add(matchupTitle);
        matchupStats.ColumnDefinitions.Clear();
        foreach (var w in new[] { 1.2, 1, 1, 1 }) matchupStats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        matchupPanel.Children.Add(matchupStats);
        matchupGraph.TimeSource = Now;
        matchupGraph.Seek -= SetTime;
        matchupGraph.Seek += SetTime;
        matchupPanel.Children.Add(matchupGraph);
        metricButtons.Children.Clear();
        foreach (var (name, _, _) in Metrics)
        {
            var b = new ToggleButton();
            SetupToggle(b, "", name, $"Graph the {name} difference over the game", S.Settings.BoardMatchupMetric == name);
            b.Padding = new Thickness(8, 3, 8, 3);
            b.MinHeight = 24;
            b.Margin = new Thickness(0, 0, 4, 0);
            b.Tag = name;
            b.Click += (_, _) => { S.Settings.BoardMatchupMetric = name; S.SaveSettingsSoon(); SyncChoiceButtons(); RefreshMatchup(); };
            metricButtons.Children.Add(b);
        }
        matchupPanel.Children.Add(metricButtons);
    }

    void SyncChoiceButtons()
    {
        foreach (var panel in new[] { laneButtons, metricButtons })
            foreach (var child in panel.Children)
                if (child is ToggleButton { Tag: string tag } tb)
                {
                    tb.IsChecked = tag == S.Settings.BoardMatchupLane || tag == S.Settings.BoardMatchupMetric;
                    tb.Background = tb.IsChecked == true ? ButtonOn : ButtonBack;
                }
    }

    void RefreshMatchup()
    {
        var g = R.Game;
        if (g == null || LanePair(g, S.Settings.BoardMatchupLane) is not var (blue, red)) return;
        double t = Now();
        var metric = Metrics.FirstOrDefault(m => m.Name == S.Settings.BoardMatchupMetric);
        if (metric.Pick == null) metric = Metrics[0];
        matchupGraph.BlueId = blue.Id;
        matchupGraph.RedId = red.Id;
        matchupGraph.Metric = metric.Pick;
        matchupGraph.Unit = metric.Unit;
        matchupGraph.InvalidateVisual();

        matchupTitle.Inlines.Clear();
        matchupTitle.Inlines.Add(new System.Windows.Documents.Run(blue.ChampionName) { Foreground = new SolidColorBrush(BlueTeam) });
        matchupTitle.Inlines.Add(new System.Windows.Documents.Run("  vs  ") { Foreground = Dim });
        matchupTitle.Inlines.Add(new System.Windows.Documents.Run(red.ChampionName) { Foreground = new SolidColorBrush(RedTeam) });

        // Live numbers when the board is at the replay's moment, otherwise Riot's per-minute timeline.
        bool live = R.LiveAt(t);
        LivePlayer? Lp(GameParticipant p) => live ? R.Live.Players.FirstOrDefault(x => x.ChampionKey == p.ChampionKey) : null;
        double Cs(GameParticipant p) => Lp(p)?.CreepScore ?? R.StatAt(p.Id, t, f => f.MinionsKilled + f.JungleMinionsKilled);
        var rowsData = new (string Label, double B, double R, Func<double, string> Fmt)[]
        {
            ("Gold", R.StatAt(blue.Id, t, f => f.TotalGold), R.StatAt(red.Id, t, f => f.TotalGold), v => $"{v / 1000:0.0}k"),
            ("CS", Cs(blue), Cs(red), v => $"{v:0}"),
            ("Level", R.StateAt(blue, t).Level, R.StateAt(red, t).Level, v => $"{v:0}"),
            ("XP", R.StatAt(blue.Id, t, f => f.Xp), R.StatAt(red.Id, t, f => f.Xp), v => $"{v:N0}"),
        };
        matchupStats.Children.Clear();
        matchupStats.RowDefinitions.Clear();
        for (int i = 0; i < rowsData.Length; i++)
        {
            var (label, b, r, fmt) = rowsData[i];
            matchupStats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            double diff = b - r;
            var cells = new (string Text, Brush Brush, HorizontalAlignment Align)[]
            {
                (label, Dim, HorizontalAlignment.Left),
                (fmt(b), new SolidColorBrush(BlueTeam), HorizontalAlignment.Right),
                (fmt(r), new SolidColorBrush(RedTeam), HorizontalAlignment.Right),
                (Math.Abs(diff) < 0.5 ? "even" : (diff > 0 ? "+" : "−") + fmt(Math.Abs(diff)), new SolidColorBrush(Math.Abs(diff) < 0.5 ? Colors.Gray : diff > 0 ? BlueTeam : RedTeam), HorizontalAlignment.Right),
            };
            for (int c = 0; c < cells.Length; c++)
            {
                var tb = new TextBlock { Text = cells[c].Text, Foreground = cells[c].Brush, HorizontalAlignment = cells[c].Align, FontWeight = c == 3 ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(2, 1, 2, 1) };
                Grid.SetRow(tb, i);
                Grid.SetColumn(tb, c);
                matchupStats.Children.Add(tb);
            }
        }
    }

    static readonly Dictionary<string, BitmapSource?> images = new();

    async void SetImage(Image img, string kind, string name)
    {
        string k = kind + ":" + name;
        if (images.TryGetValue(k, out var cached)) { img.Source = cached; return; }
        images[k] = null;
        try
        {
            var path = await DataDragon.GetImageAsync(kind, name);
            if (path == null) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 64;
            bmp.EndInit();
            bmp.Freeze();
            images[k] = bmp;
            img.Source = bmp;
        }
        catch (Exception ex) { AppSettings.LogError(ex); }
    }

    void BuildSide(GameTimeline g)
    {
        builtFor = g;
        rows.Clear();
        side.Children.Clear();
        side.Children.Add(scoreText);
        side.Children.Add(goldText);
        foreach (int team in new[] { 100, 200 })
        {
            var c = team == 100 ? BlueTeam : RedTeam;
            side.Children.Add(new TextBlock { Text = team == 100 ? "BLUE" : "RED", Foreground = new SolidColorBrush(c), FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(2, 8, 0, 4) });
            foreach (var p in g.Participants.Where(x => x.Team == team))
            {
                var row = MakeRow(p, c);
                rows.Add(row);
                side.Children.Add(row.Root);
            }
        }
        side.Children.Add(itemsNote);
        side.Children.Add(timersPanel);
        BuildMatchup();
        side.Children.Add(matchupPanel);
        side.Children.Add(lanesPanel);
        side.Children.Add(junglePanel);
        side.Children.Add(wardsPanel);
        side.Children.Add(objectives);
        side.Children.Add(details);
    }

    Row MakeRow(GameParticipant p, Color team)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Width = 32, Height = 32, Clip = new EllipseGeometry(new Point(16, 16), 16, 16) };
        SetImage(icon, "champion", p.ChampionKey);
        var iconBox = new Grid { Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Top };
        iconBox.Children.Add(icon);
        iconBox.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = new SolidColorBrush(team), StrokeThickness = 2 });
        var level = new TextBlock { FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(16, 18, 24)), Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, -3) };
        iconBox.Children.Add(level);
        grid.Children.Add(iconBox);

        var mid = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        var name = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Text = p.ChampionName;
        mid.Children.Add(name);
        var items = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        mid.Children.Add(items);
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);

        var stats = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var kda = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        var cs = new TextBlock { Foreground = Dim, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        var gold = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(230, 196, 92)), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        stats.Children.Add(kda);
        stats.Children.Add(cs);
        stats.Children.Add(gold);
        Grid.SetColumn(stats, 2);
        grid.Children.Add(stats);

        var border = new Border { Child = grid, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 1, 0, 1), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand,
            ToolTip = string.IsNullOrEmpty(p.RiotId) ? p.ChampionName : $"{p.ChampionName} · {p.RiotId}{(p.Lane.Length > 0 ? " · " + p.Lane : "")}" };
        border.MouseLeftButtonUp += (_, _) => { map.SelectedId = map.SelectedId == p.Id ? null : p.Id; map.InvalidateVisual(); RefreshSide(); };
        return new Row { P = p, Root = border, Icon = icon, Name = name, Level = level, Kda = kda, Cs = cs, Gold = gold, Items = items };
    }

    void RefreshSide()
    {
        var g = R.Game;
        if (g == null)
        {
            builtFor = null;
            side.Children.Clear();
            side.Children.Add(new TextBlock
            {
                Text = R.Status.Length > 0 ? R.Status : "Open a replay in League to start.",
                Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0),
            });
            side.Children.Add(new TextBlock
            {
                Text = "The Rift Board follows the replay that's open in League: it finds the game, loads Riot's timeline for it and reads champion positions off the minimap as the replay plays. Parts you haven't played yet are estimated from the timeline (dashed rings).",
                Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
            });
            return;
        }
        if (builtFor != g) BuildSide(g);

        double t = Now();
        bool live = R.LiveAt(t);
        int Kills(int team) => g.Events.Count(e => e.Type == "CHAMPION_KILL" && e.Time <= t && g.P(e.KillerId)?.Team == team);
        int bk = Kills(100), rk = Kills(200);
        scoreText.Inlines.Clear();
        scoreText.Inlines.Add(new System.Windows.Documents.Run(bk.ToString()) { Foreground = new SolidColorBrush(BlueTeam) });
        scoreText.Inlines.Add(new System.Windows.Documents.Run("  –  ") { Foreground = Dim });
        scoreText.Inlines.Add(new System.Windows.Documents.Run(rk.ToString()) { Foreground = new SolidColorBrush(RedTeam) });
        double diff = R.GoldDiffAt(t);
        goldText.Text = Math.Abs(diff) < 100 ? "Gold even" : $"{(diff > 0 ? "Blue" : "Red")} +{Math.Abs(diff) / 1000:0.0}k gold";
        goldText.Foreground = new SolidColorBrush(Math.Abs(diff) < 100 ? Colors.Gray : diff > 0 ? BlueTeam : RedTeam);

        foreach (var row in rows)
        {
            var p = row.P;
            var st = R.StateAt(p, t);
            var lp = live ? R.Live.Players.FirstOrDefault(x => x.ChampionKey == p.ChampionKey) : null;
            int k = lp?.Kills ?? g.Events.Count(e => e.Type == "CHAMPION_KILL" && e.Time <= t && e.KillerId == p.Id);
            int d = lp?.Deaths ?? g.Events.Count(e => e.Type == "CHAMPION_KILL" && e.Time <= t && e.VictimId == p.Id);
            int a = lp?.Assists ?? g.Events.Count(e => e.Type == "CHAMPION_KILL" && e.Time <= t && e.Assists.Contains(p.Id));
            row.Kda.Text = $"{k} / {d} / {a}";
            var pf = R.FrameAt(p.Id, t);
            int cs = lp?.CreepScore ?? (pf != null ? pf.MinionsKilled + pf.JungleMinionsKilled : 0);
            row.Cs.Text = $"{cs} CS";
            row.Gold.Text = lp != null ? $"{lp.ItemValue / 1000.0:0.0}k in items" : pf != null ? $"{pf.TotalGold / 1000.0:0.0}k gold" : "";
            row.Level.Text = st.Level.ToString(CultureInfo.InvariantCulture);
            row.Name.Text = st.Dead ? $"{p.ChampionName}  ·  dead {Math.Ceiling(st.RespawnIn)}s" : p.ChampionName;
            row.Root.Opacity = st.Dead ? 0.55 : 1;
            row.Root.Background = map.SelectedId == p.Id ? new SolidColorBrush(Color.FromRgb(44, 50, 66)) : Brushes.Transparent;

            var ids = lp?.Items.Where(i => i > 0).ToArray() ?? Array.Empty<int>();
            string sig = string.Join(",", ids);
            if ((string?)row.Items.Tag != sig)
            {
                row.Items.Tag = sig;
                row.Items.Children.Clear();
                foreach (var id in ids)
                {
                    var img = new Image { Width = 22, Height = 22, Margin = new Thickness(0, 0, 2, 0) };
                    SetImage(img, "item", id.ToString(CultureInfo.InvariantCulture));
                    row.Items.Children.Add(img);
                }
            }
        }
        itemsNote.Text = live ? "" : "Items show when the board is at the replay's moment (they come from the game itself).";
        itemsNote.Visibility = live ? Visibility.Collapsed : Visibility.Visible;

        RefreshTimers(g, t);
        RefreshMatchup();
        RefreshLanes(g, t);
        RefreshJungle(g);
        RefreshWards(g, t);
        RefreshObjectives(g, t);
        RefreshDetails(g, t);
    }

    // ---------------------------------------------------------------- lanes, jungle start, wards (side panel)

    readonly StackPanel lanesPanel = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly StackPanel junglePanel = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly StackPanel wardsPanel = new() { Margin = new Thickness(0, 12, 0, 0) };
    string lanesSig = "", jungleSig = "", wardsSig = "";

    static TextBlock Heading(string text, string? tip = null) =>
        new() { Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(2, 0, 0, 4), ToolTip = tip };

    static UIElement TowerPips(bool[] up, Color team, bool reverse)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var order = reverse ? up.Reverse().ToArray() : up;
        string[] names = { "outer", "inner", "inhibitor" };
        for (int i = 0; i < order.Length; i++)
        {
            int real = reverse ? order.Length - 1 - i : i;
            sp.Children.Add(new Border
            {
                Width = 11, Height = 11, Margin = new Thickness(1.5, 0, 1.5, 0), CornerRadius = new CornerRadius(2.5),
                Background = new SolidColorBrush(order[i] ? team : Color.FromArgb(70, 120, 120, 130)),
                BorderBrush = new SolidColorBrush(order[i] ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(90, 200, 200, 200)), BorderThickness = new Thickness(1),
                ToolTip = $"{names[real]} turret {(order[i] ? "standing" : "destroyed")}",
            });
        }
        return sp;
    }

    void RefreshLanes(GameTimeline g, double t)
    {
        var lanes = RiftInsights.Lanes(g, t);
        bool plates = t < RiftInsights.PlatesFall;
        string sig = string.Join("|", lanes.Select(l => $"{l.BluePlatesTaken},{l.RedPlatesTaken},{string.Join("", l.BlueTowersUp)},{string.Join("", l.RedTowersUp)}")) + plates;
        if (sig == lanesSig) return;
        lanesSig = sig;
        lanesPanel.Children.Clear();
        lanesPanel.Children.Add(Heading("LANES", "Turrets standing in each lane (outer, inner, inhibitor) and turret plates each team has taken. Plates fall off at 14:00."));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < lanes.Count; i++)
        {
            var l = lanes[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            void Put(UIElement e, int col) { Grid.SetRow(e, i); Grid.SetColumn(e, col); grid.Children.Add(e); }
            Put(new TextBlock { Text = l.Lane, Foreground = Fg, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 0);
            Put(TowerPips(l.BlueTowersUp, BlueTeam, false), 1);
            var mid = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            if (plates || l.BluePlatesTaken + l.RedPlatesTaken > 0)
            {
                mid.Inlines.Add(new System.Windows.Documents.Run($"{l.BluePlatesTaken}") { Foreground = new SolidColorBrush(BlueTeam), FontWeight = FontWeights.Bold });
                mid.Inlines.Add(new System.Windows.Documents.Run("  plates  ") { Foreground = Dim });
                mid.Inlines.Add(new System.Windows.Documents.Run($"{l.RedPlatesTaken}") { Foreground = new SolidColorBrush(RedTeam), FontWeight = FontWeights.Bold });
                mid.ToolTip = $"Blue took {l.BluePlatesTaken} of red's {RiftInsights.PlatesPerTurret} {l.Lane.ToLowerInvariant()} plates; red took {l.RedPlatesTaken} of blue's." + (plates ? "" : " Plates fell off at 14:00.");
            }
            Put(mid, 2);
            Put(TowerPips(l.RedTowersUp, RedTeam, true), 3);
        }
        lanesPanel.Children.Add(grid);
        if (plates) lanesPanel.Children.Add(new TextBlock { Text = $"Plates fall off in {Objectives.Clock(RiftInsights.PlatesFall - t)}.", Foreground = Dim, FontSize = 11, Margin = new Thickness(2, 2, 0, 0) });
    }

    void RefreshJungle(GameTimeline g)
    {
        var junglers = RiftInsights.Junglers(g);
        var routes = junglers.Select(j => (j, RiftInsights.Route(g, j.Id))).ToList();
        string sig = string.Join("|", routes.Select(x => x.j.Id + ":" + string.Join(",", x.Item2.Select(v => $"{v.Camp.Short}{v.Arrived:0}"))));
        if (sig == jungleSig) return;
        jungleSig = sig;
        junglePanel.Children.Clear();
        junglePanel.Children.Add(Heading("JUNGLE START", "Each jungler's camps in the first four minutes, from the recorded minimap positions. Turn on Jungle (top bar) to see the route on the map."));
        foreach (var (j, route) in routes)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(2, 1, 0, 3), Cursor = Cursors.Hand };
            tb.Inlines.Add(new System.Windows.Documents.Run(j.ChampionName + "  ") { Foreground = new SolidColorBrush(j.Team == 100 ? BlueTeam : RedTeam), FontWeight = FontWeights.SemiBold });
            if (route.Count == 0)
                tb.Inlines.Add(new System.Windows.Documents.Run("not recorded yet: play 1:30–4:00 of the replay with the minimap visible") { Foreground = Dim });
            else
            {
                string side(JungleCamp c) => c.Side == 0 ? "" : c.Side == j.Team ? "" : " (enemy)";
                tb.Inlines.Add(new System.Windows.Documents.Run(string.Join("  →  ", route.Select(v => $"{v.Camp.Short}{side(v.Camp)} {RiftInsights.Clock(v.Arrived)}"))));
            }
            var id = j.Id;
            tb.MouseLeftButtonUp += (_, _) =>
            {
                map.SelectedId = id;
                if (!map.ShowJungle) { map.ShowJungle = true; jungleBtn.IsChecked = true; jungleBtn.Background = ButtonOn; S.Settings.BoardJungle = true; S.SaveSettingsSoon(); }
                map.InvalidateVisual();
                RefreshSide();
            };
            junglePanel.Children.Add(tb);
        }
    }

    void RefreshWards(GameTimeline g, double t)
    {
        var wards = RiftInsights.Wards(g);
        var placedSoFar = wards.Where(w => w.Placed <= t).ToList();
        string sig = $"{placedSoFar.Count}|{wards.Count(w => w.AliveAt(t))}|{wards.Count(w => w.KilledAt <= t)}";
        if (sig == wardsSig) return;
        wardsSig = sig;
        wardsPanel.Children.Clear();
        wardsPanel.Children.Add(Heading("WARDS", "From Riot's timeline (it needs your Riot API key, or a game loaded from the client). Out = on the map now."));
        if (wards.Count == 0)
        {
            wardsPanel.Children.Add(new TextBlock { Text = "No ward data for this game. Add your Riot API key in ⚙ to get wards.", Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 0) });
            return;
        }
        foreach (int team in new[] { 100, 200 })
        {
            var mine = placedSoFar.Where(w => w.Team == team).ToList();
            int outNow = mine.Count(w => w.AliveAt(t));
            int control = mine.Count(w => w.IsControl);
            int cleared = wards.Count(w => w.Team != team && w.KilledAt <= t);
            var tb = new TextBlock { FontSize = 12, Margin = new Thickness(2, 1, 0, 1), TextWrapping = TextWrapping.Wrap };
            tb.Inlines.Add(new System.Windows.Documents.Run(team == 100 ? "Blue  " : "Red  ") { Foreground = new SolidColorBrush(team == 100 ? BlueTeam : RedTeam), FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new System.Windows.Documents.Run($"{mine.Count} placed ({control} control) · {outNow} out now · cleared {cleared}"));
            wardsPanel.Children.Add(tb);
        }
    }

    void RefreshObjectives(GameTimeline g, double t)
    {
        objectives.Children.Clear();
        objectives.Children.Add(new TextBlock { Text = "OBJECTIVES", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(2, 0, 0, 4) });
        foreach (int team in new[] { 100, 200 })
        {
            int TeamOf(GameEvent e) => g.P(e.KillerId)?.Team ?? (e.TeamId == 100 ? 200 : e.TeamId == 200 ? 100 : 0);
            var done = g.Events.Where(e => e.Time <= t && TeamOf(e) == team).ToList();
            var drakes = done.Where(e => e.Type == "ELITE_MONSTER_KILL" && e.MonsterType == "DRAGON").ToList();
            int barons = done.Count(e => e.MonsterType == "BARON_NASHOR");
            int heralds = done.Count(e => e.MonsterType == "RIFTHERALD");
            int grubs = done.Count(e => e.MonsterType == "HORDE");
            int atakhan = done.Count(e => e.MonsterType == "ATAKHAN");
            int towers = done.Count(e => e.Type == "BUILDING_KILL" && e.BuildingType == "TOWER_BUILDING");
            int inhibs = done.Count(e => e.Type == "BUILDING_KILL" && e.BuildingType == "INHIBITOR_BUILDING");
            var parts = new List<string> { $"{towers} towers" };
            if (inhibs > 0) parts.Add($"{inhibs} inhib");
            if (drakes.Count > 0) parts.Add($"{drakes.Count} drakes ({string.Join(", ", drakes.Select(d => (d.MonsterSubType ?? "").Replace("_DRAGON", "").ToLowerInvariant()))})");
            if (grubs > 0) parts.Add($"{grubs} grubs");
            if (heralds > 0) parts.Add($"{heralds} herald");
            if (atakhan > 0) parts.Add("Atakhan");
            if (barons > 0) parts.Add($"{barons} baron");
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(2, 1, 0, 1) };
            tb.Inlines.Add(new System.Windows.Documents.Run(team == 100 ? "Blue  " : "Red  ") { Foreground = new SolidColorBrush(team == 100 ? BlueTeam : RedTeam), FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new System.Windows.Documents.Run(string.Join(" · ", parts)));
            objectives.Children.Add(tb);
        }
    }

    void RefreshDetails(GameTimeline g, double t)
    {
        details.Children.Clear();
        if (map.SelectedId is not int id || g.P(id) is not { } p)
        {
            details.Children.Add(new TextBlock { Text = "Click a champion (on the map or above) for details. Drag champions on the map to explain a play; Reset puts them back. Double-click the map to move the replay camera there.",
                Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
            return;
        }
        var st = R.StateAt(p, t);
        var pf = R.FrameAt(id, t);
        details.Children.Add(new TextBlock { Text = p.ChampionName.ToUpperInvariant(), Foreground = new SolidColorBrush(p.Team == 100 ? BlueTeam : RedTeam), FontWeight = FontWeights.Bold, FontSize = 12 });
        if (!string.IsNullOrEmpty(p.RiotId)) details.Children.Add(new TextBlock { Text = p.RiotId + (p.Lane.Length > 0 ? $" · {p.Lane}" : ""), Foreground = Dim, FontSize = 11 });
        void Line(string label, string value)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var v = new TextBlock { Text = value, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(v, Dock.Right);
            dp.Children.Add(v);
            dp.Children.Add(new TextBlock { Text = label, Foreground = Dim });
            details.Children.Add(dp);
        }
        Line("Level", st.Level.ToString(CultureInfo.InvariantCulture));
        var myWards = RiftInsights.Wards(g).Where(w => w.Creator == id && w.Placed <= t).ToList();
        if (RiftInsights.Wards(g).Count > 0)
            Line("Wards placed / cleared", $"{myWards.Count} / {RiftInsights.Wards(g).Count(w => w.KilledBy == id && w.KilledAt <= t)}");
        Line("Position", st.Dead ? $"dead · back in {Math.Ceiling(st.RespawnIn)}s" : st.Quality switch { PosQuality.Tracked => "from the minimap", PosQuality.LastSeen => "where last seen on the minimap", _ => "estimated from the timeline" });
        if (pf != null)
        {
            Line($"At {AppState.FormatTime(Math.Floor(t / 60) * 60)} (last timeline minute)", "");
            Line("   Gold earned", $"{pf.TotalGold:N0}");
            Line("   Gold in pocket", $"{pf.CurrentGold:N0}");
            Line("   CS (lane + jungle)", $"{pf.MinionsKilled} + {pf.JungleMinionsKilled}");
            Line("   XP", $"{pf.Xp:N0}");
            if (pf.DamageToChampions is int dmg) Line("   Damage to champions", $"{dmg:N0}");
        }

        // With a Riot API key: skill order and items bought so far.
        var skills = g.Events.Where(e => e.Type == "SKILL_LEVEL_UP" && e.ParticipantId == id && e.Time <= t).Select(e => "QWER"[Math.Clamp(e.SkillSlot - 1, 0, 3)]).ToList();
        if (skills.Count > 0) Line("Skills", string.Join(" ", skills));
        var buys = g.Events.Where(e => e.Type == "ITEM_PURCHASED" && e.ParticipantId == id && e.Time <= t).ToList();
        if (buys.Count > 0)
        {
            details.Children.Add(new TextBlock { Text = "Bought so far", Foreground = Dim, Margin = new Thickness(0, 6, 0, 2) });
            var wrap = new WrapPanel();
            foreach (var b in buys.TakeLast(24))
            {
                var img = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 3, 3), ToolTip = AppState.FormatTime(b.Time) };
                SetImage(img, "item", b.ItemId.ToString(CultureInfo.InvariantCulture));
                wrap.Children.Add(img);
            }
            details.Children.Add(wrap);
        }
        else if (!g.HasItemEvents)
            details.Children.Add(new TextBlock { Text = "Add a Riot API key (⚙) for item purchases and skill order.", Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        details.Children.Add(new TextBlock { Text = $"End of game: {p.Kills}/{p.Deaths}/{p.Assists}, {p.FinalCs} CS, {p.DamageToChampions:N0} damage, vision {p.VisionScore}", Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
    }

    // ---------------------------------------------------------------- self-test hooks

    internal static RiftBoardWindow? Instance => instance;

    internal void TestSelect(int? id) { map.SelectedId = id; RefreshAll(); map.InvalidateVisual(); }

    internal void TestLayers(bool wards, bool jungle, bool timers)
    {
        map.ShowWards = wards; map.ShowJungle = jungle; map.ShowObjectives = timers;
        RefreshAll();
        map.InvalidateVisual();
    }

    internal void TestView(double zoom, Point centre) { map.SetView(zoom, centre); ink.InvalidateVisual(); }

    internal string MapVariant => map.Variant;
    internal DrawCanvas TestInk => ink;
    internal Point TestWorldToView(Point w) => map.WorldToView(w);
    internal void TestDrawing(bool on) => SetDrawing(on);

    internal void TestPaths(BoardPathMode mode, double window, string lane)
    {
        map.PathMode = mode;
        map.PathWindow = window;
        S.Settings.BoardMatchupLane = lane;
        SyncChoiceButtons();
        UpdatePathsButton();
        RefreshAll();
        map.InvalidateVisual();
    }

    internal void TestSandbox(double t) { SetLinked(false); sandboxTime = t; RefreshAll(); map.InvalidateVisual(); timeline.InvalidateVisual(); }

    internal void TestMove(int id, Point world) { map.Moved[id] = world; map.MovedAt = Now(); map.InvalidateVisual(); }

    internal void TestDraw(DrawItem item) { S.Stamp(item, Now()); S.AddItem(item); ink.InvalidateVisual(); }

    /// <summary>The whole side panel (scoreboard, timers, matchup…) as one image, scrolled parts included.</summary>
    internal void SnapshotSide(string path)
    {
        side.UpdateLayout();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var rtb = new RenderTargetBitmap((int)(side.ActualWidth * dpi) + 24, (int)(side.ActualHeight * dpi) + 24, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(PanelBack, null, new Rect(0, 0, side.ActualWidth + 24, side.ActualHeight + 24));
            dc.DrawRectangle(new VisualBrush(side), null, new Rect(12, 12, side.ActualWidth, side.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    internal void Snapshot(string path)
    {
        UpdateLayout();
        var el = (FrameworkElement)Content;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var rtb = new RenderTargetBitmap((int)(el.ActualWidth * dpi), (int)(el.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    // ---------------------------------------------------------------- settings

    void OpenSettings()
    {
        var dlg = new Window
        {
            Title = "Rift Board settings", Owner = this, Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = PanelBack, Foreground = Fg, FontFamily = FontFamily,
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = "Riot API key (optional)", FontWeight = FontWeights.SemiBold });
        sp.Children.Add(new TextBlock
        {
            Text = "Without a key, game data comes from your League client (kills, towers, objectives, per-minute gold and positions). " +
                   "A key from developer.riotgames.com adds item purchases, skill order and more. Development keys expire after 24 hours.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6), FontSize = 12,
        });
        var key = new PasswordBox { Password = S.Settings.RiotApiKey ?? "", Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(key);
        var track = new CheckBox { Content = "Record champion positions from the minimap while a replay plays", IsChecked = S.Settings.TrackMinimap, Foreground = Fg, Margin = new Thickness(0, 0, 0, 4) };
        sp.Children.Add(track);
        sp.Children.Add(new TextBlock
        {
            Text = "Reads League's minimap (the screen only, nothing in the game's memory) a few times a second. Keep the minimap visible; " +
                   "positions are saved per game, so anything you've watched once can be scrubbed later.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(22, 0, 0, 12),
        });
        var avoid = new CheckBox { Content = "Open the Rift Board on a screen League isn't on", IsChecked = S.Settings.BoardAvoidLeagueScreen, Foreground = Fg, Margin = new Thickness(0, 0, 0, 4) };
        sp.Children.Add(avoid);
        sp.Children.Add(new TextBlock
        {
            Text = "With more than one monitor, the board opens maximized on another screen so it never covers League's minimap (which pauses recording).",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(22, 0, 0, 12),
        });
        sp.Children.Add(new TextBlock { Text = "Learned icons", FontWeight = FontWeights.SemiBold });
        var iconsInfo = new TextBlock
        {
            Text = $"RiftPen remembers what each champion's minimap icon looks like (including custom skins) for {IconLibrary.Shared.ChampionCount} champions, " +
                   "so new replays recognise them right away. Forget them if champions keep getting mixed up.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 6),
        };
        sp.Children.Add(iconsInfo);
        var forget = MakeButton("", "Forget every learned icon; they're learned again as replays play", (_, _) =>
        {
            if (MessageBox.Show(dlg, "Forget all learned champion icons? RiftPen will learn them again as you watch replays.", "Rift Board",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            IconLibrary.Shared.Clear();
            R.Reload();
            iconsInfo.Text = "Learned icons forgotten. They'll be learned again as replays play.";
        }, "Forget learned icons");
        forget.HorizontalAlignment = HorizontalAlignment.Left;
        forget.Margin = new Thickness(0, 0, 0, 14);
        sp.Children.Add(forget);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = MakeButton("", "", (_, _) =>
        {
            string newKey = key.Password.Trim();
            bool keyChanged = newKey != (S.Settings.RiotApiKey ?? "");
            S.Settings.RiotApiKey = newKey.Length > 0 ? newKey : null;
            S.Settings.TrackMinimap = track.IsChecked == true;
            S.Settings.BoardAvoidLeagueScreen = avoid.IsChecked == true;
            S.Settings.Save();
            if (keyChanged) R.Reload();
            dlg.Close();
        }, "Save");
        save.IsDefault = true;
        buttons.Children.Add(MakeButton("", "", (_, _) => dlg.Close(), "Cancel"));
        buttons.Children.Add(save);
        sp.Children.Add(buttons);
        dlg.Content = sp;
        dlg.ShowDialog();
    }
}

static class TextBlockExtensions
{
    /// <summary>Digits all the same width, so the clock doesn't jiggle.</summary>
    public static void FontFeatures(this TextBlock tb) => System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
}
