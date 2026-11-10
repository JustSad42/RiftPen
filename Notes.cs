using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RiftPen;

/// <summary>Drawings made at (nearly) the same replay moment form one note.</summary>
public sealed record NoteGroup(double Time, List<DrawItem> Items)
{
    public Color Color => Items[0].Color;
    public string? Title => Items.Select(i => i.Note).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
}

public static class NoteGroups
{
    public static List<NoteGroup> For(AppState s)
    {
        var result = new List<NoteGroup>();
        if (s.ReplayKey == null) return result;
        var items = s.ContextItems.Where(i => i.ReplayKey != null && i.TimeStart != null).OrderBy(i => i.TimeStart).ToList();
        foreach (var it in items)
        {
            if (result.Count > 0 && it.TimeStart!.Value - result[^1].Items[^1].TimeStart!.Value <= 0.5) result[^1].Items.Add(it);
            else result.Add(new NoteGroup(it.TimeStart!.Value, new List<DrawItem> { it }));
        }
        return result;
    }

    /// <summary>Short summary like "2 arrows, Champion vision 1350, “ward here”".</summary>
    public static string Describe(NoteGroup g)
    {
        var parts = new List<string>();
        foreach (var grp in g.Items.Where(i => i.Kind is not (ToolKind.Text or ToolKind.Range)).GroupBy(i => i.Kind))
        {
            string name = grp.Key switch
            {
                ToolKind.Ellipse => "circle",
                ToolKind.Marker => "marker",
                _ => grp.Key.ToString().ToLowerInvariant(),
            };
            parts.Add(grp.Count() == 1 ? name : $"{grp.Count()} {name}s");
        }
        parts.AddRange(g.Items.Where(i => i.Kind == ToolKind.Range).Select(i => i.Text ?? $"range {i.Radius:0}"));
        parts.AddRange(g.Items.Where(i => i.Kind == ToolKind.Text).Select(i => $"“{i.Text}”"));
        return string.Join(", ", parts);
    }
}

/// <summary>Thin bar over the League window showing where this replay's notes are. Click a dot to jump there.</summary>
public sealed class TimelineWindow : Window
{
    readonly AppState S = AppState.I;
    readonly TimelineBar bar = new();
    IntPtr hwnd;
    Native.RECT league;
    bool positioned;

    public event Action? OpenNotesRequested;

    public TimelineWindow()
    {
        Title = "RiftPen timeline";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 600;
        Height = 34;

        var grip = new TextBlock
        {
            Text = "⋮", FontSize = 16, Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0), Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move the timeline",
        };
        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            DragMove();
            SavePosition();
        };
        var list = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
            Cursor = Cursors.Hand, ToolTip = "Notes list",
        };
        list.MouseLeftButtonDown += (_, e) => { OpenNotesRequested?.Invoke(); e.Handled = true; };

        var dock = new DockPanel();
        DockPanel.SetDock(grip, Dock.Left);
        DockPanel.SetDock(list, Dock.Right);
        dock.Children.Add(grip);
        dock.Children.Add(list);
        dock.Children.Add(bar);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 27, 28, 33)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            Child = dock,
        };

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.SetExStyle(hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW, 0);
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { if (IsVisible) bar.InvalidateVisual(); };
        timer.Start();
        S.ItemsChanged += bar.InvalidateVisual;
    }

    /// <summary>Called by the overlay: shows/hides and keeps the strip over the League window.</summary>
    internal void Update(bool leagueFound, Native.RECT leagueRect, bool focusOk)
    {
        bool show = S.Settings.ShowNotesTimeline && leagueFound && focusOk && !S.Hidden && S.ReplayKey != null;
        if (show != IsVisible)
        {
            if (show) { Show(); positioned = false; }
            else Hide();
        }
        if (!show || hwnd == IntPtr.Zero) return;
        if (!positioned || !leagueRect.Equals(league))
        {
            league = leagueRect;
            Position();
        }
    }

    public void ResetPosition()
    {
        S.Settings.TimelineStripPos = new[] { 0.5, 0.09 };
        positioned = false;
    }

    void Position()
    {
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var pos = S.Settings.TimelineStripPos is { Length: 2 } p ? p : new[] { 0.5, 0.09 };
        int w = Math.Max(300, (int)(league.Width * 0.42));
        int h = (int)(34 * s);
        int x = league.Left + (int)(pos[0] * league.Width) - w / 2;
        int y = league.Top + (int)(pos[1] * league.Height);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        positioned = true;
    }

    void SavePosition()
    {
        if (league.Width <= 0 || !Native.GetWindowRect(hwnd, out var r)) return;
        S.Settings.TimelineStripPos = new[]
        {
            Math.Clamp((r.Left + r.Width / 2.0 - league.Left) / league.Width, 0, 1),
            Math.Clamp((r.Top - league.Top) / (double)league.Height, 0, 0.97),
        };
        S.Settings.Save();
    }
}

sealed class TimelineBar : FrameworkElement
{
    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    readonly AppState S = AppState.I;
    Point? mouse;
    const double TextArea = 150;

    public TimelineBar()
    {
        Cursor = Cursors.Hand;
        ToolTip = "Click a dot to jump to that note, or anywhere on the bar to seek";
    }

    Rect Track => new(6, ActualHeight / 2 - 3, Math.Max(10, ActualWidth - TextArea - 12), 6);

