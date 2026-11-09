using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RiftPen;

/// <summary>The full-screen drawing surface: handles mouse input and renders screen, map and minimap drawings.</summary>
public sealed class DrawCanvas : FrameworkElement
{
    static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
    static readonly Brush BadgeBack = Freeze(new SolidColorBrush(Color.FromArgb(200, 15, 15, 20)));
    static readonly Typeface TextFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Pen GridPen = FreezePen(Color.FromArgb(110, 80, 220, 255), 1);
    static readonly Pen GridEdgePen = FreezePen(Color.FromArgb(220, 80, 220, 255), 2.5);
    static readonly Pen SelectPen = MakeDashed(Color.FromRgb(0x3A, 0x6F, 0xF7), 2);
    static readonly Pen MinimapOutline = MakeDashed(Color.FromArgb(130, 255, 255, 255), 1);
    const double MapSize = 15000;

    readonly AppState S = AppState.I;
    public Projector Projector { get; } = new();
    public Canvas? EditLayer { get; set; }
    /// <summary>Hides the status badge (for screenshots).</summary>
    public bool SuppressBadge { get; set; }

    bool worldVisible, force = true;
    CameraState? lastCam;
    Rect lastViewport;
    double? lastTime;
    string? lastKey;

    // drawing in progress
    DrawItem? current;
    Point startScreen, lastScreen;
    bool viaMinimap;   // this gesture is drawn on the minimap
    bool minimapPass;  // currently rendering into the minimap
    bool erasing;
    readonly List<(DrawItem Item, int Index)> erased = new();

    // select tool
    bool dragging, moved;
    DrawItem? selBefore;

    // minimap area picking
    bool picking;
    Point pickStart, pickEnd;

    readonly List<(DrawItem Item, DateTime Released)> lasers = new();

    TextBox? editBox;
    Point editStored;
    Space editSpace;
    Color editColor;
    double editThickness;

    string? status;
    DateTime statusUntil;

