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
        (MarkerKind.Danger, "Danger"),
        (MarkerKind.Missing, "Enemy missing"),
        (MarkerKind.OnMyWay, "On my way"),
        (MarkerKind.Target, "Target / focus"),
        (MarkerKind.Objective, "Objective"),
        (MarkerKind.BlueDot, "Blue team"),
        (MarkerKind.RedDot, "Red team"),
    };

    static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
    static readonly Brush Dark = B(18, 18, 22), White = B(255, 255, 255), Yellow = B(242, 201, 76), Pink = B(232, 72, 140),
        Red = B(235, 64, 52), Blue = B(52, 120, 246), Purple = B(155, 89, 230), TeamBlue = B(40, 110, 255), TeamRed = B(230, 50, 60);

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
