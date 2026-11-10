using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RiftPen;

/// <summary>
/// Developer check: `RiftPen.exe --selftest <folder>` renders sample drawings with a simulated replay camera
/// to PNGs and verifies the screen↔map math. Doesn't touch the screen or the mouse.
/// </summary>
internal static class SelfTest
{
    public static void Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var log = new StringBuilder();
        var S = AppState.I;
        S.Settings.Calibration = new CalibrationSettings();
        S.Settings.ShowGrid = true;
        var viewport = new Rect(0, 0, 1600, 900);

        // Simulated top-down replay camera above mid lane, looking north and down 56°.
        var cam = new CameraState(new Vec3(7400, 2000, 6000), 0, 56, 0, 40, "top");

        var proj = new Projector();
        proj.Update(cam, viewport, S.Settings.Calibration);
        double worst = 0;
        foreach (var sp in new[] { new Point(10, 10), new Point(800, 450), new Point(1590, 890), new Point(300, 700) })
        {
            if (!proj.TryUnproject(sp, out var w) || !proj.TryProject(w.X, w.Y, out var back)) { log.AppendLine($"FAIL round trip at {sp}"); continue; }
            worst = Math.Max(worst, (back - sp).Length);
        }
        log.AppendLine($"Round-trip worst error: {worst:0.000000} px {(worst < 0.01 ? "PASS" : "FAIL")}");
        proj.TryUnproject(new Point(800, 450), out var centre);
        log.AppendLine($"Screen centre lands on map at ({centre.X:0}, {centre.Y:0}); expected about (7400, {6000 + 1950 / Math.Tan(56 * Math.PI / 180):0})");

        // A map point east of centre should appear right of centre; a point north should appear higher.
        proj.TryProject(centre.X + 500, centre.Y, out var east);
        proj.TryProject(centre.X, centre.Y + 500, out var north);
        log.AppendLine($"East is right: {(east.X > 800 ? "PASS" : "FAIL")}, north is up: {(north.Y < 450 ? "PASS" : "FAIL")}");

        // Sample drawings: screen items + map items.
        var red = AppState.Palette[0];
        var pen = new DrawItem { Kind = ToolKind.Pen, Space = Space.Screen, Color = AppState.Palette[2], Thickness = 5 };
        for (int i = 0; i < 60; i++) pen.Points.Add(new Point(80 + i * 6, 120 + Math.Sin(i / 6.0) * 30));
        S.AddItem(pen);
        S.AddItem(new DrawItem { Kind = ToolKind.Highlighter, Space = Space.Screen, Color = AppState.Palette[3], Thickness = 6, Points = { new Point(80, 200), new Point(450, 200) } });
        S.AddItem(new DrawItem { Kind = ToolKind.Text, Space = Space.Screen, Color = Colors.White, Thickness = 6, Text = "Screen text", Points = { new Point(80, 260) } });

        var groundCircle = new DrawItem { Kind = ToolKind.Ellipse, Space = Space.World, Color = red, Thickness = 4, Points = { centre, new Point(centre.X + 600, centre.Y) } };
        S.AddItem(groundCircle);
        S.AddItem(new DrawItem { Kind = ToolKind.Arrow, Space = Space.World, Color = AppState.Palette[4], Thickness = 5, Points = { new Point(centre.X - 1200, centre.Y - 600), new Point(centre.X - 200, centre.Y + 200) } });
        S.AddItem(new DrawItem { Kind = ToolKind.Rectangle, Space = Space.World, Color = AppState.Palette[6], Thickness = 3,
            Points = { new Point(centre.X + 800, centre.Y + 300), new Point(centre.X + 1600, centre.Y + 300), new Point(centre.X + 1600, centre.Y + 1100), new Point(centre.X + 800, centre.Y + 1100) } });
        S.AddItem(new DrawItem { Kind = ToolKind.Text, Space = Space.World, Color = AppState.Palette[2], Thickness = 5, Text = "Ward here", Points = { new Point(centre.X + 900, centre.Y - 700) } });
        S.AddItem(new DrawItem { Kind = ToolKind.Range, Space = Space.World, Color = Color.FromRgb(0xFF, 0xD6, 0x0A), Thickness = 3, Radius = 1350,
            Text = "Champion vision 1350", Points = { new Point(centre.X + 300, centre.Y + 900) } });
        int mi = 0;
        foreach (var (kind, _) in MarkerRenderer.All)
            S.AddItem(new DrawItem { Kind = ToolKind.Marker, Space = Space.World, Marker = kind, Thickness = 6, Points = { new Point(centre.X - 1600 + mi++ * 400, centre.Y - 450) } });