    public DrawCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        S.Changed += () => { force = true; UpdateCursor(); InvalidateVisual(); };
        S.ItemsChanged += InvalidateVisual;
        S.Message += Flash;
    }

    double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>True when map drawings can be shown and placed right now.</summary>
    public bool CanUseMap => worldVisible && Projector.Valid;

    /// <summary>Called every frame by the overlay with the latest camera and League viewport.</summary>
    public void Frame(CameraState? cam, Rect viewport, bool visible)
    {
        bool camChanged = !CameraState.SameView(cam, lastCam) || viewport != lastViewport;
        if (camChanged || force)
        {
            if (cam != null) Projector.Update(cam, viewport, S.Settings.Calibration);
            else Projector.Invalidate();
        }
        bool visChanged = visible != worldVisible;
        lastCam = cam;
        lastViewport = viewport;
        worldVisible = visible;

        // Replay clock: timed notes fade in and out as it moves.
        var t = S.ReplayTime;
        var key = S.ReplayKey;
        bool timeChanged = t != lastTime || key != lastKey;
        lastTime = t;
        lastKey = key;
        bool timedActive = timeChanged && key != null && S.Items.Any(i => i.ReplayKey == key);

        bool lasersActive = lasers.Count > 0;
        if (lasersActive)
        {
            double life = S.Settings.LaserSeconds + 0.6;
            lasers.RemoveAll(l => (DateTime.Now - l.Released).TotalSeconds > life);
        }

        bool statusExpired = status != null && DateTime.Now > statusUntil;
        if (statusExpired) status = null;

        bool redraw = force || visChanged || statusExpired || timedActive || lasersActive ||
                      (camChanged && visible && HasWorldContent());
        force = false;
        if (redraw) InvalidateVisual();
    }

    bool HasWorldContent() =>
        S.Settings.ShowGrid || current?.Space == Space.World || S.Items.Any(i => i.Space == Space.World) ||
        lasers.Any(l => l.Item.Space == Space.World);

    public void UpdateCursor()
    {
        Cursor = !S.DrawMode ? null : S.PickingMinimap ? Cursors.Cross : S.Tool switch
        {
            ToolKind.Text => Cursors.IBeam,
            ToolKind.Eraser => Cursors.Hand,
            ToolKind.Select => Cursors.Arrow,
            _ => Cursors.Cross,
        };
    }

    public void Flash(string message)
    {
        status = message;
        statusUntil = DateTime.Now.AddSeconds(3);
        InvalidateVisual();
    }

    /// <summary>Shown and placeable right now (timeline, replay and camera all agree).</summary>
    bool IsShown(DrawItem it) => S.Visible(it, out _) && (it.Space == Space.Screen || CanUseMap);

    // ---------------------------------------------------------------- minimap mapping

    Rect? MinimapRect
    {
        get
        {
            var r = S.Settings.MinimapRect;
            var vp = lastViewport;
            if (r is not { Length: 4 } || vp.Width < 10) return null;
            return new Rect(vp.X + r[0] * vp.Width, vp.Y + r[1] * vp.Height, r[2] * vp.Width, r[3] * vp.Height);
        }
    }

    bool MinimapProject(double wx, double wz, out Point p)
    {
        p = default;
        if (MinimapRect is not Rect r) return false;
        double size = S.Settings.MinimapWorldSize;
        p = new Point(r.X + wx / size * r.Width, r.Bottom - wz / size * r.Height);
        return true;
    }

    bool MinimapUnproject(Point p, out Point world)
    {
        world = default;
        if (MinimapRect is not Rect r) return false;
        double size = S.Settings.MinimapWorldSize;
        world = new Point((p.X - r.X) / r.Width * size, (r.Bottom - p.Y) / r.Height * size);
        return true;
    }

    bool OnMinimap(Point p) =>
        S.Settings.MinimapInput && CanUseMap && S.SpaceMode != SpaceMode.Screen && MinimapRect is Rect r && r.Contains(p);

    // ---------------------------------------------------------------- input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!S.DrawMode) return;
        e.Handled = true;
        CommitText();
        Focus();

        var p = e.GetPosition(this);
        if (S.PickingMinimap)
        {
            picking = true;
            pickStart = pickEnd = p;
            CaptureMouse();
            return;
        }

        if (S.Hidden) S.Hidden = false;
        startScreen = lastScreen = p;
        viaMinimap = OnMinimap(p);

        if (S.Tool == ToolKind.Select) { BeginSelect(p); return; }

        if (S.Tool == ToolKind.Eraser)
        {
            erasing = true;
            erased.Clear();
            EraseAt(p);
            CaptureMouse();
            return;
        }

        if (S.Tool == ToolKind.Range)
        {
            var preset = S.CurrentRange;
            if (preset == null) { Flash("No range circles yet. Add some in Settings → Range circles."); return; }
            if (!CanUseMap || !ToWorld(p, out var centre))
            {
                Flash("Range circles go on the map: open a replay with the Replay API on.");
                return;
            }
            current = new DrawItem
            {
                Kind = ToolKind.Range, Space = Space.World, Color = ParseColor(preset.Color), Thickness = 3,
                Radius = preset.Radius, Text = $"{preset.Name} {preset.Radius:0}", Points = { centre },
            };
            CaptureMouse();
            InvalidateVisual();
            return;
        }

        var space = ResolveSpace();
        if (!ToStore(p, space, out var stored))
        {
            Flash("Can't place that there: it's above the map's horizon.");
            return;
        }

        if (S.Tool == ToolKind.Text) { BeginText(p, stored, space); return; }

        var item = new DrawItem { Kind = S.Tool, Space = space, Color = S.Color, Thickness = S.Thickness, Marker = S.Marker };
        if (S.Tool == ToolKind.Marker)
        {
            item.Points.Add(stored);
            S.Stamp(item);
            S.AddItem(item);
            return;
        }

        // Map rectangles get their 4 ground corners on the first mouse move.
        if (!(S.Tool == ToolKind.Rectangle && space == Space.World)) item.Points.Add(stored);
        current = item;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!S.DrawMode) return;
        var p = e.GetPosition(this);
        if (picking) { pickEnd = p; InvalidateVisual(); return; }
        if (erasing) { EraseAt(p); return; }
        if (dragging) { DragSelected(p); return; }
        if (current == null || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateCurrent(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => Finish();

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        Finish();
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (!S.DrawMode) return;
        e.Handled = true;
        if (current != null)
        {
            current = null; // right-click cancels the shape in progress
            if (IsMouseCaptured) ReleaseMouseCapture();
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!S.DrawMode || S.Tool != ToolKind.Select || S.Selected == null) return;
        e.Handled = true;
        S.ScaleSelected(e.Delta > 0 ? 1.1 : 1 / 1.1);
    }

    void UpdateCurrent(Point p)
    {
        var it = current!;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (it.Kind)
        {
            case ToolKind.Range: // drag to reposition before letting go
                if (ToWorld(p, out var moved)) it.Points[0] = moved;
                break;

            case ToolKind.Pen:
            case ToolKind.Highlighter:
            case ToolKind.Laser:
                if ((p - lastScreen).Length < 1.5) return;
                if (ToStore(p, it.Space, out var w)) { it.Points.Add(w); lastScreen = p; }
                break;

            case ToolKind.Line:
            case ToolKind.Arrow:
                if (shift) p = Snap45(startScreen, p);
                SetSecond(it, p);
                break;

            case ToolKind.Ellipse:
                if (shift && it.Space == Space.Screen) p = Square(startScreen, p);
                SetSecond(it, p);
                break;

            case ToolKind.Rectangle:
                if (shift) p = Square(startScreen, p);
                if (it.Space == Space.Screen) { SetSecond(it, p); break; }
                var corners = new[] { startScreen, new Point(p.X, startScreen.Y), p, new Point(startScreen.X, p.Y) };
                var ground = new List<Point>(4);
                foreach (var c in corners)
                {
                    if (!ToWorld(c, out var cw)) return;
                    ground.Add(cw);
                }
                it.Points.Clear();
                it.Points.AddRange(ground);
                break;
        }
        InvalidateVisual();
    }

    void SetSecond(DrawItem it, Point screen)
    {
        if (!ToStore(screen, it.Space, out var w)) return;
        if (it.Points.Count < 2) it.Points.Add(w);
        else it.Points[1] = w;
    }

    void Finish()
    {
        if (picking)
        {
            picking = false;
            var r = new Rect(pickStart, pickEnd);
            var vp = lastViewport;
            if (r.Width > 20 && r.Height > 20 && vp.Width > 10)
                S.FinishMinimapPick(new[] { (r.X - vp.X) / vp.Width, (r.Y - vp.Y) / vp.Height, r.Width / vp.Width, r.Height / vp.Height });
            else
                Flash("Drag a box around the whole minimap.");
        }
        if (erasing)
        {
            erasing = false;
            if (erased.Count > 0) S.PushRemoval(erased.ToList());
            erased.Clear();
        }
        if (dragging)
        {
            dragging = false;
            if (moved && S.Selected is { } sel && selBefore != null) S.PushEdit(sel, selBefore, null);
            selBefore = null;
        }
        if (current != null)
        {
            var it = current;
            current = null;
            if (it.Kind == ToolKind.Laser)
            {
                if (it.Points.Count > 0) lasers.Add((it, DateTime.Now));
            }
            else if (IsComplete(it))
            {
                S.Stamp(it);
                S.AddItem(it);
            }
            InvalidateVisual();
        }
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    bool IsComplete(DrawItem it)
    {
        double min = it.Space == Space.World ? 15 : 3;
        return it.Kind switch
        {
            ToolKind.Pen or ToolKind.Highlighter => it.Points.Count >= 1,
            ToolKind.Line or ToolKind.Arrow or ToolKind.Ellipse => it.Points.Count == 2 && (it.Points[1] - it.Points[0]).Length > min,
            ToolKind.Rectangle when it.Space == Space.World => it.Points.Count == 4,
            ToolKind.Rectangle => it.Points.Count == 2 && Math.Abs(it.Points[1].X - it.Points[0].X) > min && Math.Abs(it.Points[1].Y - it.Points[0].Y) > min,
            _ => true,
        };
    }

    Space ResolveSpace()
    {
        if (viaMinimap) return Space.World;
        switch (S.SpaceMode)
        {
            case SpaceMode.Screen:
                return Space.Screen;
            case SpaceMode.Map:
                if (CanUseMap) return Space.World;
                Flash("Map mode needs an open replay with the Replay API turned on. Drew on the screen instead.");
                return Space.Screen;
            default:
                return CanUseMap ? Space.World : Space.Screen;
        }
    }

    bool ToWorld(Point screen, out Point world) =>
        viaMinimap ? MinimapUnproject(screen, out world) : Projector.TryUnproject(screen, out world);

    bool ToStore(Point screen, Space space, out Point stored)
    {
        if (space == Space.Screen) { stored = screen; return true; }
        return ToWorld(screen, out stored);
    }

    bool MapPoint(Point world, out Point screen) =>
        minimapPass ? MinimapProject(world.X, world.Y, out screen) : Projector.TryProject(world.X, world.Y, out screen);

    bool TryScreen(DrawItem it, Point stored, out Point screen)
    {
        if (it.Space == Space.Screen) { screen = stored; return true; }
        return MapPoint(stored, out screen);
    }

    // ---------------------------------------------------------------- select / move

    void BeginSelect(Point p)
    {
        DrawItem? hit = null;
        for (int i = S.Items.Count - 1; i >= 0; i--)
        {
            var it = S.Items[i];
            if (IsShown(it) && Hit(it, p, 8)) { hit = it; break; }
        }
        S.Selected = hit;
        if (hit != null)
        {
            selBefore = hit.Clone();
            dragging = true;
            moved = false;
            lastScreen = p;
            CaptureMouse();
        }
        InvalidateVisual();
    }

    void DragSelected(Point p)
    {
        if (S.Selected is not { } it) return;
        Vector delta;
        if (it.Space == Space.Screen) delta = p - lastScreen;
        else if (Projector.TryUnproject(lastScreen, out var a) && Projector.TryUnproject(p, out var b)) delta = b - a;
        else return;
        for (int i = 0; i < it.Points.Count; i++) it.Points[i] += delta;
        lastScreen = p;
        moved = true;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- eraser

    void EraseAt(Point p)
    {
        double r = 6 + S.Thickness;
        for (int i = S.Items.Count - 1; i >= 0; i--)
        {
            var it = S.Items[i];
            if (!IsShown(it)) continue;
            if (Hit(it, p, r))
            {
                erased.Add((it, i));
                S.Items.RemoveAt(i);
            }
        }
        InvalidateVisual();
    }

    bool Hit(DrawItem it, Point p, double r)
    {
        if (it.Points.Count == 0) return false;
        if (it.Kind == ToolKind.Marker)
            return TryScreen(it, it.Points[0], out var m) && (m - p).Length <= MarkerSize(it) / 2 + r;
        if (it.Kind == ToolKind.Range && TryScreen(it, it.Points[0], out var rc) && (rc - p).Length <= r + 8) return true;
        if (it.Kind == ToolKind.Text)
        {
            if (!TryScreen(it, it.Points[0], out var a)) return false;
            var b = TextBounds(it, a);
            b.Inflate(r, r);
            return b.Contains(p);
        }
        double tol = r + (it.Kind == ToolKind.Highlighter ? it.Thickness * 1.75 : it.Thickness / 2);
        foreach (var run in ScreenPaths(it))
        {
            if (run.Count == 1 && (run[0] - p).Length <= tol) return true;
            for (int k = 1; k < run.Count; k++)
                if (SegmentDistance(p, run[k - 1], run[k]) <= tol) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- text

    void BeginText(Point screen, Point stored, Space space)
    {
        if (EditLayer == null) return;
        double fs = FontSizeFor(S.Thickness);
        var box = new TextBox
        {
            MinWidth = 160,
            FontSize = fs,
            FontFamily = TextFace.FontFamily,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(S.Color),
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CaretBrush = Brushes.White,
            Padding = new Thickness(2, 0, 2, 0),
        };
        Canvas.SetLeft(box, screen.X - 4);
        Canvas.SetTop(box, screen.Y - fs * 0.75);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitText(); Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelText(); Focus(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => CommitText();

        editBox = box;
        if (S.Hook != null) S.Hook.EditingText = true; // let letters reach the text box
        editStored = stored;
        editSpace = space;
        editColor = S.Color;
        editThickness = S.Thickness;
        EditLayer.Children.Add(box);
        Dispatcher.BeginInvoke(() => { box.Focus(); Keyboard.Focus(box); }, DispatcherPriority.Input);
    }

    public void CommitText()
    {
        var box = editBox;
        if (box == null) return;
        editBox = null;
        if (S.Hook != null) S.Hook.EditingText = false;
        EditLayer?.Children.Remove(box);
        var text = box.Text.Trim();
        if (text.Length == 0) return;
        var item = new DrawItem { Kind = ToolKind.Text, Space = editSpace, Color = editColor, Thickness = editThickness, Text = text, Points = { editStored } };
        S.Stamp(item);
        S.AddItem(item);
    }

    void CancelText()
    {
        var box = editBox;
        if (box == null) return;
        editBox = null;
        if (S.Hook != null) S.Hook.EditingText = false;
        EditLayer?.Children.Remove(box);
    }

    // ---------------------------------------------------------------- rendering

    protected override void OnRender(DrawingContext dc)
    {
        if (S.DrawMode) dc.DrawRectangle(HitBrush, null, new Rect(RenderSize)); // lets the window catch clicks

        if (!S.Hidden)
        {
            bool map = CanUseMap;
            if (map && S.Settings.ShowGrid) DrawGrid(dc);

            foreach (var it in S.Items)
            {
                if (it.Space == Space.World && !map) continue;
                if (!S.Visible(it, out double alpha)) continue;
                if (alpha < 1) dc.PushOpacity(alpha);
                RenderItem(dc, it);
                if (alpha < 1) dc.Pop();
            }
            if (current != null && (current.Space == Space.Screen || map))
            {
                if (current.Kind == ToolKind.Laser) RenderLaser(dc, current, 1);
                else RenderItem(dc, current);
            }
            foreach (var (laser, released) in lasers)
            {
                if (laser.Space == Space.World && !map) continue;
                double age = (DateTime.Now - released).TotalSeconds, hold = S.Settings.LaserSeconds;
                double alpha = age < hold ? 1 : Math.Clamp(1 - (age - hold) / 0.6, 0, 1);
                if (alpha > 0) RenderLaser(dc, laser, alpha);
            }

            if (S.Tool == ToolKind.Select && S.Selected is { } sel && IsShown(sel)) DrawSelection(dc, sel);
            if (worldVisible) DrawMinimap(dc);
        }

        if (S.PickingMinimap) DrawPicking(dc);
        if (!SuppressBadge) DrawBadge(dc);
    }

    void RenderItem(DrawingContext dc, DrawItem it)
    {
        if (it.Points.Count == 0) return;
        bool hl = it.Kind == ToolKind.Highlighter;
        var color = hl ? Color.FromArgb(105, it.Color.R, it.Color.G, it.Color.B) : it.Color;
        var brush = Freeze(new SolidColorBrush(color));

        switch (it.Kind)
        {
            case ToolKind.Text:
                if (!minimapPass && TryScreen(it, it.Points[0], out var ta)) DrawText(dc, it.Text ?? "", ta, it.Color, FontSizeFor(it.Thickness));
                return;
            case ToolKind.Marker:
                if (TryScreen(it, it.Points[0], out var ma)) MarkerRenderer.Draw(dc, it.Marker, ma, minimapPass ? 12 : MarkerSize(it), Ppd);
                return;
            case ToolKind.Range:
                RenderRange(dc, it);
                return;
        }

        double th = (hl ? it.Thickness * 3.5 : it.Thickness) * (minimapPass ? 0.35 : 1);
        if (minimapPass) th = Math.Max(1.2, th);
        var pen = new Pen(brush, th) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();

        foreach (var run in ScreenPaths(it))
        {
            if (it.Kind == ToolKind.Arrow) { DrawArrow(dc, run, brush, pen, th); continue; }
            if (run.Count == 1) { dc.DrawEllipse(brush, null, run[0], th / 2, th / 2); continue; }
            dc.DrawGeometry(null, pen, Polyline(run));
        }
    }

    void RenderLaser(DrawingContext dc, DrawItem it, double alpha)
    {
        var c = it.Color;
        double th = Math.Max(3, it.Thickness);
        var glow = new Pen(Freeze(new SolidColorBrush(Color.FromArgb((byte)(80 * alpha), c.R, c.G, c.B))), th * 3.2)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var core = new Pen(Freeze(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), c.R, c.G, c.B))), th)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var hot = new Pen(Freeze(new SolidColorBrush(Color.FromArgb((byte)(200 * alpha), 255, 255, 255))), Math.Max(1, th * 0.3))
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var run in ScreenPaths(it))
        {
            if (run.Count == 1) { dc.DrawEllipse(core.Brush, null, run[0], th, th); continue; }
            var g = Polyline(run);
            dc.DrawGeometry(null, glow, g);
            dc.DrawGeometry(null, core, g);
            dc.DrawGeometry(null, hot, g);
        }
    }

    /// <summary>A ground circle like League's own range indicators: soft fill, solid edge, label on top.</summary>
    void RenderRange(DrawingContext dc, DrawItem it)
    {
        var c = it.Points[0];
        var runs = ScreenPaths(it);
        var edge = Freeze(new SolidColorBrush(it.Color));
        double th = minimapPass ? 1.2 : it.Thickness;
        var pen = new Pen(edge, th) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();

        if (runs.Count == 1 && runs[0].Count > 100) // whole circle visible: fill it
        {
            var fill = Freeze(new SolidColorBrush(Color.FromArgb(38, it.Color.R, it.Color.G, it.Color.B)));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(runs[0][0], true, true);
                ctx.PolyLineTo(runs[0].Skip(1).ToList(), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(fill, null, g);
        }
        foreach (var run in runs)
            if (run.Count > 1) dc.DrawGeometry(null, pen, Polyline(run));

        if (minimapPass) return;
        if (MapPoint(c, out var mid)) dc.DrawEllipse(edge, null, mid, 3, 3);
        if (S.Settings.ShowRangeLabels && !string.IsNullOrEmpty(it.Text) && MapPoint(new Point(c.X, c.Y + it.Radius), out var top))
            Label(dc, it.Text!, top, 12);
    }

    /// <summary>Map drawings repeated, small, on the minimap.</summary>
    void DrawMinimap(DrawingContext dc)
    {
        if (MinimapRect is not Rect r) return;
        if (S.DrawMode && S.Settings.MinimapInput && CanUseMap && S.SpaceMode != SpaceMode.Screen)
            dc.DrawRectangle(null, MinimapOutline, r); // shows you can draw there
        if (!S.Settings.MirrorToMinimap) return;

        minimapPass = true;
        dc.PushClip(new RectangleGeometry(r));
        foreach (var it in S.Items)
        {
            if (it.Space != Space.World || !S.Visible(it, out double alpha)) continue;
            if (alpha < 1) dc.PushOpacity(alpha);
            RenderItem(dc, it);
            if (alpha < 1) dc.Pop();
        }
        if (current is { Space: Space.World, Kind: not ToolKind.Laser }) RenderItem(dc, current);
        dc.Pop();
        minimapPass = false;
    }

    void DrawSelection(DrawingContext dc, DrawItem it)
    {
        Rect? bounds = null;
        void Add(Point p) => bounds = bounds is Rect b ? Rect.Union(b, p) : new Rect(p, new Size(0, 0));
        if (it.Kind == ToolKind.Text && TryScreen(it, it.Points[0], out var ta)) bounds = TextBounds(it, ta);
        else if (it.Kind == ToolKind.Marker && TryScreen(it, it.Points[0], out var ma))
        {
            double h = MarkerSize(it) / 2;
            bounds = new Rect(ma.X - h, ma.Y - h, h * 2, h * 2);
        }
        else foreach (var run in ScreenPaths(it)) foreach (var p in run) Add(p);
        if (bounds is not Rect r) return;
        r.Inflate(8 + it.Thickness / 2, 8 + it.Thickness / 2);
        dc.DrawRoundedRectangle(null, SelectPen, r, 4, 4);
    }

    void DrawPicking(DrawingContext dc)
    {
        dc.DrawRectangle(Freeze(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0))), null, new Rect(RenderSize));
        if (MinimapRect is Rect old && !picking) dc.DrawRectangle(null, MinimapOutline, old);
        if (!picking) return;
        var r = new Rect(pickStart, pickEnd);
        dc.DrawRectangle(Freeze(new SolidColorBrush(Color.FromArgb(50, 58, 111, 247))), SelectPen, r);
    }

    /// <summary>The item's outline in overlay pixels, split wherever a map point goes off-camera.</summary>
    List<List<Point>> ScreenPaths(DrawItem it)
    {
        var pts = it.Points;
        IEnumerable<Point> src = pts;
        switch (it.Kind)
        {
            case ToolKind.Rectangle when it.Space == Space.Screen && pts.Count >= 2:
                src = new[] { pts[0], new Point(pts[1].X, pts[0].Y), pts[1], new Point(pts[0].X, pts[1].Y), pts[0] };
                break;
            case ToolKind.Rectangle when it.Space == Space.World && pts.Count == 4:
                src = pts.Append(pts[0]);
                break;
            case ToolKind.Ellipse when pts.Count >= 2 && it.Space == Space.Screen:
            {
                var c = new Point((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2);
                src = Ring(c, Math.Abs(pts[1].X - pts[0].X) / 2, Math.Abs(pts[1].Y - pts[0].Y) / 2, 72);
                break;
            }
            case ToolKind.Range:
                src = Ring(pts[0], it.Radius, it.Radius, 120);
                break;
            case ToolKind.Ellipse when pts.Count >= 2:
            {
                double r = (pts[1] - pts[0]).Length; // ground circle around the start point
                src = Ring(pts[0], r, r, 96);
                break;
            }
        }
        if (it.Space == Space.Screen) return new List<List<Point>> { src.ToList() };
        return ProjectRuns(src);
    }

    List<List<Point>> ProjectRuns(IEnumerable<Point> world)
    {
        var runs = new List<List<Point>>();
        List<Point>? cur = null;
        foreach (var w in world)
        {
            if (MapPoint(w, out var s)) (cur ??= new List<Point>()).Add(s);
            else if (cur != null) { runs.Add(cur); cur = null; }
        }
        if (cur != null) runs.Add(cur);
        return runs;
    }

    static IEnumerable<Point> Ring(Point c, double rx, double ry, int n)
    {
        for (int i = 0; i <= n; i++)
        {
            double t = i * 2 * Math.PI / n;
            yield return new Point(c.X + rx * Math.Cos(t), c.Y + ry * Math.Sin(t));
        }
    }

    static void DrawArrow(DrawingContext dc, List<Point> run, Brush brush, Pen pen, double th)
    {
        if (run.Count < 2) return;
        var a = run[0];
        var b = run[^1];
        var d = b - a;
        double len = d.Length;
        if (len < 1) return;
        d /= len;
        double headLen = Math.Min(len * 0.6, 10 + th * 3);
        double headW = headLen * 0.7;
        var n = new Vector(-d.Y, d.X);
        var basePt = b - d * headLen;
        dc.DrawLine(pen, a, b - d * headLen * 0.6);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(b, true, true);
            c.PolyLineTo(new[] { basePt + n * headW / 2, basePt - n * headW / 2 }, true, true);
        }
        g.Freeze();
        dc.DrawGeometry(brush, new Pen(brush, Math.Max(1, th * 0.4)) { LineJoin = PenLineJoin.Round }, g);
    }

    void DrawText(DrawingContext dc, string text, Point anchor, Color color, double size)
    {
        var fill = Freeze(new SolidColorBrush(color));
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, size, fill, Ppd);
        var geo = ft.BuildGeometry(new Point(anchor.X, anchor.Y - ft.Height / 2));
        bool dark = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B < 80;
        var outline = new Pen(dark ? new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
            Math.Max(2.5, size / 6)) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, outline, geo);
        dc.DrawGeometry(fill, null, geo);
    }

    Rect TextBounds(DrawItem it, Point anchor)
    {
        var ft = new FormattedText(it.Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace,
            FontSizeFor(it.Thickness), Brushes.White, Ppd);
        return new Rect(anchor.X, anchor.Y - ft.Height / 2, ft.Width, ft.Height);
    }

    void DrawGrid(DrawingContext dc)
    {
        for (int i = 0; i <= 15; i++)
        {
            double v = i * 1000;
            var pen = i % 5 == 0 ? GridEdgePen : GridPen;
            DrawWorldLine(dc, new Point(v, 0), new Point(v, MapSize), pen);
            DrawWorldLine(dc, new Point(0, v), new Point(MapSize, v), pen);
        }
        for (int x = 0; x <= 15000; x += 5000)
            for (int z = 0; z <= 15000; z += 5000)
                if (Projector.TryProject(x, z, out var p)) Label(dc, $"{x / 1000}k,{z / 1000}k", p, 12);

        GroundLabel(dc, new Point(400, 420), "Blue fountain", Color.FromRgb(60, 140, 255));
        GroundLabel(dc, new Point(14340, 14390), "Red fountain", Color.FromRgb(255, 70, 70));
    }

    void GroundLabel(DrawingContext dc, Point world, string text, Color c)
    {
        var pts = ProjectRuns(Ring(world, 350, 350, 48));
        var pen = FreezePen(c, 3);
        foreach (var run in pts) if (run.Count > 1) dc.DrawGeometry(null, pen, Polyline(run));
        if (Projector.TryProject(world.X, world.Y, out var p)) Label(dc, text, p, 13);
    }

    void DrawWorldLine(DrawingContext dc, Point a, Point b, Pen pen)
    {
        const int steps = 48;
        var pts = Enumerable.Range(0, steps + 1).Select(i => a + (b - a) * (i / (double)steps));
        foreach (var run in ProjectRuns(pts))
            if (run.Count > 1) dc.DrawGeometry(null, pen, Polyline(run));
    }

    void Label(DrawingContext dc, string text, Point p, double size)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TextFace, size, Brushes.White, Ppd);
        var r = new Rect(p.X - ft.Width / 2 - 4, p.Y - ft.Height / 2 - 1, ft.Width + 8, ft.Height + 2);
        dc.DrawRoundedRectangle(BadgeBack, null, r, 4, 4);
        dc.DrawText(ft, new Point(r.X + 4, r.Y + 1));
    }

    void DrawBadge(DrawingContext dc)
    {
        var lines = new List<string>();
        if (S.PickingMinimap)
            lines.Add("Drag a box around your minimap  ·  Esc cancels");
        else if (S.DrawMode)
        {
            string where = S.SpaceMode switch
            {
                SpaceMode.Screen => "on screen",
                _ when CanUseMap => "on the map",
                SpaceMode.Map => "map mode (no replay found, using screen)",
                _ => "on screen",
            };
            string timeline = "";
            if (S.Settings.TimelineEnabled && S.ReplayKey != null && S.ReplayTime is double t)
                timeline = S.Settings.NoteDurationSec > 0
                    ? $"  ·  notes at {AppState.FormatTime(t)} for {S.Settings.NoteDurationSec:0}s"
                    : $"  ·  notes from {AppState.FormatTime(t)} on";
            string tool = S.Tool switch
            {
                ToolKind.Select => "  ·  drag to move, scroll to resize",
                ToolKind.Laser => "  ·  laser fades",
                _ => "",
            };
            lines.Add($"DRAWING {where}{timeline}{tool}  ·  Esc to stop");
        }
        if (S.Hidden) lines.Add("Drawings hidden");
        if (status != null) lines.Add(status);
        if (lines.Count == 0) return;

        double y = 10;
        foreach (var line in lines)
        {
            var ft = new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, 13, Brushes.White, Ppd);
            var r = new Rect((RenderSize.Width - ft.Width) / 2 - 12, y, ft.Width + 24, ft.Height + 8);
            dc.DrawRoundedRectangle(BadgeBack, null, r, 8, 8);
            dc.DrawText(ft, new Point(r.X + 12, r.Y + 4));
            y += r.Height + 6;
        }
    }

    // ---------------------------------------------------------------- helpers

    static double FontSizeFor(double thickness) => 14 + thickness * 2;
    static double MarkerSize(DrawItem it) => 22 + it.Thickness * 1.5;

    static Color ParseColor(string s)
    {
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch { return Colors.White; }
    }

    static Geometry Polyline(List<Point> pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], false, false);
            c.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    static double SegmentDistance(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-9) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    static Point Snap45(Point a, Point b)
    {
        var d = b - a;
        double step = Math.PI / 4;
        double ang = Math.Round(Math.Atan2(d.Y, d.X) / step) * step;
        return a + new Vector(Math.Cos(ang), Math.Sin(ang)) * d.Length;
    }

    static Point Square(Point a, Point b)
    {
        var d = b - a;
        double s = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
        return new Point(a.X + (d.X < 0 ? -s : s), a.Y + (d.Y < 0 ? -s : s));
    }

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    static Pen FreezePen(Color c, double th)
    {
        var p = new Pen(new SolidColorBrush(c), th) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    static Pen MakeDashed(Color c, double th)
    {
        var p = new Pen(new SolidColorBrush(c), th) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        p.Freeze();
        return p;
    }
}
