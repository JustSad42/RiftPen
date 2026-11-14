using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RiftPen;

/// <summary>Simple vector map markers (original artwork, no game assets).</summary>
public static class MarkerRenderer
{
    public static readonly (MarkerKind Kind, string Name)[] All =
    {
        (MarkerKind.Ward, "Ward"),
        (MarkerKind.ControlWard, "Control ward"),
        (MarkerKind.FarsightWard, "Farsight ward"),
        (MarkerKind.Sweep, "Sweep (Oracle Lens)"),
        (MarkerKind.Flash, "Flash"),
        (MarkerKind.Teleport, "Teleport"),
        (MarkerKind.Recall, "Recall / back"),
        (MarkerKind.Dragon, "Dragon"),
        (MarkerKind.Baron, "Baron"),
        (MarkerKind.Herald, "Rift Herald"),
        (MarkerKind.Grubs, "Void Grubs"),
        (MarkerKind.Danger, "Danger"),
        (MarkerKind.Missing, "Enemy missing"),
        (MarkerKind.OnMyWay, "On my way"),
        (MarkerKind.Target, "Target / focus"),
        (MarkerKind.Objective, "Objective"),
        (MarkerKind.BlueDot, "Blue team"),
        (MarkerKind.RedDot, "Red team"),
    };

    /// <summary>Headings for the stamp menu (stamps in <see cref="All"/> are already in heading order).</summary>
    public static string Group(MarkerKind k) => k switch
    {
        MarkerKind.Ward or MarkerKind.ControlWard or MarkerKind.FarsightWard or MarkerKind.Sweep => "Vision",
        MarkerKind.Flash or MarkerKind.Teleport or MarkerKind.Recall => "Spells",
        MarkerKind.Dragon or MarkerKind.Baron or MarkerKind.Herald or MarkerKind.Grubs => "Objectives",
        _ => "Calls",
    };

    static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
    static readonly Brush Dark = B(18, 18, 22), White = B(255, 255, 255), Yellow = B(242, 201, 76), Pink = B(232, 72, 140),
        Red = B(235, 64, 52), Blue = B(52, 120, 246), Purple = B(155, 89, 230), TeamBlue = B(40, 110, 255), TeamRed = B(230, 50, 60),
        Lens = B(214, 48, 49), LensLight = B(255, 205, 205), Spell = B(255, 196, 0), Tp = B(142, 68, 230), Home = B(46, 134, 222),
        Drake = B(242, 120, 40), Nashor = B(120, 60, 200), HeraldC = B(171, 71, 188), Grub = B(126, 87, 194), Farsight = B(64, 156, 255);

    static Brush B(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }

