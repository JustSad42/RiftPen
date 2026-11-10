using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RiftPen;

/// <summary>
/// The Rift Board's timeline: team gold difference over the game, kills and objectives as ticks, the stretches
/// where positions were recorded from the minimap, and a playhead you drag to move through the game.
/// </summary>
public sealed class BoardTimeline : FrameworkElement
{
    static readonly Color BlueTeam = Color.FromRgb(0, 153, 224), RedTeam = Color.FromRgb(232, 61, 61);
    static readonly Typeface Face = new("Segoe UI");
    readonly RiftSession R = RiftSession.I;

    public Func<double>? TimeSource { get; set; }
    /// <summary>(time, finished): fired while dragging, and once more when the mouse is let go.</summary>
    public event Action<double, bool>? Scrub;

    List<(double Start, double End)> coverage = new();
    DateTime coverageAt;
    const double Pad = 10;

    public BoardTimeline()
    {
        Height = 108;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    double Length => R.Game?.Length is > 0 and var l ? l : AppState.I.Replay.Playback?.Length ?? 0;

    double X(double t) => Pad + t / Math.Max(1, Length) * (ActualWidth - 2 * Pad);
    double T(double x) => Math.Clamp((x - Pad) / Math.Max(1, ActualWidth - 2 * Pad) * Length, 0, Length);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Length <= 0) return;
        CaptureMouse();
        Scrub?.Invoke(T(e.GetPosition(this).X), false);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Scrub?.Invoke(T(e.GetPosition(this).X), false);
        hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        hoverX = null;
        InvalidateVisual();
    }

    double? hoverX;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        Scrub?.Invoke(T(e.GetPosition(this).X), true);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 22, 28)), null, new Rect(size));
        var game = R.Game;
        double len = Length;
        if (len <= 0) return;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Gold difference (blue above the line, red below).
        double graphTop = 6, graphH = 46, mid = graphTop + graphH / 2;
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1), new Point(Pad, mid), new Point(size.Width - Pad, mid));
        if (game is { Frames.Count: > 1 })
        {
            var diffs = game.Frames.Select(f => (f.Time, D: R.GoldDiffAt(f.Time))).ToList();
            double max = Math.Max(2000, diffs.Max(d => Math.Abs(d.D)));
            foreach (var (team, sign) in new[] { (BlueTeam, 1), (RedTeam, -1) })
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(X(diffs[0].Time), mid), true, true);
                    foreach (var (t, d) in diffs)
                        ctx.LineTo(new Point(X(t), mid - Math.Max(0, d * sign) / max * (graphH / 2) * sign), true, true);
                    ctx.LineTo(new Point(X(diffs[^1].Time), mid), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, team.R, team.G, team.B)), null, g);
            }
            var lbl = new FormattedText($"±{max / 1000:0.#}k gold", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), ppd);
            dc.DrawText(lbl, new Point(Pad + 2, graphTop - 2));
        }

        // Recorded stretches (positions read from the minimap) under the graph.
        double covY = graphTop + graphH + 4;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 44, 52)), null, new Rect(Pad, covY, size.Width - 2 * Pad, 4));
        if (R.Tracks != null && (DateTime.Now - coverageAt).TotalSeconds > 2)
        {
            coverage = R.Tracks.Coverage();
            coverageAt = DateTime.Now;
        }
        foreach (var (a, b) in coverage)
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(120, 200, 120)), null, new Rect(X(a), covY, Math.Max(1.5, X(b) - X(a)), 4));

        // Events
        double evY = covY + 13;
        if (game != null)
            foreach (var e in game.Events)
            {
                int team = game.P(e.KillerId)?.Team ?? (e.TeamId == 100 ? 200 : e.TeamId == 200 ? 100 : 0);
                var c = team == 100 ? BlueTeam : team == 200 ? RedTeam : Colors.Gold;
                var brush = new SolidColorBrush(c);
                double x = X(e.Time);
                switch (e.Type)
                {
                    case "CHAMPION_KILL":
                        dc.DrawEllipse(brush, null, new Point(x, evY + (team == 100 ? -3 : 3)), 2.6, 2.6);
                        break;
                    case "ELITE_MONSTER_KILL":
                        var g = new StreamGeometry();
                        using (var ctx = g.Open())
                        {
                            double y = evY + 15;
                            ctx.BeginFigure(new Point(x, y - 5), true, true);
                            ctx.PolyLineTo(new[] { new Point(x + 5, y), new Point(x, y + 5), new Point(x - 5, y) }, true, true);
                        }
                        dc.DrawGeometry(brush, new Pen(Brushes.Black, 0.8), g);
                        break;
                    case "BUILDING_KILL":
                        dc.DrawRectangle(brush, new Pen(Brushes.Black, 0.8), new Rect(x - 3, evY + 12, 6, 6));
                        break;
                }
            }

        // Minute labels
        double step = len > 1800 ? 300 : 180;
        for (double t = step; t < len; t += step)
        {
            var ft = new FormattedText(AppState.FormatTime(t), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), ppd);
            dc.DrawText(ft, new Point(X(t) - ft.Width / 2, size.Height - ft.Height - 2));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 1), new Point(X(t), graphTop), new Point(X(t), graphTop + graphH));
        }

        if (hoverX is double hx && !IsMouseCaptured)
        {
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1), new Point(hx, 0), new Point(hx, size.Height));
            var ft = new FormattedText(AppState.FormatTime(T(hx)), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, Brushes.White, ppd);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 30, 32, 40)), null, new Rect(hx + 4, 2, ft.Width + 8, ft.Height + 2), 3, 3);
            dc.DrawText(ft, new Point(hx + 8, 3));
        }

        // Playhead
        double now = TimeSource?.Invoke() ?? 0;
        double px = X(now);
        dc.DrawLine(new Pen(Brushes.White, 2), new Point(px, 0), new Point(px, size.Height));
        dc.DrawEllipse(Brushes.White, null, new Point(px, 3), 5, 5);
    }
}