        // Two camera positions: drawings should move with the map between them.
        var cam2 = cam with { Position = new Vec3(7900, 2300, 6300) };
        RenderTo(Path.Combine(folder, "selftest_cam1.png"), cam, viewport);
        RenderTo(Path.Combine(folder, "selftest_cam2.png"), cam2, viewport);
        S.RunAction("wheel", true);
        RenderTo(Path.Combine(folder, "selftest_wheel.png"), cam, viewport);
        S.RunAction("wheel", false);
        S.RunAction("cheatsheet", true);
        RenderTo(Path.Combine(folder, "selftest_cheatsheet.png"), cam, viewport);
        S.RunAction("cheatsheet", false);

        // Undo/redo/clear.
        int n = S.Items.Count;
        S.Undo(); bool undoOk = S.Items.Count == n - 1;
        S.Redo(); bool redoOk = S.Items.Count == n;
        S.Clear(); bool clearOk = S.Items.Count > 0 && S.Items.All(i => i.Space == Space.World); // map drawings stay when no replay is open
        S.Undo(); bool unclearOk = S.Items.Count == n;
        log.AppendLine($"Undo {P(undoOk)}, redo {P(redoOk)}, clear {P(clearOk)}, undo clear {P(unclearOk)}");

        // Shortcut actions.
        S.DrawMode = false;
        S.RunAction("holdDraw", true); bool holdOn = S.DrawMode;
        S.RunAction("holdDraw", false); bool holdOff = !S.DrawMode;
        S.RunAction("tool:Arrow", true); bool toolOk = S.Tool == ToolKind.Arrow && S.DrawMode;
        S.RunAction("range:xp", true); bool rangeOk = S.Tool == ToolKind.Range && S.CurrentRange?.Radius == 1400;
        S.RunAction("color:3", true); bool colorOk = S.Color == AppState.Palette[3];
        S.RunAction("toggleDraw", true); S.RunAction("toggleDraw", false); bool toggleOk = !S.DrawMode; // tap = press + quick release
        log.AppendLine($"Hold to draw {P(holdOn && holdOff)}, tool {P(toolOk)}, range preset {P(rangeOk)}, color {P(colorOk)}, toggle {P(toggleOk)}");
        log.AppendLine($"Key names: {HotkeyBinding.Of(System.Windows.Input.Key.Oem3).Display}, {new HotkeyBinding { Vk = 6, Ctrl = true }.Display}, {HotkeyBinding.Of(System.Windows.Input.Key.D1, shift: true).Display}");

        // Save/load round trip keeps everything.
        var sample = new DrawItem { Kind = ToolKind.Range, Space = Space.World, Color = Colors.Orange, Thickness = 3, Radius = 975, Text = "Q 975",
            ReplayKey = "1834.2", TimeStart = 61.5, TimeEnd = 81.5, Points = { new Point(1200, 3400) } };
        string tmp = Path.Combine(folder, "roundtrip.rpboard");
        Persistence.Save(tmp, new[] { sample });
        var loaded = Persistence.Load(tmp).Single();
        bool rtOk = loaded.Kind == sample.Kind && loaded.Radius == 975 && loaded.Color == Colors.Orange && loaded.ReplayKey == "1834.2" &&
                    loaded.TimeStart == 61.5 && loaded.TimeEnd == 81.5 && loaded.Points[0] == new Point(1200, 3400) && loaded.Text == "Q 975";
        log.AppendLine($"Save/load round trip {P(rtOk)}");

