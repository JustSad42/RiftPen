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

        // Objective timers follow the spawn rules.
        {
            var g = new GameTimeline { Participants = { new GameParticipant { Id = 1, Team = 100 }, new GameParticipant { Id = 6, Team = 200 } } };
            ObjectiveTimer Get(double t, string kind) => Objectives.At(g, t).First(o => o.Kind == kind);
            bool firstDrake = Math.Abs(Get(200, "dragon").SpawnsIn - 100) < 0.01 && Get(301, "dragon").Up;
            bool grubsFirst = Math.Abs(Get(100, "grubs").SpawnsIn - 380) < 0.01;
            foreach (var at in new[] { 400.0, 800, 1200, 1600 })
                g.Events.Add(new GameEvent { Type = "ELITE_MONSTER_KILL", MonsterType = "DRAGON", MonsterSubType = "FIRE_DRAGON", KillerId = 1, Time = at });
            bool respawn = Math.Abs(Get(500, "dragon").SpawnsIn - 200) < 0.01 && Get(500, "dragon").Name == "Drake";
            bool elder = Get(1700, "dragon").Name == "Elder Dragon" && Math.Abs(Get(1700, "dragon").SpawnsIn - 260) < 0.01;
            bool baron = Objectives.At(g, 1100).Any(o => o.Kind == "herald") && Math.Abs(Get(1190, "baron").SpawnsIn - 10) < 0.01 && Get(1250, "baron").Up;
            g.Events.Add(new GameEvent { Type = "ELITE_MONSTER_KILL", MonsterType = "BARON_NASHOR", KillerId = 6, Time = 1300 });
            bool baronRespawn = Math.Abs(Get(1400, "baron").SpawnsIn - 260) < 0.01;
            log.AppendLine($"Objective timers: first drake {P(firstDrake)}, grubs {P(grubsFirst)}, drake respawn {P(respawn)}, Elder after soul {P(elder)}, Baron {P(baron)}, Baron respawn {P(baronRespawn)}");
        }

        // "Hide from stream capture": a guarded window vanishes from screen captures, and comes back when switched off.
        {
            bool before = S.Settings.HideUiFromCapture;
            var probe = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = new SolidColorBrush(Color.FromRgb(255, 0, 255)),
                Width = 160, Height = 160, Left = 40, Top = 40, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
            };
            CaptureGuard.Guard(probe);
            probe.Show();
            bool Magenta()
            {
                probe.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                Thread.Sleep(250);
                Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(probe).Handle, out var r);
                var mid = new Native.RECT { Left = r.Left + r.Width / 2 - 2, Top = r.Top + r.Height / 2 - 2, Right = r.Left + r.Width / 2 + 2, Bottom = r.Top + r.Height / 2 + 2 };
                var px = Screenshot.CaptureBgra(mid, includeOverlays: true);
                return px != null && px[2] > 200 && px[1] < 60 && px[0] > 200;
            }
            S.Settings.HideUiFromCapture = false; CaptureGuard.Refresh();
            bool shownWhenOff = Magenta();
            S.Settings.HideUiFromCapture = true; CaptureGuard.Refresh();
            bool hiddenWhenOn = !Magenta();
            S.Settings.HideUiFromCapture = before; CaptureGuard.Refresh();
            probe.Close();
            log.AppendLine($"Hide from stream capture: captured when off {P(shownWhenOff)}, left out when on {P(hiddenWhenOn)}");
        }

        // Choices from last time survive a save and reload.
        {
            var saved = new AppSettings { LastTool = nameof(ToolKind.Arrow), LastSpaceMode = nameof(SpaceMode.Map), BoardTrails = false, BoardLinked = false,
                BoardZoom = 2.5, BoardCenter = new double[] { 7000, 7400 }, TimelineEnabled = false, ShowNotesTimeline = false, BoardCleanMap = true };
            var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(saved))!;
            bool kept = back.LastTool == "Arrow" && back.LastSpaceMode == "Map" && !back.BoardTrails && !back.BoardLinked && back.BoardZoom == 2.5 &&
                        back.BoardCenter is [7000, 7400] && !back.TimelineEnabled && !back.ShowNotesTimeline && back.BoardCleanMap;
            log.AppendLine($"Last-used choices (tool, mode, Trails, Linked, board view, notes) are kept: {P(kept)}");
            log.AppendLine($"Self-tests don't autosave your settings: {P(!S.SettingsAutosave)}");
        }

        // The replay notes bar can be hidden and brought back.
        {
            bool before = S.Settings.ShowNotesTimeline;
            S.RunAction("notesBar", true);
            bool flipped = S.Settings.ShowNotesTimeline != before;
            S.RunAction("notesBar", true);
            log.AppendLine($"Notes bar toggle hides and shows it again: {P(flipped && S.Settings.ShowNotesTimeline == before)}");
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

    /// <summary>
    /// `--track-test <folder> [seconds]`: pause the open replay on a timeline frame, find every champion on the
    /// minimap, and compare with Riot's recorded positions for that moment.
    /// </summary>
    public static async Task RunTrackTest(string folder, double atTime)
    {
        Directory.CreateDirectory(folder);
        var log = new StringBuilder();
        var S = AppState.I;
        S.Replay.Start();
        using var live = new LiveClient();
        live.Start();
        for (int i = 0; i < 40 && (S.Replay.Playback == null || live.Players.Count == 0); i++) await Task.Delay(250);
        var match = ReplayFiles.FindMatch(S.Replay.Playback?.Length ?? 0, live.Players.Select(p => p.ChampionKey));
        if (match == null) { log.AppendLine("No replay matched."); File.WriteAllText(Path.Combine(folder, "tracktest.txt"), log.ToString()); return; }
        var game = await GameLoader.LoadAsync(match.Platform, match.GameId);

        var leagueHwnd = Native.FindLeagueWindow();
        if (!Native.TryGetClientScreenRect(leagueHwnd, out var league)) { log.AppendLine("League window not found."); File.WriteAllText(Path.Combine(folder, "tracktest.txt"), log.ToString()); return; }

        double[]? frac = S.Settings.MinimapRect;
        if (frac == null)
        {
            var (rect, msg) = await MinimapDetect.DetectAsync(league, _ => { });
            log.AppendLine("Minimap detection: " + msg);
            frac = rect;
        }
        if (frac == null) { File.WriteAllText(Path.Combine(folder, "tracktest.txt"), log.ToString()); return; }
        var mm = new Native.RECT
        {
            Left = league.Left + (int)Math.Round(frac[0] * league.Width),
            Top = league.Top + (int)Math.Round(frac[1] * league.Height),
        };
        mm.Right = mm.Left + (int)Math.Round(frac[2] * league.Width);
        mm.Bottom = mm.Top + (int)Math.Round(frac[3] * league.Height);
        log.AppendLine($"Minimap on screen: {mm.Left},{mm.Top} {mm.Width}x{mm.Height}");

        var tracker = await ChampionTracker.CreateAsync(game);
        log.AppendLine($"Started with saved icons for {tracker.PreloadedCount}/10 champions");
        var tracks = new GameTracks("test");
        var rawTracks = new GameTracks("test-raw"); // every sighting, as recorded before the speed filter
        int w = mm.Width, h = mm.Height;
        var allErrors = new List<double>();
        int idRight = 0, idTotal = 0;

        // For each checkpoint: play the 15 s before a timeline frame while tracking, then compare with Riot's positions.
        foreach (double want in new[] { atTime, atTime + 240, atTime + 540 })
        {
            var frame = game.Frames.OrderBy(f => Math.Abs(f.Time - want)).First();
            S.Replay.SetPaused(true);
            await Task.Delay(300);
            S.Replay.Seek(frame.Time - 15);
            await Task.Delay(3000);
            tracker.ResetMotion();
            S.Replay.SetPaused(false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int passes = 0;
            var earlyScores = new List<double>();
            double procMs = 0;
            byte[]? lastPx = null;
            List<MinimapVision.Ring>? lastRings = null;
            List<ChampionTracker.Sighting>? lastSeen = null;
            while (sw.Elapsed.TotalSeconds < 25)
            {
                double t = S.Replay.CurrentTime ?? 0;
                if (t > frame.Time + 2) break;
                var px = Screenshot.CaptureBgra(mm);
                if (px == null) { await Task.Delay(200); continue; }
                var ps = System.Diagnostics.Stopwatch.StartNew();
                var seen = tracker.Process(px, w, h, t, key => live.Players.FirstOrDefault(p => p.ChampionKey == key)?.IsDead == true, out var rings);
                procMs += ps.Elapsed.TotalMilliseconds;
                passes++;
                foreach (var s in seen) rawTracks.Add(s.ParticipantId, t, s.World);
                foreach (var s in tracker.Plausible(seen, t)) tracks.Add(s.ParticipantId, t, s.World);
                if (passes <= 5) earlyScores.AddRange(seen.Where(s => !s.Hidden).Select(s => s.Appearance));
                if (t <= frame.Time + 0.3) { lastPx = px; lastRings = rings; lastSeen = seen; }
                await Task.Delay(150);
            }
            S.Replay.SetPaused(true);
            log.AppendLine();
            log.AppendLine($"== Frame at {frame.Time:0.0}s: {passes} passes, {procMs / Math.Max(1, passes):0.0} ms each, learned icons {tracker.LearnedCount}/10, first-second match score {(earlyScores.Count > 0 ? earlyScores.Average() : 0):0.00}");

            var errors = new List<double>();
            foreach (var p in game.Participants)
            {
                if (!frame.Participants.TryGetValue(p.Id, out var pf) || pf.Position is not Point tp) continue;
                bool got = tracks.TryGet(p.Id, frame.Time, out var pos);
                // Identity check: is the champion's ring nearest the truth labelled with this champion?
                string idNote = "";
                if (lastSeen != null && lastRings != null)
                {
                    var tpx = MinimapVision.ToPixel(tp, w, h);
                    var near = lastSeen.Where(s => game.Participants.First(x => x.Id == s.ParticipantId).Team == p.Team)
                                       .OrderBy(s => (s.Pixel - tpx).Length).FirstOrDefault();
                    if (near != null && (near.Pixel - tpx).Length < 25)
                    {
                        idTotal++;
                        if (near.ParticipantId == p.Id) idRight++;
                        idNote = near.ParticipantId == p.Id ? " id ok" : $" id WRONG (nearest icon labelled {near.ChampionKey})";
                    }
                }
                if (got) { errors.Add((pos - tp).Length); allErrors.Add((pos - tp).Length); }
                log.AppendLine($"  {p.ChampionKey,-12} Riot ({tp.X:0},{tp.Y:0}) " + (got ? $"tracked ({pos.X:0},{pos.Y:0}) error {(pos - tp).Length:0}" : "not tracked") + idNote + (lastSeen?.FirstOrDefault(s => s.ParticipantId == p.Id && !s.Hidden) is { } ls ? $" match {ls.Appearance:0.00}" : ""));
            }
            if (errors.Count > 0) log.AppendLine($"  median error {errors.OrderBy(x => x).ElementAt(errors.Count / 2):0} units over {errors.Count} champions");

            // Smoothness: play the stretch back at 30 fps the old way and the new way.
            int oldJumps = 0, oldDrops = 0, newJumps = 0, newDrops = 0, newFrames = 0, oldFrames = 0, total = 0;
            foreach (var p in game.Participants)
            {
                Point? po = null, pn = null;
                bool oo = true, on = true;
                for (double t = frame.Time - 14; t <= frame.Time + 1.5; t += 1 / 30.0)
                {
                    total++;
                    bool okOld = rawTracks.TryGet(p.Id, t, out var a, maxGap: 4.5);
                    bool okNew = tracks.Lookup(p.Id, t, out var b) != GameTracks.Fix.None;
                    if (okOld) oldFrames++;
                    if (okNew) newFrames++;
                    if (okOld && po is Point lo && (a - lo).Length > 250) oldJumps++;
                    if (okNew && pn is Point ln && (b - ln).Length > 250) newJumps++;
                    if (okOld != oo) oldDrops++;
                    if (okNew != on) newDrops++;
                    po = okOld ? a : null; pn = okNew ? b : null; oo = okOld; on = okNew;
                }
            }
            log.AppendLine($"  playback: impossible jumps old {oldJumps} → new {newJumps}; drop-outs to the estimate old {oldDrops} → new {newDrops}; frames from the minimap old {100.0 * oldFrames / total:0}% → new {100.0 * newFrames / total:0}%");

            if (lastPx != null)
            {
                var img = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, lastPx, w * 4);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(img, new Rect(0, 0, w, h));
                    foreach (var r in lastRings!)
                        dc.DrawEllipse(null, new Pen(r.Team == 100 ? Brushes.Cyan : Brushes.Orange, 1), r.Center, 3, 3);
                    foreach (var p in game.Participants)
                        if (frame.Participants.TryGetValue(p.Id, out var pf) && pf.Position is Point tp)
                            dc.DrawEllipse(null, new Pen(Brushes.Lime, 2), MinimapVision.ToPixel(tp, w, h), 4, 4);
                    foreach (var s in lastSeen!)
                    {
                        var ft = new FormattedText(s.ChampionKey, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface("Segoe UI"), 11, s.Hidden ? Brushes.Gray : s.Confident ? Brushes.Yellow : Brushes.White, 1);
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null, new Rect(s.Pixel.X - ft.Width / 2, s.Pixel.Y + 12, ft.Width, ft.Height));
                        dc.DrawText(ft, new Point(s.Pixel.X - ft.Width / 2, s.Pixel.Y + 12));
                    }
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(folder, $"track_{frame.Time:0}.png"));
                enc.Save(fs);
            }
        }
        log.AppendLine();
        IconLibrary.Shared.Save();
        if (allErrors.Count > 0) log.AppendLine($"Overall median error {allErrors.OrderBy(x => x).ElementAt(allErrors.Count / 2):0} units, identity {idRight}/{idTotal} right");
        File.WriteAllText(Path.Combine(folder, "tracktest.txt"), log.ToString());
    }

    /// <summary>`--board-test-offline <folder>`: the Rift Board on the most recent saved game, no replay needed.</summary>
    public static async Task RunBoardTestOffline(string folder)
    {
        Directory.CreateDirectory(folder);
        var log = new StringBuilder();
        AppState.I.AutosaveEnabled = false;
        var file = new DirectoryInfo(GameLoader.CacheFolder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        if (file == null) { File.WriteAllText(Path.Combine(folder, "boardtest.txt"), "No saved games."); return; }
        var game = System.Text.Json.JsonSerializer.Deserialize<GameTimeline>(await File.ReadAllTextAsync(file.FullName))!;
        var R = RiftSession.I;
        R.TestLoad(game);
        log.AppendLine($"Game {game.Key}: {R.Tracks?.SampleCount} recorded positions");
        log.AppendLine($"Recorded stretches: {string.Join(", ", (R.Tracks?.Coverage() ?? new()).Where(c => c.End - c.Start > 5).Select(c => $"{AppState.FormatTime(c.Start)}–{AppState.FormatTime(c.End)}"))}");
        RiftBoardWindow.Open();
        var w = RiftBoardWindow.Instance!;
        var spans = (R.Tracks?.Coverage() ?? new()).Where(c => c.End - c.Start > 30).ToList();
        double at = spans.Count > 0 ? spans.OrderByDescending(c => c.End - c.Start).First().End - 2 : 900;
        log.AppendLine($"Showing {AppState.FormatTime(at)}");
        foreach (var o in Objectives.At(game, at)) log.AppendLine($"  timer: {o.Name} {(o.Up ? "up" : Objectives.Clock(o.SpawnsIn))}");

        w.TestSandbox(at);
        w.TestSelect(null);
        w.TestPaths(BoardPathMode.Path, 120, "TOP");
        await Task.Delay(1200);
        w.Snapshot(Path.Combine(folder, "board_paths_all.png"));
        w.TestPaths(BoardPathMode.Heatmap, 0, "MIDDLE");
        await Task.Delay(800);
        w.Snapshot(Path.Combine(folder, "board_heatmap.png"));
        w.TestSelect(game.Participants[1].Id);
        w.TestPaths(BoardPathMode.Path, 300, "JUNGLE");
        await Task.Delay(800);
        w.Snapshot(Path.Combine(folder, "board_path_jungler.png"));
        w.TestPaths(BoardPathMode.Path, 300, "TOP");
        await Task.Delay(500);
        w.SnapshotSide(Path.Combine(folder, "board_side.png"));
        w.TestPaths(BoardPathMode.Off, 120, "TOP");

        // Drawings that follow champions, and ward vision.
        var S = AppState.I;
        var jungler = game.Participants[1];
        var top = game.Participants[0];
        var added = new List<DrawItem>
        {
            new() { Kind = ToolKind.Marker, Space = Space.World, Marker = MarkerKind.Ward, Thickness = 4, Points = { new Point(4500, 7400) } },
            new() { Kind = ToolKind.Marker, Space = Space.World, Marker = MarkerKind.ControlWard, Thickness = 4, Points = { new Point(9800, 6400) } },
            new() { Kind = ToolKind.Range, Space = Space.World, Color = Colors.Yellow, Thickness = 3, Radius = 1350, Text = "Champion vision 1350", FollowKey = jungler.ChampionKey, Points = { new Point(0, 0) } },
            new() { Kind = ToolKind.Arrow, Space = Space.World, Color = Colors.Cyan, Thickness = 6, FollowKey = top.ChampionKey, Points = { new Point(0, 0), new Point(7400, 7400) } },
        };
        foreach (var it in added) S.AddItem(it);
        w.TestSelect(null);
        w.TestSandbox(at - 20);
        await Task.Delay(800);
        w.Snapshot(Path.Combine(folder, "board_follow_a.png"));
        var circleA = added[2].Points[0];
        w.TestSandbox(at);
        await Task.Delay(800);
        w.Snapshot(Path.Combine(folder, "board_follow_b.png"));
        var circleB = added[2].Points[0];
        var truth = R.StateAt(jungler, at).Pos;
        log.AppendLine($"Circle on {jungler.ChampionName}: moved {(circleB - circleA).Length:0} units in 20 s, sits {(circleB - truth).Length:0} units from them");
        log.AppendLine($"Arrow starts on {top.ChampionName}: {(added[3].Points[0] - R.StateAt(top, at).Pos).Length:0} units off");
        S.RemoveItems(added);

        w.Close();
        File.WriteAllText(Path.Combine(folder, "boardtest.txt"), log.ToString());
    }

    /// <summary>`--board-test <folder>`: open the Rift Board on the open replay, play a bit, and save screenshots of it.</summary>
    public static async Task RunBoardTest(string folder)
    {
        Directory.CreateDirectory(folder);
        var log = new StringBuilder();
        var S = AppState.I;
        S.AutosaveEnabled = false;
        S.Replay.Start();
        var R = RiftSession.I;
        R.Start(Application.Current.Dispatcher);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (R.Game == null && sw.Elapsed.TotalSeconds < 40) await Task.Delay(250);
        log.AppendLine($"Game: {R.Game?.Key ?? "none"} after {sw.Elapsed.TotalSeconds:0.0}s; status '{R.Status}'");
        if (R.Game == null) { File.WriteAllText(Path.Combine(folder, "boardtest.txt"), log.ToString()); return; }

        RiftBoardWindow.Open();
        var w = RiftBoardWindow.Instance!;
        S.Replay.SetPaused(true);
        await Task.Delay(300);
        S.Replay.Seek(590);
        await Task.Delay(2500);
        S.Replay.SetPaused(false);
        await Task.Delay(12000);
        S.Replay.SetPaused(true);
        await Task.Delay(1500);
        log.AppendLine($"Recorded samples: {R.Tracks?.SampleCount}; track status '{R.TrackStatus}'");
        double now = S.Replay.CurrentTime ?? 0;
        foreach (var p in R.Game.Participants)
        {
            var st = R.StateAt(p, now);
            log.AppendLine($"  {p.ChampionKey,-12} {st.Quality,-9} ({st.Pos.X:0},{st.Pos.Y:0}) lvl {st.Level}{(st.Dead ? $" dead {st.RespawnIn:0}s" : "")}");
        }
        w.TestDraw(new DrawItem { Kind = ToolKind.Arrow, Space = Space.World, Color = Colors.Yellow, Thickness = 6, Points = { new Point(5000, 5000), new Point(8000, 8000) } });
        w.TestSelect(R.Game.Participants[0].Id);
        await Task.Delay(800);
        w.Snapshot(Path.Combine(folder, "board_linked.png"));

        // Paths, heatmap and lane matchup.
        w.TestSelect(null);
        w.TestPaths(BoardPathMode.Heatmap, 0, "MIDDLE");
        await Task.Delay(800);
        w.Snapshot(Path.Combine(folder, "board_heatmap.png"));
        w.TestSelect(R.Game.Participants[1].Id);
        w.TestPaths(BoardPathMode.Path, 300, "JUNGLE");
        await Task.Delay(800);
        w.Snapshot(Path.Combine(folder, "board_path.png"));
        log.AppendLine($"Recorded stretches available for paths: {string.Join(", ", (R.Tracks?.Coverage() ?? new()).Where(c => c.End - c.Start > 5).Select(c => $"{AppState.FormatTime(c.Start)}–{AppState.FormatTime(c.End)}"))}");
        w.TestPaths(BoardPathMode.Off, 120, "TOP");

        // Sandbox: jump ahead without moving the replay, drag a champion.
        w.TestSandbox(1250);
        w.TestMove(R.Game.Participants[5].Id, new Point(7400, 7400));
        await Task.Delay(800);
        w.Snapshot(Path.Combine(folder, "board_sandbox.png"));
        log.AppendLine($"Replay time stayed at {S.Replay.CurrentTime:0.0}s while the board showed 1250s");
        S.RemoveItems(S.Items.Where(i => i.Kind == ToolKind.Arrow && i.Color == Colors.Yellow).ToList());
        w.Close();
        File.WriteAllText(Path.Combine(folder, "boardtest.txt"), log.ToString());
    }

    /// <summary>`--game-test <folder>`: identify the open replay and load its game data (needs a replay running).</summary>
    public static async Task RunGameTest(string folder)
    {
        Directory.CreateDirectory(folder);
        var log = new StringBuilder();
        var S = AppState.I;
        S.Replay.Start();
        using var live = new LiveClient();
        live.Start();
        for (int i = 0; i < 40 && (S.Replay.Playback == null || live.Players.Count == 0); i++) await Task.Delay(250);
        log.AppendLine($"Replay connected: {S.Replay.Connected}, length {S.Replay.Playback?.Length:0.0}s, live players: {live.Players.Count}");
        foreach (var p in live.Players)
            log.AppendLine($"  {p.ChampionKey,-12} team {p.Team} {p.Position,-8} lvl {p.Level} {p.Kills}/{p.Deaths}/{p.Assists} cs {p.CreepScore} items {p.Items.Length} ({p.ItemValue}g)");

        var match = ReplayFiles.FindMatch(S.Replay.Playback?.Length ?? 0, live.Players.Select(p => p.ChampionKey));
        log.AppendLine($"Matched replay file: {match?.Path ?? "none"}");
        if (match != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var game = await GameLoader.LoadAsync(match.Platform, match.GameId);
                log.AppendLine($"Loaded {game.Key} from {game.Source} in {sw.ElapsedMilliseconds} ms: length {game.Length:0}s, {game.Frames.Count} frames, {game.Events.Count} events");
                foreach (var p in game.Participants)
                    log.AppendLine($"  #{p.Id} {p.ChampionName} ({p.ChampionKey}) team {p.Team} {p.Lane} {p.Kills}/{p.Deaths}/{p.Assists}");
                log.AppendLine("  events: " + string.Join(", ", game.Events.GroupBy(e => e.Type).Select(g => $"{g.Key}={g.Count()}")));
            }
            catch (Exception ex) { log.AppendLine("Load failed: " + ex.Message); }
        }
        File.WriteAllText(Path.Combine(folder, "gametest.txt"), log.ToString());
    }

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