    NoteGroup? Near(Point p, List<NoteGroup> groups, double length)
    {
        var t = Track;
        NoteGroup? best = null;
        double bestD = 8;
        foreach (var g in groups)
        {
            double d = Math.Abs(t.X + g.Time / length * t.Width - p.X);
            if (d < bestD) { bestD = d; best = g; }
        }
        return best;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // catch clicks everywhere
        var pb = S.Replay.Playback;
        double length = pb?.Length ?? 0;
        if (length <= 0) return;
        double now = S.ReplayTime ?? 0;
        var t = Track;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, t, 3, 3);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(140, 58, 111, 247)), null,
            new Rect(t.X, t.Y, Math.Clamp(now / length, 0, 1) * t.Width, t.Height), 3, 3);

        var groups = NoteGroups.For(S);
        var hot = mouse is Point m ? Near(m, groups, length) : null;
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(18, 18, 22)), 1.5);
        foreach (var g in groups)
        {
            double x = t.X + g.Time / length * t.Width;
            double r = g == hot ? 7 : 5;
            dc.DrawEllipse(new SolidColorBrush(g.Color), outline, new Point(x, t.Y + t.Height / 2), r, r);
        }
        double px = t.X + Math.Clamp(now / length, 0, 1) * t.Width;
        dc.DrawLine(new Pen(Brushes.White, 2), new Point(px, 4), new Point(px, ActualHeight - 4));

        string text = hot != null
            ? $"{AppState.FormatTime(hot.Time)} · {hot.Title ?? $"{hot.Items.Count} drawing{(hot.Items.Count == 1 ? "" : "s")}"}"
            : $"{AppState.FormatTime(now)} / {AppState.FormatTime(length)}  ·  {groups.Count} note{(groups.Count == 1 ? "" : "s")}";
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11.5, Brushes.White, ppd)
            { MaxTextWidth = TextArea - 6, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(ft, new Point(ActualWidth - TextArea + 4, (ActualHeight - ft.Height) / 2));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        mouse = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        var pb = S.Replay.Playback;
        if (pb == null || pb.Length <= 0) return;
        var p = e.GetPosition(this);
        if (Near(p, NoteGroups.For(S), pb.Length) is { } g)
        {
            S.Replay.Seek(g.Time + 0.05);
            S.Say($"Note at {AppState.FormatTime(g.Time)}");
            return;
        }
        var t = Track;
        if (p.X >= t.X - 4 && p.X <= t.Right + 4)
            S.Replay.Seek(Math.Clamp((p.X - t.X) / t.Width, 0, 1) * pb.Length);
    }
}

/// <summary>Every note in the open replay: jump to it, give it a title, delete it, or copy the list as text.</summary>
public sealed class NotesWindow : Window
{
    readonly AppState S = AppState.I;
    readonly StackPanel list = new();
    readonly TextBlock header = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
    string signature = "";

    public NotesWindow()
    {
        Title = "Replay notes";
        Width = 580;
        Height = 620;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var copy = new Button { Content = "Copy list as text", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => CopyAsText();
        var close = new Button { Content = "Close", Padding = new Thickness(12, 4, 12, 4) };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var hint = new TextBlock
        {
            Text = "Click a time to jump there. Type a title for a note so it's easy to find later.",
            Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        var dock = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(hint);
        dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
        Content = dock;

        S.ItemsChanged += Rebuild;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) => Rebuild(); // picks up a replay being opened or closed
        timer.Start();
        Closed += (_, _) => { S.ItemsChanged -= Rebuild; timer.Stop(); };
        Rebuild();
    }

    void Rebuild()
    {
        var groups = NoteGroups.For(S);
        var sig = $"{S.ReplayKey}|{string.Join(",", groups.Select(g => $"{g.Time:0.0}:{g.Items.Count}"))}";
        if (sig == signature) return;
        signature = sig;

        list.Children.Clear();
        if (S.ReplayKey == null)
        {
            header.Text = "Open a replay to see its notes";
            return;
        }
        header.Text = $"Notes for this replay ({groups.Count})";
        if (groups.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "No notes yet. Draw something while the replay is open (with timeline notes on).", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
            return;
        }

        foreach (var g in groups)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var jump = new Button
            {
                Content = $"▶ {AppState.FormatTime(g.Time)}", Padding = new Thickness(6, 3, 6, 3), VerticalAlignment = VerticalAlignment.Top,
                BorderBrush = new SolidColorBrush(g.Color), BorderThickness = new Thickness(0, 0, 0, 3),
            };
            jump.Click += (_, _) => S.Replay.Seek(g.Time + 0.05);
            row.Children.Add(jump);

            var title = new TextBox { Text = g.Title ?? "", Padding = new Thickness(3), FontWeight = FontWeights.SemiBold };
            title.LostFocus += (_, _) =>
            {
                var text = string.IsNullOrWhiteSpace(title.Text) ? null : title.Text.Trim();
                foreach (var it in g.Items) it.Note = text;
                S.SaveSoon();
            };
            title.KeyDown += (_, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
            var info = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
            info.Children.Add(title);
            info.Children.Add(new TextBlock { Text = NoteGroups.Describe(g), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 3, 0, 0) });
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            var delete = new Button { Content = "Delete", Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Delete this note (Undo brings it back)" };
            delete.Click += (_, _) => S.RemoveItems(g.Items);
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);

            list.Children.Add(row);
        }
    }

    void CopyAsText()
    {
        var lines = NoteGroups.For(S).Select(g =>
            $"{AppState.FormatTime(g.Time)}  {(g.Title != null ? g.Title + " — " : "")}{NoteGroups.Describe(g)}");
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); S.Say("Notes copied to the clipboard."); } catch { }
    }
}