        // Timeline: a note for another replay (or no replay) stays hidden; untimed drawings always show.
        bool hiddenOk = !S.Visible(sample, out _) && S.Visible(new DrawItem { Kind = ToolKind.Pen }, out _);
        log.AppendLine($"Timeline visibility (no replay open) {P(hiddenOk)}");

        // Resize with the select tool (undoable).
        var circle = new DrawItem { Kind = ToolKind.Range, Space = Space.World, Color = Colors.White, Thickness = 3, Radius = 1000, Points = { new Point(5000, 5000) } };
        S.AddItem(circle);
        S.Tool = ToolKind.Select;
        S.Selected = circle;
        S.ScaleSelected(1.1);
        S.ScaleSelected(1.1); // merges into one undo step
        bool scaled = Math.Abs(circle.Radius - 1210) < 0.01;
        S.Undo();
        bool scaleUndo = Math.Abs(circle.Radius - 1000) < 0.01;
        log.AppendLine($"Resize {P(scaled)}, resize undo (merged) {P(scaleUndo)}");

        // Old "Anywhere" flag upgrades to a scope.
        var legacy = new HotkeyBinding { Vk = 0xC0, Anywhere = true };
        log.AppendLine($"Legacy shortcut scope -> {legacy.EffectiveScope} {P(legacy.EffectiveScope == HotkeyScope.Everywhere)}");
        log.AppendLine($"Draw toggle default scope: {HotkeyCatalog.BuiltIn.First(a => a.Id == "toggleDraw").Default.EffectiveScope}");

        // Stroke simplification: a straight line collapses to its ends; corners survive.
        var straight = Enumerable.Range(0, 100).Select(i => new Point(i * 3, i * 1.5)).ToList();
        var zigzag = Enumerable.Range(0, 9).Select(i => new Point(i * 20, i % 2 == 0 ? 0 : 20)).ToList();
        int keptStraight = DrawCanvas.Rdp(straight, 0.5).Count, keptZig = DrawCanvas.Rdp(zigzag, 0.5).Count;
        log.AppendLine($"Simplify straight 100→{keptStraight} {P(keptStraight == 2)}, zigzag 9→{keptZig} {P(keptZig == 9)}");

        // Each tool remembers its own colour.
        S.Selected = null;
        S.Tool = ToolKind.Pen;
        S.Color = AppState.Palette[5];
        S.Tool = ToolKind.Laser;
        var laserColor = S.Color;
        S.Tool = ToolKind.Pen;
        log.AppendLine($"Per-tool colour: pen keeps blue {P(S.Color == AppState.Palette[5])}, laser differs {P(laserColor != AppState.Palette[5])}");
        log.AppendLine($"Last shape remembered: {S.LastShape}");

        // Context index only holds drawings that can show now (no replay open: no replay notes).
        S.AddItem(new DrawItem { Kind = ToolKind.Pen, Space = Space.Screen, Color = Colors.White, Thickness = 2, ReplayKey = "999.9", TimeStart = 1, Points = { new Point(1, 1) } });
        bool ctxOk = S.ContextItems.All(i => i.ReplayKey == null) && S.Items.Any(i => i.ReplayKey == "999.9");
        log.AppendLine($"Context index excludes other replays' notes {P(ctxOk)}");