    public static void Draw(DrawingContext dc, MarkerKind kind, Point c, double size, double pixelsPerDip)
    {
        double r = size / 2;
        var outline = new Pen(Dark, Math.Max(1.5, size * 0.08)) { LineJoin = PenLineJoin.Round };

        switch (kind)
        {
            case MarkerKind.Ward:
                dc.DrawEllipse(Yellow, outline, c, r, r);
                Eye(dc, c, r);
                break;

            case MarkerKind.FarsightWard:
                dc.DrawEllipse(Farsight, outline, c, r, r);
                Eye(dc, c, r);
                break;

            case MarkerKind.Sweep:
            {
                dc.DrawEllipse(Lens, outline, c, r, r);
                // A radar sweep: a lit wedge inside a ring.
                dc.DrawEllipse(null, new Pen(White, size * 0.08), c, r * 0.58, r * 0.58);
                dc.DrawGeometry(LensLight, null, Poly(true, c,
                    new Point(c.X + r * 0.58 * Math.Cos(-1.9), c.Y + r * 0.58 * Math.Sin(-1.9)),
                    new Point(c.X + r * 0.58 * Math.Cos(-1.2), c.Y + r * 0.58 * Math.Sin(-1.2)),
                    new Point(c.X + r * 0.58 * Math.Cos(-0.5), c.Y + r * 0.58 * Math.Sin(-0.5))));
                dc.DrawEllipse(White, null, c, r * 0.13, r * 0.13);
                break;
            }

            case MarkerKind.Flash:
            {
                dc.DrawEllipse(Spell, outline, c, r, r);
                var pts = new Point[16];
                for (int i = 0; i < 16; i++)
                {
                    double a = -Math.PI / 2 + i * Math.PI / 8, rr = i % 2 == 0 ? r * 0.72 : r * 0.34;
                    pts[i] = new Point(c.X + rr * Math.Cos(a), c.Y + rr * Math.Sin(a));
                }
                dc.DrawGeometry(White, null, Poly(true, pts));
                dc.DrawEllipse(Spell, null, c, r * 0.16, r * 0.16);
                break;
            }

            case MarkerKind.Teleport:
            {
                dc.DrawEllipse(Tp, outline, c, r, r);
                var swirl = new Pen(White, size * 0.09) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawGeometry(null, swirl, Spiral(c, r * 0.68, 0));
                dc.DrawGeometry(null, swirl, Spiral(c, r * 0.68, Math.PI));
                break;
            }

            case MarkerKind.Recall:
            {
                dc.DrawEllipse(Home, outline, c, r, r);
                double w = r * 0.5;
                dc.DrawGeometry(White, null, Poly(true,
                    new Point(c.X, c.Y - r * 0.58), new Point(c.X + w * 1.15, c.Y - r * 0.02), new Point(c.X + w * 0.78, c.Y - r * 0.02),
                    new Point(c.X + w * 0.78, c.Y + r * 0.5), new Point(c.X - w * 0.78, c.Y + r * 0.5),
                    new Point(c.X - w * 0.78, c.Y - r * 0.02), new Point(c.X - w * 1.15, c.Y - r * 0.02)));
                dc.DrawRectangle(Home, null, new Rect(c.X - w * 0.24, c.Y + r * 0.12, w * 0.48, r * 0.38));
                break;
            }

            case MarkerKind.Dragon:
            {
                dc.DrawEllipse(Drake, outline, c, r, r);
                // A flame.
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(c.X, c.Y - r * 0.66), true, true);
                    ctx.BezierTo(new Point(c.X + r * 0.25, c.Y - r * 0.3), new Point(c.X + r * 0.62, c.Y - r * 0.05), new Point(c.X + r * 0.5, c.Y + r * 0.28), true, true);
                    ctx.BezierTo(new Point(c.X + r * 0.4, c.Y + r * 0.6), new Point(c.X - r * 0.4, c.Y + r * 0.6), new Point(c.X - r * 0.5, c.Y + r * 0.28), true, true);
                    ctx.BezierTo(new Point(c.X - r * 0.6, c.Y - r * 0.02), new Point(c.X - r * 0.3, c.Y - r * 0.15), new Point(c.X - r * 0.22, c.Y - r * 0.38), true, true);
                    ctx.BezierTo(new Point(c.X - r * 0.1, c.Y - r * 0.2), new Point(c.X - r * 0.05, c.Y - r * 0.45), new Point(c.X, c.Y - r * 0.66), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(White, null, g);
                dc.DrawEllipse(Drake, null, new Point(c.X, c.Y + r * 0.22), r * 0.2, r * 0.24);
                break;
            }

            case MarkerKind.Baron:
                dc.DrawEllipse(Nashor, outline, c, r, r);
                // Two horns over a slit eye.
                dc.DrawGeometry(White, null, Poly(true, new Point(c.X - r * 0.55, c.Y - r * 0.1), new Point(c.X - r * 0.42, c.Y - r * 0.7), new Point(c.X - r * 0.18, c.Y - r * 0.22)));
                dc.DrawGeometry(White, null, Poly(true, new Point(c.X + r * 0.55, c.Y - r * 0.1), new Point(c.X + r * 0.42, c.Y - r * 0.7), new Point(c.X + r * 0.18, c.Y - r * 0.22)));
                dc.DrawEllipse(White, null, new Point(c.X, c.Y + r * 0.18), r * 0.5, r * 0.3);
                dc.DrawEllipse(Nashor, null, new Point(c.X, c.Y + r * 0.18), r * 0.09, r * 0.26);
                break;

            case MarkerKind.Herald:
                dc.DrawEllipse(HeraldC, outline, c, r, r);
                dc.DrawGeometry(White, null, Poly(true, new Point(c.X - r * 0.7, c.Y), new Point(c.X - r * 0.25, c.Y - r * 0.42), new Point(c.X + r * 0.25, c.Y - r * 0.42),
                    new Point(c.X + r * 0.7, c.Y), new Point(c.X + r * 0.25, c.Y + r * 0.42), new Point(c.X - r * 0.25, c.Y + r * 0.42)));
                dc.DrawEllipse(HeraldC, null, c, r * 0.2, r * 0.3);
                break;

            case MarkerKind.Grubs:
                dc.DrawEllipse(Grub, outline, c, r, r);
                dc.DrawEllipse(White, null, new Point(c.X, c.Y - r * 0.3), r * 0.22, r * 0.22);
                dc.DrawEllipse(White, null, new Point(c.X - r * 0.34, c.Y + r * 0.27), r * 0.22, r * 0.22);
                dc.DrawEllipse(White, null, new Point(c.X + r * 0.34, c.Y + r * 0.27), r * 0.22, r * 0.22);
                break;

            case MarkerKind.ControlWard:
                dc.DrawEllipse(Pink, outline, c, r, r);
                Eye(dc, c, r);
                break;

            case MarkerKind.Danger:
                dc.DrawGeometry(Red, outline, Poly(true,
                    new Point(c.X, c.Y - r), new Point(c.X + r * 0.97, c.Y + r * 0.8), new Point(c.X - r * 0.97, c.Y + r * 0.8)));
                Glyph(dc, "!", new Point(c.X, c.Y + r * 0.2), size * 0.55, White, pixelsPerDip);
                break;

            case MarkerKind.Missing:
                dc.DrawEllipse(Yellow, outline, c, r, r);
                Glyph(dc, "?", c, size * 0.62, Dark, pixelsPerDip);
                break;

            case MarkerKind.OnMyWay:
            {
                dc.DrawEllipse(Blue, outline, c, r, r);
                var chev = new Pen(White, size * 0.11) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                for (int i = 0; i < 2; i++)
                {
                    double oy = -r * 0.18 + i * r * 0.38;
                    dc.DrawGeometry(null, chev, Poly(false,
                        new Point(c.X - r * 0.38, c.Y + oy + r * 0.18), new Point(c.X, c.Y + oy - r * 0.18), new Point(c.X + r * 0.38, c.Y + oy + r * 0.18)));
                }
                break;
            }

            case MarkerKind.Target:
            {
                var under = new Pen(Dark, size * 0.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var top = new Pen(Red, size * 0.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                foreach (var pen in new[] { under, top })
                {
                    dc.DrawEllipse(null, pen, c, r * 0.7, r * 0.7);
                    dc.DrawLine(pen, new Point(c.X, c.Y - r), new Point(c.X, c.Y - r * 0.35));
                    dc.DrawLine(pen, new Point(c.X, c.Y + r), new Point(c.X, c.Y + r * 0.35));
                    dc.DrawLine(pen, new Point(c.X - r, c.Y), new Point(c.X - r * 0.35, c.Y));
                    dc.DrawLine(pen, new Point(c.X + r, c.Y), new Point(c.X + r * 0.35, c.Y));
                }
                dc.DrawEllipse(Red, null, c, r * 0.12, r * 0.12);
                break;
            }

            case MarkerKind.Objective:
            {
                var pts = new Point[10];
                for (int i = 0; i < 10; i++)
                {
                    double a = -Math.PI / 2 + i * Math.PI / 5;
                    double rr = i % 2 == 0 ? r : r * 0.45;
                    pts[i] = new Point(c.X + rr * Math.Cos(a), c.Y + rr * Math.Sin(a) + r * 0.08);
                }
                dc.DrawGeometry(Purple, outline, Poly(true, pts));
                break;
            }

            case MarkerKind.BlueDot:
                dc.DrawEllipse(TeamBlue, new Pen(White, size * 0.12), c, r * 0.72, r * 0.72);
                break;

            case MarkerKind.RedDot:
                dc.DrawEllipse(TeamRed, new Pen(White, size * 0.12), c, r * 0.72, r * 0.72);
                break;
        }
    }

    static void Eye(DrawingContext dc, Point c, double r)
    {
        dc.DrawEllipse(White, null, c, r * 0.62, r * 0.36);
        dc.DrawEllipse(Dark, null, c, r * 0.24, r * 0.24);
    }

    /// <summary>Half a swirl from the centre outwards, starting at angle <paramref name="start"/>.</summary>
    static Geometry Spiral(Point c, double r, double start)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            const int n = 18;
            ctx.BeginFigure(new Point(c.X + r * 0.12 * Math.Cos(start), c.Y + r * 0.12 * Math.Sin(start)), false, false);
            var pts = new List<Point>();
            for (int i = 1; i <= n; i++)
            {
                double k = (double)i / n, a = start + k * Math.PI * 1.15, rr = r * (0.12 + 0.88 * k);
                pts.Add(new Point(c.X + rr * Math.Cos(a), c.Y + rr * Math.Sin(a)));
            }
            ctx.PolyLineTo(pts, true, true);
        }
        g.Freeze();
        return g;
    }

    static Geometry Poly(bool closed, params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], closed, closed);
            ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    static void Glyph(DrawingContext dc, string s, Point center, double fontSize, Brush brush, double ppd)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, fontSize, brush, ppd);
        // Centre on the visible ink, not the line box.
        double y = center.Y - ft.Height - ft.OverhangAfter + ft.Extent / 2;
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, y));
    }
}

/// <summary>Small element that shows one marker (used in the toolbar).</summary>
public sealed class MarkerIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(MarkerKind), typeof(MarkerIcon),
        new FrameworkPropertyMetadata(MarkerKind.Ward, FrameworkPropertyMetadataOptions.AffectsRender));

    public MarkerKind Kind
    {
        get => (MarkerKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight) * 0.9;
        MarkerRenderer.Draw(dc, Kind, new Point(ActualWidth / 2, ActualHeight / 2), size, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
