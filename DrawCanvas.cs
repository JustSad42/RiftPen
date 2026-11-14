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
    // Resizing a range circle by dragging its edge.
    bool resizing;
    Point resizeCursor;

    // minimap area picking
    bool picking;
    Point pickStart, pickEnd;

    readonly List<(DrawItem Item, DateTime Released)> lasers = new();

    // Fading ink: finished strokes that disappear on their own (never saved).
    readonly List<(DrawItem Item, DateTime Done)> inks = new();
    const double InkFadeOut = 0.8;
    static bool CanFade(ToolKind k) => k is ToolKind.Pen or ToolKind.Highlighter or ToolKind.Line or ToolKind.Arrow or ToolKind.Rectangle or ToolKind.Ellipse;
    double InkAlpha(DateTime done)
    {
        double age = (DateTime.Now - done).TotalSeconds, hold = Math.Max(0.5, S.Settings.FadeSeconds);
        return age < hold ? 1 : Math.Clamp(1 - (age - hold) / InkFadeOut, 0, 1);
    }
    void ExpireInks() { if (inks.Count > 0) inks.RemoveAll(i => InkAlpha(i.Done) <= 0); }

    void RenderInks(DrawingContext dc, bool map)
    {
        foreach (var (ink, done) in inks)
        {
            if (ink.Space == Space.World && !map) continue;
            if (Board != null && ink.Space != Space.World) continue;
            double a = InkAlpha(done);
            if (a <= 0) continue;
            dc.PushOpacity(a);
            RenderItem(dc, ink);
            dc.Pop();
        }
    }

    TextBox? editBox;
    Point editStored;
    Space editSpace;
    Color editColor;
    double editThickness;

    string? status;
    DateTime statusUntil;

    // Fixed-on-screen drawings don't move with the camera, so they're drawn once into this cache.
    readonly DrawingGroup staticLayer = new();
    bool staticDirty = true;
    readonly Dictionary<uint, SolidColorBrush> brushCache = new();
    readonly Dictionary<(uint, double), Pen> penCache = new();

    // Screen positions of the stroke being drawn (for simplifying it when finished).
    readonly List<Point> currentScreen = new();

    // Faint preview of a range circle / marker under the cursor.
    Point? hover;

    // The replay camera is locked onto a champion right now.
    bool followLocked;

    // Tool wheel
    static readonly ToolKind[] WheelTools =
    {
        ToolKind.Select, ToolKind.Pen, ToolKind.Highlighter, ToolKind.Laser, ToolKind.Ping, ToolKind.Eraser, ToolKind.Line,
        ToolKind.Arrow, ToolKind.Rectangle, ToolKind.Ellipse, ToolKind.Text, ToolKind.Marker, ToolKind.Range,
    };
    const double WheelR1 = 46, WheelR2 = 100, WheelR3 = 108, WheelR4 = 190;
    Point wheelCenter;
    ToolKind? wheelTool;
    int? wheelColor;

    /// <summary>Mouse position in this element's coordinates, even while the overlay is click-through.</summary>
    public Func<Point>? CursorProvider { get; set; }

    /// <summary>Set when this canvas draws on the Rift Board (a flat top-down map) instead of over the game.</summary>
    public IBoardView? Board { get; set; }

    /// <summary>Mouse input draws right now.</summary>
    bool InputOn => Board != null ? Board.Drawing : S.DrawMode;

    bool ShownAt(DrawItem it, out double alpha) => Board != null ? S.Visible(it, Board.Time, out alpha) : S.Visible(it, out alpha);

    void Stamp(DrawItem it)
    {
        if (Board != null) S.Stamp(it, Board.Time);
        else S.Stamp(it);
    }

    public DrawCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        S.Changed += () => { force = true; staticDirty = true; UpdateCursor(); InvalidateVisual(); };
        S.ItemsChanged += () => { staticDirty = true; InvalidateVisual(); };
        S.Message += Flash;
        S.WheelChanged += OnWheelChanged;
    }

    /// <summary>Something is animating or in progress, so the overlay should keep redrawing every frame.</summary>
    public bool Busy => lasers.Count > 0 || inks.Count > 0 || pings.Count > 0 || status != null || current != null || dragging || resizing || erasing || S.WheelOpen || S.Spotlight;

    // Ping ripples: transient, never saved.
    readonly List<(Point Pos, Space Space, Color Color, DateTime At)> pings = new();
    const double PingSeconds = 1.1;

    /// <summary>A ripple at the mouse position (on the map when possible).</summary>
    public void PingAtCursor()
    {
        var p = CursorProvider?.Invoke() ?? new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        if (S.SpaceMode != SpaceMode.Screen && CanUseMap && Projector.TryUnproject(p, out var w)) pings.Add((w, Space.World, S.Color, DateTime.Now));
        else pings.Add((p, Space.Screen, S.Color, DateTime.Now));
        InvalidateVisual();
    }

    double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>True when map drawings can be shown and placed right now.</summary>
    public bool CanUseMap => Board != null || (worldVisible && Projector.Valid);

    /// <summary>Called by the overlay with the latest camera and League viewport. Returns true if anything changed.</summary>
    public bool Frame(CameraState? cam, Rect viewport, bool visible)
    {
        if (S.WheelOpen && CursorProvider != null) UpdateWheelHover(CursorProvider());
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
        if (key != lastKey) staticDirty = true;
        lastTime = t;
        lastKey = key;
        bool timedActive = timeChanged && key != null && S.ContextHasTimedNotes;

        bool lasersActive = lasers.Count > 0 || inks.Count > 0 || pings.Count > 0 || S.Spotlight;
        ExpireInks();
        if (lasers.Count > 0)
        {
            double life = S.Settings.LaserSeconds + 0.6;
            lasers.RemoveAll(l => (DateTime.Now - l.Released).TotalSeconds > life);
        }
        if (pings.Count > 0) pings.RemoveAll(p => (DateTime.Now - p.At).TotalSeconds > PingSeconds);

        // Follow circles ride along with the champion the camera is locked onto.
        followLocked = cam?.Attached == true && Projector.Valid;
        if (followLocked && Projector.TryLookAt(out var lookAt))
            foreach (var it in S.ContextItems)
                if (it.Follow && it.Points.Count > 0 && it.Points[0] != lookAt) it.Points[0] = lookAt;
        // …and drawings attached to a champion move with that champion's tracked position.
        bool followersMoved = timeChanged && UpdateFollowers(t);

        bool statusExpired = status != null && DateTime.Now > statusUntil;
        if (statusExpired) status = null;

        bool redraw = force || visChanged || statusExpired || timedActive || lasersActive || (followersMoved && visible) ||
                      (camChanged && visible && HasWorldContent());
        force = false;
        if (redraw) InvalidateVisual();
        return redraw;
    }

    // ---------------------------------------------------------------- following a champion

    /// <summary>The moment drawings are shown at: the board's moment on the Rift Board, the replay's otherwise.</summary>
    double? ShownTime => Board != null ? Board.Time : S.ReplayTime;

    /// <summary>
    /// The champion drawn at (or right next to) a point, from the Rift Board's minimap tracking. Only champions
    /// whose position is actually known (not estimated or dead) count.
    /// </summary>
    bool ChampionAt(Point screen, out string key, out Point world)
    {
        key = "";
        world = default;
        var R = RiftSession.I;
        if (R.Game == null || ShownTime is not double t) return false;
        double best = Board != null ? 32 : 75;
        bool found = false;
        foreach (var p in R.Game.Participants)
        {
            var st = R.StateAt(p, t);
            if (st.Dead || st.Quality == PosQuality.Estimated || !MapPoint(st.Pos, out var s)) continue;
            double d = (s - screen).Length;
            if (d < best) { best = d; key = p.ChampionKey; world = st.Pos; found = true; }
        }
        return found;
    }

    /// <summary>Moves drawings attached to champions to where those champions are at <paramref name="t"/>. True if any did.</summary>
    bool UpdateFollowers(double? t)
    {
        var R = RiftSession.I;
        if (t is not double now || R.Game is not { } g) return false;
        bool any = false;
        void Move(DrawItem it)
        {
            if (it.FollowKey == null || it.Points.Count == 0) return;
            var p = g.Participants.FirstOrDefault(x => x.ChampionKey == it.FollowKey);
            if (p == null) return;
            var st = R.StateAt(p, now);
            if (st.Dead) return; // stays where they died until they're back
            any = true;
            it.Points[0] = st.Pos;
        }
        foreach (var it in S.ContextItems) Move(it);
        if (current != null) Move(current);
        return any;
    }

    string ChampionName(string key) => RiftSession.I.Game?.Participants.FirstOrDefault(p => p.ChampionKey == key)?.ChampionName ?? key;

    bool HasWorldContent() =>
        S.Settings.ShowGrid || current?.Space == Space.World || S.ContextItems.Any(i => i.Space == Space.World) ||
        lasers.Any(l => l.Item.Space == Space.World) || inks.Any(i => i.Item.Space == Space.World) || (hover != null && S.Tool == ToolKind.Range);

    public void UpdateCursor()
    {
        Cursor = !InputOn ? null : S.PickingMinimap ? Cursors.Cross : S.Tool switch
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
    bool IsShown(DrawItem it) => ShownAt(it, out _) && (it.Space == Space.Screen ? Board == null : CanUseMap);

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
        Board == null && S.Settings.MinimapInput && CanUseMap && S.SpaceMode != SpaceMode.Screen && MinimapRect is Rect r && r.Contains(p);

    // ---------------------------------------------------------------- input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!InputOn) return;
        e.Handled = true;
        Press(e.GetPosition(this), PressureOf(e));
    }

    // Self-test hooks: the same as pressing, moving and letting go of the left button at a point.
    internal void TestDown(Point p) { if (InputOn) Press(p, null); }
    internal void TestMove(Point p) { if (resizing) ResizeRange(p); else if (dragging) DragSelected(p); else if (current != null) UpdateCurrent(p, null); }
    internal void TestUp() => Finish();
    internal int FadingCount => inks.Count;

    void Press(Point p, float? downPressure)
    {
        CommitText();
        Focus();

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
        hover = null;
        currentScreen.Clear();
        currentScreen.Add(p);

        if (S.Tool == ToolKind.Select) { BeginSelect(p); return; }

        if (S.Tool == ToolKind.Eraser)
        {
            erasing = true;
            erased.Clear();
            EraseAt(p);
            CaptureMouse();
            return;
        }

        if (S.Tool == ToolKind.Ping)
        {
            var pingSpace = ResolveSpace();
            if (ToStore(p, pingSpace, out var at)) pings.Add((at, pingSpace, S.Color, DateTime.Now));
            InvalidateVisual();
            return;
        }

        // Dragging the edge of a circle that's already there resizes it instead of placing a new one.
        if (S.Tool == ToolKind.Range && RangeEdgeAt(p) != null) { BeginSelect(p); return; }

        if (S.Tool == ToolKind.Range)
        {
            var preset = S.CurrentRange;
            if (preset == null) { Flash("No range circles yet. Add some in Settings → Range circles."); return; }
            if (!CanUseMap) { Flash("Range circles go on the map: open a replay with the Replay API on."); return; }
            bool follow = S.Settings.RangeFollow;
            string? followKey = null;
            Point centre;
            if (follow && ChampionAt(p, out var champ, out var champPos))
            {
                // Clicked on a champion: the circle stays on them.
                followKey = champ;
                centre = champPos;
                follow = false;
            }
            else if (follow && Board == null && followLocked && Projector.TryLookAt(out centre)) { /* camera-locked champion */ }
            else if (follow)
            {
                Flash(RiftSession.I.Game == null
                    ? "To follow a champion, open the Rift Board's replay tracking (it needs the minimap visible), or lock the camera onto one."
                    : "Click on a champion to attach the circle to them" + (Board == null ? ", or lock the camera onto one." : "."));
                return;
            }
            else if (!ToWorld(p, out centre))
            {
                Flash("Can't place that there: it's above the map's horizon.");
                return;
            }
            current = new DrawItem
            {
                Kind = ToolKind.Range, Space = Space.World, Color = ParseColor(preset.Color), Thickness = 3, Follow = follow, FollowKey = followKey,
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
            Stamp(item);
            S.AddItem(item);
            return;
        }

        // Map rectangles get their 4 ground corners on the first mouse move.
        if (!(S.Tool == ToolKind.Rectangle && space == Space.World)) item.Points.Add(stored);
        // An arrow drawn from a champion (with Follow on) keeps starting from them as they move.
        if (item.Kind == ToolKind.Arrow && space == Space.World && S.Settings.RangeFollow && ChampionAt(p, out var from, out var fromPos))
        {
            item.FollowKey = from;
            item.Points[0] = fromPos;
        }
        if (IsFreehand(item.Kind) && S.Settings.PenPressure && downPressure is float pressure)
            item.Pressure = new List<float> { pressure };
        current = item;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!InputOn) return;
        var p = e.GetPosition(this);
        if (picking) { pickEnd = p; InvalidateVisual(); return; }
        if (erasing) { EraseAt(p); return; }
        if (resizing) { ResizeRange(p); return; }
        if (dragging) { DragSelected(p); return; }
        if (current == null || e.LeftButton != MouseButtonState.Pressed)
        {
            if (S.Tool is ToolKind.Range or ToolKind.Marker) { hover = p; InvalidateVisual(); }
            return;
        }
        UpdateCurrent(p, PressureOf(e));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (hover != null) { hover = null; InvalidateVisual(); }
        base.OnMouseLeave(e);
    }

    static bool IsFreehand(ToolKind k) => k is ToolKind.Pen or ToolKind.Highlighter or ToolKind.Laser;

    /// <summary>Pen-tablet pressure (0–1), or null for a mouse.</summary>
    float? PressureOf(MouseEventArgs e)
    {
        var device = e.StylusDevice;
        if (device == null) return null;
        var pts = device.GetStylusPoints(this);
        return pts.Count > 0 ? pts[pts.Count - 1].PressureFactor : null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => Finish();

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        Finish();
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (!InputOn) return;
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
        if (!InputOn || S.Tool != ToolKind.Select || S.Selected == null) return;
        e.Handled = true;
        S.ScaleSelected(e.Delta > 0 ? 1.1 : 1 / 1.1);
    }

    void UpdateCurrent(Point p, float? pressure)
    {
        var it = current!;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (it.Kind)
        {
            case ToolKind.Range: // drag to reposition before letting go
                if (!it.Follow && ToWorld(p, out var moved)) it.Points[0] = moved;
                break;

            case ToolKind.Pen:
            case ToolKind.Highlighter:
            case ToolKind.Laser:
                if ((p - lastScreen).Length < 1.5) return;
                if (ToStore(p, it.Space, out var w))
                {
                    it.Points.Add(w);
                    it.Pressure?.Add(pressure ?? it.Pressure[^1]);
                    currentScreen.Add(p);
                    lastScreen = p;
                }
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
        if (dragging || resizing)
        {
            dragging = resizing = false;
            if (moved && S.Selected is { } sel && selBefore != null) S.PushEdit(sel, selBefore, null);
            selBefore = null;
            InvalidateVisual();
        }
        if (current != null)
        {
            var it = current;
            current = null;
            if (it.Kind == ToolKind.Laser)
            {
                if (it.Points.Count > 0) lasers.Add((it, DateTime.Now));
            }
            else if (IsComplete(it) && S.Settings.FadingInk && CanFade(it.Kind))
            {
                if (it.Kind is ToolKind.Pen or ToolKind.Highlighter) Simplify(it);
                inks.Add((it, DateTime.Now));
            }
            else if (IsComplete(it))
            {
                if (it.Kind is ToolKind.Pen or ToolKind.Highlighter) Simplify(it);
                Stamp(it);
                S.AddItem(it);
            }
            InvalidateVisual();
        }
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    /// <summary>Drops points that don't change the stroke's shape by more than half a pixel (smaller files, faster drawing).</summary>
    void Simplify(DrawItem it)
    {
        if (currentScreen.Count != it.Points.Count || it.Points.Count < 3) return;
        var keep = Rdp(currentScreen, 0.5);
        var pts = keep.Select(i => it.Points[i]).ToList();
        var pressure = it.Pressure != null && it.Pressure.Count == it.Points.Count ? keep.Select(i => it.Pressure[i]).ToList() : null;
        it.Points.Clear();
        it.Points.AddRange(pts);
        it.Pressure = pressure;
    }

    /// <summary>Ramer–Douglas–Peucker line simplification; returns the indices to keep, in order.</summary>
    public static List<int> Rdp(IReadOnlyList<Point> pts, double epsilon)
    {
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double best = 0;
            int index = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = SegmentDistance(pts[i], pts[a], pts[b]);
                if (d > best) { best = d; index = i; }
            }
            if (index >= 0 && best > epsilon)
            {
                keep[index] = true;
                stack.Push((a, index));
                stack.Push((index, b));
            }
        }
        var result = new List<int>();
        for (int i = 0; i < keep.Length; i++) if (keep[i]) result.Add(i);
        return result;
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

    bool BoardProject(Point world, out Point view) { view = Board!.WorldToView(world); return true; }
    bool BoardUnproject(Point view, out Point world) { world = Board!.ViewToWorld(view); return true; }

    Space ResolveSpace()
    {
        if (viaMinimap || Board != null) return Space.World;
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
        Board != null ? BoardUnproject(screen, out world) : viaMinimap ? MinimapUnproject(screen, out world) : Projector.TryUnproject(screen, out world);

    bool ToStore(Point screen, Space space, out Point stored)
    {
        if (space == Space.Screen) { stored = screen; return true; }
        return ToWorld(screen, out stored);
    }

    bool MapPoint(Point world, out Point screen) =>
        Board != null && !minimapPass ? BoardProject(world, out screen) : minimapPass ? MinimapProject(world.X, world.Y, out screen) : Projector.TryProject(world.X, world.Y, out screen);

    bool TryScreen(DrawItem it, Point stored, out Point screen)
    {
        if (it.Space == Space.Screen) { screen = stored; return true; }
        return MapPoint(stored, out screen);
    }

    // ---------------------------------------------------------------- select / move

    /// <summary>A range circle whose edge (or edge handle) is under the cursor, selected one first.</summary>
    DrawItem? RangeEdgeAt(Point p)
    {
        bool OnEdge(DrawItem it)
        {
            if (it.Kind != ToolKind.Range || it.Points.Count == 0 || !IsShown(it)) return false;
            if (RangeHandle(it, out var h) && (h - p).Length <= 11) return true;
            if (TryScreen(it, it.Points[0], out var c) && (c - p).Length <= 14) return false; // the centre moves it
            foreach (var run in ScreenPaths(it))
                for (int k = 1; k < run.Count; k++)
                    if (SegmentDistance(p, run[k - 1], run[k]) <= 6 + it.Thickness / 2) return true;
            return false;
        }
        if (S.Selected is { } sel && OnEdge(sel)) return sel;
        for (int i = S.Items.Count - 1; i >= 0; i--)
            if (OnEdge(S.Items[i])) return S.Items[i];
        return null;
    }

    /// <summary>Where a range circle's resize handle sits on screen (its right-hand edge).</summary>
    bool RangeHandle(DrawItem it, out Point screen)
    {
        screen = default;
        return it.Kind == ToolKind.Range && it.Points.Count > 0 && MapPoint(new Point(it.Points[0].X + it.Radius, it.Points[0].Y), out screen);
    }

    void ResizeRange(Point p)
    {
        if (S.Selected is not { Kind: ToolKind.Range } it || !ToWorld(p, out var w)) return;
        double r = (w - it.Points[0]).Length;
        // Shift snaps to 25 units, like most ability ranges.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) r = Math.Round(r / 25) * 25;
        it.Radius = Math.Clamp(Math.Round(r), 25, 8000);
        resizeCursor = p;
        moved = true;
        InvalidateVisual();
    }

    /// <summary>A range circle's label, with the size it has now (it can be resized after placing).</summary>
    static string RangeLabel(DrawItem it)
    {
        var text = it.Text ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^(.*?)\s*\d+$");
        return (m.Success ? m.Groups[1].Value : text) + $" {it.Radius:0}";
    }

    void BeginSelect(Point p)
    {
        if (RangeEdgeAt(p) is { } edge)
        {
            S.Selected = edge;
            selBefore = edge.Clone();
            resizing = true;
            moved = false;
            resizeCursor = p;
            CaptureMouse();
            InvalidateVisual();
            return;
        }
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
        else if (ToWorld(lastScreen, out var a) && ToWorld(p, out var b)) delta = b - a;
        else return;
        for (int i = 0; i < it.Points.Count; i++) it.Points[i] += delta;
        lastScreen = p;
        moved = true;
        if (it.Space == Space.Screen) staticDirty = true;
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
                S.Touch();
                staticDirty = true;
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
        Stamp(item);
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

    /// <summary>On the Rift Board only map drawings show, at the board's moment.</summary>
    void RenderBoard(DrawingContext dc)
    {
        if (InputOn) dc.DrawRectangle(HitBrush, null, new Rect(RenderSize));
        if (S.Hidden) return;
        UpdateFollowers(Board!.Time);
        foreach (var it in S.ContextItems)
        {
            if (it.Space != Space.World || !ShownAt(it, out double alpha)) continue;
            if (alpha < 1) dc.PushOpacity(alpha);
            RenderItem(dc, it);
            if (alpha < 1) dc.Pop();
        }
        if (current != null)
        {
            if (current.Kind == ToolKind.Laser) RenderLaser(dc, current, 1);
            else RenderItem(dc, current);
        }
        foreach (var (laser, released) in lasers)
        {
            double age = (DateTime.Now - released).TotalSeconds, hold = S.Settings.LaserSeconds;
            double alpha = age < hold ? 1 : Math.Clamp(1 - (age - hold) / 0.6, 0, 1);
            if (alpha > 0) RenderLaser(dc, laser, alpha);
        }
        RenderInks(dc, true);
        if ((S.Tool == ToolKind.Select || resizing) && S.Selected is { } sel && IsShown(sel)) DrawSelection(dc, sel);
        DrawGhost(dc);
        DrawPings(dc, true);
        if (status != null && DateTime.Now < statusUntil) Label(dc, status, new Point(RenderSize.Width / 2, 28), 13);
    }

    /// <summary>Advances lasers, pings and messages on the board; true while something is still animating.</summary>
    public bool TickBoard()
    {
        if (lasers.Count > 0) lasers.RemoveAll(l => (DateTime.Now - l.Released).TotalSeconds > S.Settings.LaserSeconds + 0.6);
        if (pings.Count > 0) pings.RemoveAll(p => (DateTime.Now - p.At).TotalSeconds > PingSeconds);
        ExpireInks();
        if (status != null && DateTime.Now > statusUntil) { status = null; InvalidateVisual(); }
        bool busy = lasers.Count > 0 || inks.Count > 0 || pings.Count > 0;
        if (busy) InvalidateVisual();
        return busy;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Board != null) { RenderBoard(dc); return; }
        if (S.DrawMode) dc.DrawRectangle(HitBrush, null, new Rect(RenderSize)); // lets the window catch clicks

        if (!S.Hidden)
        {
            if (staticDirty)
            {
                using (var ctx = staticLayer.Open())
                    foreach (var it in S.ContextItems)
                        if (it.Space == Space.Screen && it.ReplayKey == null) RenderItem(ctx, it);
                staticDirty = false;
            }
            dc.DrawDrawing(staticLayer);

            bool map = CanUseMap;
            if (map && S.Settings.ShowGrid) DrawGrid(dc);

            foreach (var it in S.ContextItems)
            {
                if (it.Space == Space.Screen && it.ReplayKey == null) continue; // in the cached layer
                if (it.Space == Space.World && !map) continue;
                if (!ShownAt(it, out double alpha)) continue;
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
            RenderInks(dc, map);

            if ((S.Tool == ToolKind.Select || resizing) && S.Selected is { } sel && IsShown(sel)) DrawSelection(dc, sel);
            if (worldVisible) DrawMinimap(dc);
            DrawGhost(dc);
            DrawPings(dc, map);
        }

        if (S.Spotlight) DrawSpotlight(dc);
        if (S.PickingMinimap) DrawPicking(dc);
        if (S.WheelOpen) DrawWheel(dc);
        if (S.CheatSheet) DrawCheatSheet(dc);
        if (!SuppressBadge) DrawBadge(dc);
    }

    /// <summary>Expanding rings, like a League ping.</summary>
    void DrawPings(DrawingContext dc, bool map)
    {
        foreach (var (pos, space, color, at) in pings)
        {
            Point c;
            if (space == Space.Screen) c = pos;
            else if (!map || !MapPoint(pos, out c)) continue;
            double age = (DateTime.Now - at).TotalSeconds;
            for (int ring = 0; ring < 2; ring++)
            {
                double t = (age - ring * 0.25) / (PingSeconds - 0.25);
                if (t is < 0 or > 1) continue;
                double eased = 1 - Math.Pow(1 - t, 3);
                byte a = (byte)(255 * (1 - t));
                var pen = new Pen(BrushOf(Color.FromArgb(a, color.R, color.G, color.B)), 4 - 2 * t);
                dc.DrawEllipse(null, pen, c, 8 + 70 * eased, 8 + 70 * eased);
            }
            double dotA = Math.Clamp(1 - age / PingSeconds, 0, 1);
            dc.DrawEllipse(BrushOf(Color.FromArgb((byte)(255 * dotA), color.R, color.G, color.B)), new Pen(BrushOf(Color.FromArgb((byte)(200 * dotA), 0, 0, 0)), 1.5), c, 6, 6);
        }
    }

    /// <summary>Darkens everything except a soft circle around the cursor.</summary>
    void DrawSpotlight(DrawingContext dc)
    {
        var c = CursorProvider?.Invoke() ?? new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        double r = Math.Clamp(S.Settings.SpotlightRadius, 60, 800);
        byte dim = (byte)(255 * Math.Clamp(S.Settings.SpotlightDim, 0.1, 0.95));
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = c,
            GradientOrigin = c,
            RadiusX = r * 1.25,
            RadiusY = r * 1.25,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.78),
                new GradientStop(Color.FromArgb(dim, 0, 0, 0), 1),
            },
        };
        brush.Freeze();
        dc.DrawRectangle(brush, null, new Rect(RenderSize));
    }

    /// <summary>Faint copy of the range circle or marker under the cursor, before you click.</summary>
    void DrawGhost(DrawingContext dc)
    {
        if (!InputOn || hover is not Point h || current != null || S.WheelOpen) return;
        if (S.Tool == ToolKind.Range && S.CurrentRange is { } preset && CanUseMap)
        {
            bool old = viaMinimap;
            viaMinimap = OnMinimap(h);
            bool follow = S.Settings.RangeFollow;
            Point w = default;
            bool ok = follow
                ? ChampionAt(h, out _, out w) || (Board == null && followLocked && Projector.TryLookAt(out w))
                : ToWorld(h, out w);
            if (ok)
            {
                var ghost = new DrawItem { Kind = ToolKind.Range, Space = Space.World, Color = ParseColor(preset.Color), Thickness = 2,
                    Radius = preset.Radius, Text = $"{preset.Name} {preset.Radius:0}", Points = { w }, Follow = follow };
                dc.PushOpacity(0.45);
                RenderRange(dc, ghost);
                dc.Pop();
            }
            viaMinimap = old;
        }
        else if (S.Tool == ToolKind.Marker)
        {
            dc.PushOpacity(0.5);
            if (CanUseMap && S.SpaceMode != SpaceMode.Screen && ToWorld(h, out var wardAt)) DrawWardVision(dc, S.Marker, wardAt);
            MarkerRenderer.Draw(dc, S.Marker, h, 22 + S.Thickness * 1.5, Ppd);
            dc.Pop();
        }
    }

    // ---------------------------------------------------------------- tool wheel

    void OnWheelChanged(bool open)
    {
        if (open)
        {
            var c = CursorProvider?.Invoke() ?? new Point(RenderSize.Width / 2, RenderSize.Height / 2);
            double m = WheelR4 + 12;
            c.X = Math.Clamp(c.X, m, Math.Max(m, RenderSize.Width - m));
            c.Y = Math.Clamp(c.Y, m, Math.Max(m, RenderSize.Height - m));
            wheelCenter = c;
            wheelTool = null;
            wheelColor = null;
        }
        else
        {
            if (wheelTool is ToolKind t) S.SelectTool(t);
            if (wheelColor is int ci) S.Color = AppState.Palette[ci]; // after the tool, which loads its own colour
        }
        InvalidateVisual();
    }

    void UpdateWheelHover(Point p)
    {
        var d = p - wheelCenter;
        double r = d.Length;
        double a = (Math.Atan2(d.Y, d.X) + Math.PI / 2 + 2 * Math.PI) % (2 * Math.PI); // 0 = straight up, clockwise
        ToolKind? tool = wheelTool;
        int? color = wheelColor;
        if (r < WheelR1) tool = null;
        else if (r <= WheelR2)
        {
            double step = 2 * Math.PI / AppState.Palette.Length;
            color = (int)Math.Floor((a + step / 2) / step) % AppState.Palette.Length;
        }
        else if (r >= WheelR3 - 4)
        {
            double step = 2 * Math.PI / WheelTools.Length;
            tool = WheelTools[(int)Math.Floor((a + step / 2) / step) % WheelTools.Length];
        }
        if (color != wheelColor && color is int c) S.Color = AppState.Palette[c]; // colours apply as you pass over them
        if (tool != wheelTool || color != wheelColor)
        {
            wheelTool = tool;
            wheelColor = color;
            InvalidateVisual();
        }
    }

    void DrawWheel(DrawingContext dc)
    {
        if (wheelCenter == default) wheelCenter = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        var c = wheelCenter;
        dc.DrawEllipse(BrushOf(Color.FromArgb(225, 22, 23, 28)), new Pen(BrushOf(Color.FromArgb(70, 255, 255, 255)), 1), c, WheelR4 + 6, WheelR4 + 6);

        // Inner ring: colours.
        int n = AppState.Palette.Length;
        double step = 2 * Math.PI / n;
        for (int i = 0; i < n; i++)
        {
            bool hot = wheelColor == i, currentColor = AppState.Palette[i] == S.Color;
            double a0 = i * step - step / 2, a1 = a0 + step;
            var sector = Sector(c, WheelR1, hot ? WheelR2 + 6 : WheelR2, a0 + 0.02, a1 - 0.02);
            dc.DrawGeometry(BrushOf(AppState.Palette[i]), currentColor || hot ? new Pen(Brushes.White, hot ? 3 : 2) : null, sector);
        }

        // Outer ring: tools.
        n = WheelTools.Length;
        step = 2 * Math.PI / n;
        var accent = BrushOf(Color.FromRgb(0x3A, 0x6F, 0xF7));
        var activeBg = BrushOf(Color.FromArgb(55, 255, 255, 255));
        for (int i = 0; i < n; i++)
        {
            var tool = WheelTools[i];
            double a0 = i * step - step / 2, a1 = a0 + step;
            bool hot = wheelTool == tool, active = S.Tool == tool;
            if (hot || active) dc.DrawGeometry(hot ? accent : activeBg, null, Sector(c, WheelR3, WheelR4, a0 + 0.015, a1 - 0.015));
            double mid = (a0 + a1) / 2, rr = (WheelR3 + WheelR4) / 2;
            var ic = new Point(c.X + rr * Math.Sin(mid), c.Y - rr * Math.Cos(mid));
            DrawToolIcon(dc, tool, ic, 26);
        }

        // Centre label.
        string label = wheelTool is ToolKind t ? ToolLabel(t) : "Pick a tool";
        var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, 13, Brushes.White, Ppd)
            { MaxTextWidth = WheelR1 * 2 - 6, TextAlignment = TextAlignment.Center };
        dc.DrawEllipse(BrushOf(S.Color), null, new Point(c.X, c.Y - 14), 6, 6);
        dc.DrawText(ft, new Point(c.X - (WheelR1 * 2 - 6) / 2, c.Y - 2));
    }

    static string ToolLabel(ToolKind t) => t switch
    {
        ToolKind.Ellipse => "Circle",
        ToolKind.Range => "Range circle",
        ToolKind.Laser => "Laser",
        _ => t.ToString(),
    };

    static Geometry Sector(Point c, double r0, double r1, double a0, double a1)
    {
        Point P(double r, double a) => new(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        bool large = a1 - a0 > Math.PI;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(r0, a0), true, true);
            ctx.LineTo(P(r1, a0), true, false);
            ctx.ArcTo(P(r1, a1), new Size(r1, r1), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(P(r0, a1), true, false);
            ctx.ArcTo(P(r0, a0), new Size(r0, r0), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    void DrawToolIcon(DrawingContext dc, ToolKind tool, Point c, double size)
    {
        var fg = Brushes.White;
        var stroke = new Pen(fg, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        double h = size / 2;
        string? glyph = tool switch
        {
            ToolKind.Pen => "",
            ToolKind.Highlighter => "",
            ToolKind.Eraser => "",
            ToolKind.Arrow => "",
            ToolKind.Text => "",
            _ => null,
        };
        if (glyph != null)
        {
            var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconFace, size * 0.8, fg, Ppd);
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
            return;
        }
        switch (tool)
        {
            case ToolKind.Select:
            {
                var pts = new[] { (0.0, 0.0), (0, 15), (4, 11.5), (6.6, 17), (9, 16), (6.4, 10.4), (11.5, 10.4) }
                    .Select(q => new Point(c.X - 5 + q.Item1, c.Y - 9 + q.Item2)).ToList();
                var g = new StreamGeometry();
                using (var ctx = g.Open()) { ctx.BeginFigure(pts[0], true, true); ctx.PolyLineTo(pts.Skip(1).ToList(), true, true); }
                g.Freeze();
                dc.DrawGeometry(fg, null, g);
                break;
            }
            case ToolKind.Laser:
                dc.DrawEllipse(BrushOf(Color.FromArgb(90, S.Color.R, S.Color.G, S.Color.B)), null, c, h * 0.75, h * 0.75);
                dc.DrawEllipse(BrushOf(S.Color), null, c, h * 0.35, h * 0.35);
                dc.DrawEllipse(Brushes.White, null, c, h * 0.13, h * 0.13);
                break;
            case ToolKind.Line:
                dc.DrawLine(stroke, new Point(c.X - h * 0.6, c.Y + h * 0.6), new Point(c.X + h * 0.6, c.Y - h * 0.6));
                break;
            case ToolKind.Rectangle:
                dc.DrawRoundedRectangle(null, stroke, new Rect(c.X - h * 0.65, c.Y - h * 0.48, h * 1.3, h * 0.96), 1.5, 1.5);
                break;
            case ToolKind.Ellipse:
                dc.DrawEllipse(null, stroke, c, h * 0.62, h * 0.62);
                break;
            case ToolKind.Marker:
                MarkerRenderer.Draw(dc, S.Marker, c, size * 0.85, Ppd);
                break;
            case ToolKind.Ping:
                dc.DrawEllipse(null, new Pen(BrushOf(S.Color), 2), c, h * 0.75, h * 0.75);
                dc.DrawEllipse(null, new Pen(BrushOf(Color.FromArgb(140, S.Color.R, S.Color.G, S.Color.B)), 1.5), c, h * 0.45, h * 0.45);
                dc.DrawEllipse(BrushOf(S.Color), null, c, h * 0.16, h * 0.16);
                break;
            case ToolKind.Range:
                dc.DrawEllipse(null, stroke, c, h * 0.72, h * 0.72);
                dc.DrawEllipse(null, stroke, c, h * 0.32, h * 0.32);
                break;
        }
    }

    // ---------------------------------------------------------------- shortcut cheat sheet

    void DrawCheatSheet(DrawingContext dc)
    {
        var groups = HotkeyCatalog.All(S.Settings)
            .Select(a => (a.Group, a.Name, Key: S.BindingFor(a.Id)?.Display))
            .Where(x => x.Key != null)
            .GroupBy(x => x.Group)
            .ToList();
        if (groups.Count == 0) return;

        // Lay out groups into columns of roughly equal height.
        const double lineH = 21, colW = 330, pad = 22;
        var columns = new List<List<(string Text, bool Header, string? Key)>> { new() };
        int perColumn = Math.Max(12, (int)Math.Ceiling(groups.Sum(g => g.Count() + 2) / 3.0));
        foreach (var g in groups)
        {
            var col = columns[^1];
            if (col.Count > 0 && col.Count + g.Count() + 2 > perColumn && columns.Count < 4) columns.Add(col = new());
            if (col.Count > 0) col.Add(("", false, null));
            col.Add((g.Key, true, null));
            foreach (var x in g) col.Add((x.Name, false, x.Key));
        }

        double w = columns.Count * colW + pad * 2;
        double h = columns.Max(c => c.Count) * lineH + pad * 2 + 30;
        var area = new Rect((RenderSize.Width - w) / 2, Math.Max(20, (RenderSize.Height - h) / 2), w, h);
        dc.DrawRoundedRectangle(BrushOf(Color.FromArgb(235, 18, 19, 24)), new Pen(BrushOf(Color.FromArgb(70, 255, 255, 255)), 1), area, 12, 12);
        var title = new FormattedText("RiftPen shortcuts", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, 17, Brushes.White, Ppd);
        dc.DrawText(title, new Point(area.X + pad, area.Y + pad - 4));

        var dim = BrushOf(Color.FromArgb(170, 255, 255, 255));
        var keyBrush = BrushOf(Color.FromRgb(0x8A, 0xB4, 0xFF));
        for (int ci = 0; ci < columns.Count; ci++)
        {
            double x = area.X + pad + ci * colW, y = area.Y + pad + 30;
            foreach (var (text, header, key) in columns[ci])
            {
                if (header)
                {
                    dc.DrawText(new FormattedText(text.ToUpperInvariant(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, 11, dim, Ppd), new Point(x, y + 4));
                }
                else if (key != null)
                {
                    var k = new FormattedText(key, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, 13, keyBrush, Ppd);
                    dc.DrawText(k, new Point(x, y));
                    var t = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, 13, Brushes.White, Ppd)
                        { MaxTextWidth = colW - 130, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                    dc.DrawText(t, new Point(x + 118, y));
                }
                y += lineH;
            }
        }
    }

    void RenderItem(DrawingContext dc, DrawItem it)
    {
        if (it.Points.Count == 0) return;
        bool hl = it.Kind == ToolKind.Highlighter;
        var color = hl ? Color.FromArgb(105, it.Color.R, it.Color.G, it.Color.B) : it.Color;
        var brush = BrushOf(color);

        switch (it.Kind)
        {
            case ToolKind.Text:
                if (!minimapPass && TryScreen(it, it.Points[0], out var ta)) DrawText(dc, it.Text ?? "", ta, it.Color, FontSizeFor(it.Thickness));
                return;
            case ToolKind.Marker:
                if (it.Space == Space.World) DrawWardVision(dc, it.Marker, it.Points[0]);
                if (TryScreen(it, it.Points[0], out var ma)) MarkerRenderer.Draw(dc, it.Marker, ma, minimapPass ? 12 : MarkerSize(it), Ppd);
                return;
            case ToolKind.Range:
                RenderRange(dc, it);
                return;
        }

        double th = (hl ? it.Thickness * 3.5 : it.Thickness) * (minimapPass ? 0.35 : 1);
        if (minimapPass) th = Math.Max(1.2, th);
        var pen = PenOf(color, th);
        var runs = ScreenPaths(it);

        // Tablet strokes: width follows pen pressure.
        if (!minimapPass && it.Pressure is { } pr && runs.Count == 1 && runs[0].Count == pr.Count && pr.Count > 1)
        {
            var run = runs[0];
            for (int i = 1; i < run.Count; i++)
                dc.DrawLine(PenOf(color, Math.Max(0.5, Math.Round(th * (0.25 + 0.95 * (pr[i - 1] + pr[i]) / 2) * 2) / 2)), run[i - 1], run[i]);
            return;
        }

        bool smooth = S.Settings.SmoothStrokes && IsFreehand(it.Kind);
        foreach (var run in runs)
        {
            if (it.Kind == ToolKind.Arrow) { DrawArrow(dc, run, brush, pen, th); continue; }
            if (run.Count == 1) { dc.DrawEllipse(brush, null, run[0], th / 2, th / 2); continue; }
            dc.DrawGeometry(null, pen, smooth ? SmoothLine(run) : Polyline(run));
        }
    }

    /// <summary>Curves through the midpoints between points, so freehand strokes look smooth instead of jagged.</summary>
    static Geometry SmoothLine(List<Point> pts)
    {
        if (pts.Count < 3) return Polyline(pts);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Count - 1; i++)
                c.QuadraticBezierTo(pts[i], new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2), true, true);
            c.LineTo(pts[^1], true, true);
        }
        g.Freeze();
        return g;
    }

    static uint ColorKey(Color c) => (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);

    SolidColorBrush BrushOf(Color c)
    {
        if (!brushCache.TryGetValue(ColorKey(c), out var b))
        {
            if (brushCache.Count > 256) brushCache.Clear();
            b = new SolidColorBrush(c);
            b.Freeze();
            brushCache[ColorKey(c)] = b;
        }
        return b;
    }

    Pen PenOf(Color c, double th)
    {
        var k = (ColorKey(c), th);
        if (!penCache.TryGetValue(k, out var p))
        {
            if (penCache.Count > 512) penCache.Clear();
            p = new Pen(BrushOf(c), th) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            p.Freeze();
            penCache[k] = p;
        }
        return p;
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
            var g = S.Settings.SmoothStrokes ? SmoothLine(run) : Polyline(run);
            dc.DrawGeometry(null, glow, g);
            dc.DrawGeometry(null, core, g);
            dc.DrawGeometry(null, hot, g);
        }
    }

    /// <summary>Vision radius of a ward stamp in game units (stealth and control wards see 900, farsight 500), or 0.</summary>
    static double WardVisionRadius(MarkerKind k) => k switch
    {
        MarkerKind.Ward or MarkerKind.ControlWard => 900,
        MarkerKind.FarsightWard => 500,
        _ => 0,
    };

    /// <summary>The area a ward marker would reveal, drawn on the ground around it.</summary>
    void DrawWardVision(DrawingContext dc, MarkerKind kind, Point world)
    {
        double radius = WardVisionRadius(kind);
        if (radius <= 0 || !S.Settings.WardVision) return;
        var c = kind switch
        {
            MarkerKind.ControlWard => Color.FromRgb(255, 79, 163),
            MarkerKind.FarsightWard => Color.FromRgb(64, 156, 255),
            _ => Color.FromRgb(255, 214, 10),
        };
        var runs = ProjectRuns(Ring(world, radius, radius, 96));
        if (runs.Count == 1 && runs[0].Count > 90 && !minimapPass)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(runs[0][0], true, true);
                ctx.PolyLineTo(runs[0].Skip(1).ToList(), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(BrushOf(Color.FromArgb(34, c.R, c.G, c.B)), null, g);
        }
        var pen = PenOf(Color.FromArgb(200, c.R, c.G, c.B), minimapPass ? 1 : 2);
        foreach (var run in runs)
            if (run.Count > 1) dc.DrawGeometry(null, pen, Polyline(run));
    }

    /// <summary>A ground circle like League's own range indicators: soft fill, solid edge, label on top.</summary>
    void RenderRange(DrawingContext dc, DrawItem it)
    {
        var c = it.Points[0];
        // A follow circle waits (faded) while the camera isn't locked onto anyone.
        bool waiting = it.Follow && !followLocked && !minimapPass;
        if (waiting) dc.PushOpacity(0.4);
        try { RenderRangeInner(dc, it, c, waiting); }
        finally { if (waiting) dc.Pop(); }
    }

    void RenderRangeInner(DrawingContext dc, DrawItem it, Point c, bool waiting)
    {
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
        {
            var name = RangeLabel(it);
            Label(dc, it.FollowKey != null ? $"{name}  ·  on {ChampionName(it.FollowKey)}" : it.Follow ? $"{name}  ·  {(waiting ? "lock the camera to follow" : "following")}" : name, top, 12);
        }
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
        foreach (var it in S.ContextItems)
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
        if (it.Kind == ToolKind.Range)
        {
            // A handle on the edge: drag it (or anywhere on the edge) to change the size.
            if (RangeHandle(it, out var h))
            {
                dc.DrawEllipse(Brushes.White, new Pen(Freeze(new SolidColorBrush(it.Color)), 2.5), h, 6.5, 6.5);
                if (resizing)
                {
                    var at = resizeCursor == default ? h : resizeCursor;
                    Label(dc, $"{it.Radius:0} units", new Point(at.X, at.Y + 16), 13);
                }
            }
            return;
        }
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
                ToolKind.Select => "  ·  drag to move, drag a circle's edge to resize",
                ToolKind.Laser => "  ·  laser fades",
                var k when CanFade(k) && S.Settings.FadingInk => $"  ·  fading ink ({S.Settings.FadeSeconds:0.#}s)",
                ToolKind.Ping => "  ·  click to ping",
                ToolKind.Range when S.Settings.RangeFollow => "  ·  circles follow the camera-locked champion",
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