        // Minimap detection on synthetic screenshots: a 300px square in the bottom-right changes,
        // plus a small decoration above it and a few stray pixels.
        {
            int w = 1600, h = 600, side = 300, x0 = w - side - 10, y0 = h - side - 6;
            var a = new byte[w * h * 4];
            var b = new byte[w * h * 4];
            var rnd = new Random(1);
            rnd.NextBytes(a);
            Array.Copy(a, b, a.Length);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool inMap = x >= x0 && x < x0 + side && y >= y0 && y < y0 + side;
                    bool decoration = x >= x0 + 40 && x < x0 + 120 && y >= y0 - 30 && y < y0;
                    if (inMap || decoration) { int i = (y * w + x) * 4; b[i] = (byte)(a[i] ^ 0xFF); b[i + 1] = (byte)(a[i + 1] ^ 0xFF); b[i + 2] = (byte)(a[i + 2] ^ 0xFF); }
                }
            for (int k = 0; k < 300; k++) { int i = rnd.Next(w * h) * 4; b[i] ^= 0xFF; }
            var found = MinimapDetect.ChangedSquare(a, b, w, h, onLeft: false);
            bool ok = found is var (fx, fy, fs) && Math.Abs(fx - x0) <= 2 && Math.Abs(fy - y0) <= 2 && Math.Abs(fs - side) <= 3;
            log.AppendLine($"Minimap detection (synthetic): {(found is var (gx, gy, gs) ? $"{gs}px at {gx},{gy}" : "none")} {P(ok)}");
        }
        log.AppendLine($"League HUD signature: {MinimapDetect.HudSignature(S.Settings.GameCfgPath) ?? "(game.cfg not readable)"}");

        // Follow circles jump to the view centre (the locked champion) when the camera is attached.
        {
            var follow = new DrawItem { Kind = ToolKind.Range, Space = Space.World, Color = Colors.Yellow, Thickness = 3, Radius = 1350, Follow = true, Points = { new Point(100, 100) } };
            S.AddItem(follow);
            var fc = new DrawCanvas();
            fc.Measure(viewport.Size);
            fc.Arrange(viewport);
            fc.Frame(cam with { Attached = false }, viewport, true);
            bool staysWhenFree = follow.Points[0] == new Point(100, 100);
            fc.Frame(cam with { Attached = true }, viewport, true);
            var lookP = new Projector();
            lookP.Update(cam, viewport, S.Settings.Calibration);
            lookP.TryLookAt(out var look);
            bool follows = (follow.Points[0] - look).Length < 0.01;
            log.AppendLine($"Follow circle: waits while camera free {P(staysWhenFree)}, jumps to locked champion {P(follows)}");
            S.RemoveItems(new[] { follow });
        }

        // Spotlight + ping render.
        S.Spotlight = true;
        RenderTo(Path.Combine(folder, "selftest_spotlight.png"), cam, viewport, c => { c.PingAtCursor(); });
        S.Spotlight = false;

        // Prediction pushes the camera along its motion.
        var api = new ReplayApiClient();
        log.AppendLine($"Predict with no camera returns null: {P(api.Predict(10) == null)}");

        File.WriteAllText(Path.Combine(folder, "selftest.txt"), log.ToString());
    }

    static string P(bool ok) => ok ? "PASS" : "FAIL";

    /// <summary>`--demo`: a few screen drawings so the live overlay can be checked visually.</summary>
    public static void AddDemoItems()
    {
        var S = AppState.I;
        var pen = new DrawItem { Kind = ToolKind.Pen, Space = Space.Screen, Color = AppState.Palette[2], Thickness = 6 };
        for (int i = 0; i < 60; i++) pen.Points.Add(new Point(200 + i * 8, 400 + Math.Sin(i / 6.0) * 40));
        S.AddItem(pen);
        S.AddItem(new DrawItem { Kind = ToolKind.Arrow, Space = Space.Screen, Color = AppState.Palette[0], Thickness = 6, Points = { new Point(200, 520), new Point(680, 520) } });
        S.AddItem(new DrawItem { Kind = ToolKind.Text, Space = Space.Screen, Color = Colors.White, Thickness = 6, Text = "RiftPen demo", Points = { new Point(200, 600) } });
        S.AddItem(new DrawItem { Kind = ToolKind.Marker, Space = Space.Screen, Marker = MarkerKind.Ward, Thickness = 8, Points = { new Point(760, 520) } });
    }

    static void RenderTo(string file, CameraState cam, Rect viewport, Action<DrawCanvas>? setup = null)
    {
        var canvas = new DrawCanvas();
        canvas.Frame(cam, viewport, visible: true);
        setup?.Invoke(canvas);
        canvas.Measure(viewport.Size);
        canvas.Arrange(viewport);
        canvas.UpdateLayout();

        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(38, 52, 44)), null, viewport); // stand-in for the game
        var bmp = new RenderTargetBitmap((int)viewport.Width, (int)viewport.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(background);
        bmp.Render(canvas);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
