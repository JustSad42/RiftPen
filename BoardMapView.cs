using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RiftPen;

public enum BoardPathMode { Off, Path, Heatmap }

/// <summary>What a drawing surface needs to draw on the Rift Board instead of over the game.</summary>
public interface IBoardView
{
    Point WorldToView(Point world);
    Point ViewToWorld(Point view);
    /// <summary>Mouse input draws (otherwise it moves the map and champions).</summary>
    bool Drawing { get; }
    /// <summary>The replay moment the board shows.</summary>
    double? Time { get; }
}

/// <summary>
/// The big Summoner's Rift map: official map art, towers, champions where they were at the board's moment,
/// recent kills and objectives, movement trails and the replay camera's view. Pan and zoom like a map app.
/// </summary>
public sealed class BoardMapView : FrameworkElement, IBoardView
{
    static readonly Rect W = MinimapVision.WorldBounds;
    static readonly Color BlueTeam = Color.FromRgb(0, 153, 224), RedTeam = Color.FromRgb(232, 61, 61);
    static readonly Brush Back = Frozen(new SolidColorBrush(Color.FromRgb(14, 16, 20)));
    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    readonly RiftSession R = RiftSession.I;
    readonly AppState S = AppState.I;

    public double Zoom { get; private set; } = 1;
    Vector pan;

    public Func<double>? TimeSource { get; set; }
    public bool Drawing { get; set; }
    double? IBoardView.Time => TimeSource?.Invoke();
    public double Now => TimeSource?.Invoke() ?? 0;

    public bool CleanMap { get; set; }
    public bool Trails { get; set; } = true; // set from settings by the window
    /// <summary>Show where the replay camera is looking (the board is at the replay's moment).</summary>
    public bool ShowCamera { get; set; }
    public int? SelectedId { get; set; }
    public int? HoverId { get; set; }

    /// <summary>Champions dragged by hand, and the moment they were moved at (they snap back when time moves on).</summary>
    public Dictionary<int, Point> Moved { get; } = new();
    public double MovedAt { get; set; } = double.NaN;

    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    // ---------------------------------------------------------------- view transform

    double Scale => Math.Max(1, Math.Min(ActualWidth, ActualHeight)) / W.Height * Zoom;
    Point Origin => new(ActualWidth / 2 - W.Width * Scale / 2 + pan.X, ActualHeight / 2 - W.Height * Scale / 2 + pan.Y);

    public Point WorldToView(Point w)
    {
        double s = Scale;
        var o = Origin;
        return new Point(o.X + (w.X - W.X) * s, o.Y + (W.Bottom - w.Y) * s);
    }

    public Point ViewToWorld(Point v)
    {
        double s = Scale;
        var o = Origin;
        return new Point(W.X + (v.X - o.X) / s, W.Bottom - (v.Y - o.Y) / s);
    }

    public void ZoomAt(Point view, double factor)
    {
        var world = ViewToWorld(view);
        Zoom = Math.Clamp(Zoom * factor, 0.6, 10);
        pan += view - WorldToView(world);
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void PanBy(Vector v) { pan += v; InvalidateVisual(); ViewChanged?.Invoke(); }

    public void ResetView() { Zoom = 1; pan = default; InvalidateVisual(); ViewChanged?.Invoke(); }

    /// <summary>Raised after zooming, panning or resetting (the window saves the view).</summary>
    public event Action? ViewChanged;

    /// <summary>The world point (X, Z) in the middle of the view.</summary>
    public Point CenterWorld => ViewToWorld(new Point(ActualWidth / 2, ActualHeight / 2));

    /// <summary>Restores a saved view: zoom level, and which world point sits in the middle.</summary>
    public void SetView(double zoom, Point centerWorld)
    {
        Zoom = Math.Clamp(zoom, 0.6, 10);
        pan = default;
        pan += new Point(ActualWidth / 2, ActualHeight / 2) - WorldToView(centerWorld);
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- images

    static readonly Dictionary<string, (BitmapSource Color, BitmapSource Gray)?> icons = new();
    static BitmapSource? mapArt, mapGray;
    static bool mapRequested;

    static (BitmapSource, BitmapSource) Load(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        var gray = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
        gray.Freeze();
        return (bmp, gray);
    }

    async void RequestIcon(string key)
    {
        if (icons.ContainsKey(key)) return;
        icons[key] = null;
        try
        {
            var path = await DataDragon.ChampionIconAsync(key);
            if (path != null) { icons[key] = Load(path); InvalidateVisual(); }
        }
        catch (Exception ex) { AppSettings.LogError(ex); }
    }

    async void RequestMap()
    {
        if (mapRequested) return;
        mapRequested = true;
        try
        {
            var path = await DataDragon.MapImageAsync();
            if (path != null) { (mapArt, mapGray) = Load(path); InvalidateVisual(); }
        }
        catch (Exception ex) { AppSettings.LogError(ex); mapRequested = false; }
    }

    // ---------------------------------------------------------------- structures

    enum Building { Tower, Inhibitor, Nexus }

    /// <summary>Summoner's Rift towers, inhibitors and nexuses (world X, Z).</summary>
    static readonly (int Team, Building Kind, Point P)[] Structures =
    {
        (100, Building.Tower, new(10504, 1029)), (100, Building.Tower, new(6919, 1483)), (100, Building.Tower, new(4281, 1253)),
        (100, Building.Tower, new(5846, 6396)), (100, Building.Tower, new(5048, 4812)), (100, Building.Tower, new(3651, 3696)),
        (100, Building.Tower, new(981, 10441)), (100, Building.Tower, new(1512, 6699)), (100, Building.Tower, new(1169, 4287)),
        (100, Building.Tower, new(1748, 2270)), (100, Building.Tower, new(2177, 1807)),
        (100, Building.Inhibitor, new(1171, 3571)), (100, Building.Inhibitor, new(3203, 3208)), (100, Building.Inhibitor, new(3452, 1236)),
        (100, Building.Nexus, new(1551, 1659)),
        (200, Building.Tower, new(4318, 13875)), (200, Building.Tower, new(7943, 13411)), (200, Building.Tower, new(10481, 13650)),
        (200, Building.Tower, new(8955, 8510)), (200, Building.Tower, new(9767, 10113)), (200, Building.Tower, new(11134, 11207)),
        (200, Building.Tower, new(13866, 4505)), (200, Building.Tower, new(13327, 8226)), (200, Building.Tower, new(13624, 10572)),
        (200, Building.Tower, new(12611, 13084)), (200, Building.Tower, new(13052, 12612)),
        (200, Building.Inhibitor, new(11261, 13676)), (200, Building.Inhibitor, new(11598, 11667)), (200, Building.Inhibitor, new(13604, 11316)),
        (200, Building.Nexus, new(13300, 13300)),
    };

    GameTimeline? structuresFor;
    List<double>[] destroyed = Array.Empty<List<double>>();

    void MatchBuildings(GameTimeline g)
    {
        structuresFor = g;
        destroyed = Structures.Select(_ => new List<double>()).ToArray();
        foreach (var e in g.Events.Where(e => e.Type == "BUILDING_KILL" && e.Position != null))
        {
            int best = -1;
            double bestD = 1500;
            for (int i = 0; i < Structures.Length; i++)
            {
                if (e.TeamId != 0 && Structures[i].Team != e.TeamId) continue;
                double d = (Structures[i].P - e.Position!.Value).Length;
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0) destroyed[best].Add(e.Time);
        }
    }

    bool StructureUp(int i, double t)
    {
        foreach (var d in destroyed[i])
            if (d <= t && (Structures[i].Kind != Building.Inhibitor || t < d + 300)) return false;
        return true;
    }

    // ---------------------------------------------------------------- objective timers

    public bool ShowObjectives { get; set; } = true;

    static readonly Dictionary<string, Color> ObjectiveColors = new()
    {
        ["dragon"] = Color.FromRgb(255, 140, 60), ["grubs"] = Color.FromRgb(180, 120, 255),
        ["herald"] = Color.FromRgb(200, 110, 255), ["baron"] = Color.FromRgb(170, 90, 255),
    };

    void DrawObjectives(DrawingContext dc, GameTimeline g, double t, double r)
    {
        foreach (var o in Objectives.At(g, t))
        {
            var v = WorldToView(o.Pit);
            var c = ObjectiveColors.TryGetValue(o.Kind, out var oc) ? oc : Colors.Gold;
            string text = o.Up ? $"{o.Name} · up" : $"{o.Name} {Objectives.Clock(o.SpawnsIn)}";
            var ft = Text(text, Math.Clamp(r * 0.62, 10, 15), Brushes.White, Bold);
            var box = new Rect(v.X - ft.Width / 2 - 7, v.Y - ft.Height / 2 - 3, ft.Width + 14, ft.Height + 6);
            if (o.Up) dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, c.R, c.G, c.B)), 5), box, 8, 8);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(o.Up ? (byte)235 : (byte)205, 16, 18, 24)),
                new Pen(new SolidColorBrush(o.Up ? c : Color.FromArgb(150, c.R, c.G, c.B)), o.Up ? 2 : 1.2), box, 7, 7);
            dc.DrawText(ft, new Point(box.X + 7, box.Y + 3));
        }
    }

    /// <summary>Countdown on a destroyed inhibitor until it comes back (5 minutes).</summary>
    void DrawInhibitorTimers(DrawingContext dc, double t, double r)
    {
        for (int i = 0; i < Structures.Length; i++)
        {
            if (Structures[i].Kind != Building.Inhibitor) continue;
            var down = destroyed[i].Where(d => d <= t && t < d + 300).DefaultIfEmpty(double.NaN).Max();
            if (double.IsNaN(down)) continue;
            var v = WorldToView(Structures[i].P);
            var ft = Text(Objectives.Clock(down + 300 - t), Math.Clamp(r * 0.55, 9, 13), Brushes.White, Bold);
            var at = new Point(v.X - ft.Width / 2, v.Y + r * 0.55);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 16, 18, 24)), null, new Rect(at.X - 4, at.Y - 1, ft.Width + 8, ft.Height + 2), 4, 4);
            dc.DrawText(ft, at);
        }
    }

    // ---------------------------------------------------------------- paths and heatmap

    /// <summary>Where champions went: a fading line (Path) or where they spent their time (Heatmap).</summary>
    public BoardPathMode PathMode { get; set; } = BoardPathMode.Off;
    /// <summary>How far back paths and heatmaps look, in seconds (0 = the whole game so far).</summary>
    public double PathWindow { get; set; } = 120;

    (string Key, List<(int Team, float[] Cells)> Layers, float Max)? heatCache;
    const int HeatN = 64;

    /// <summary>
    /// Recorded positions tidied for drawing: single readings that jump away and straight back (a mixed-up icon)
    /// are dropped, and the rest are averaged over about a second so the line follows the walk, not the jitter.
    /// </summary>
    static List<(double T, Point P)> CleanPath(List<(double T, Point P)> s)
    {
        if (s.Count < 3) return s;
        var kept = new List<(double T, Point P)> { s[0] };
        for (int i = 1; i < s.Count - 1; i++)
        {
            var (ta, a) = kept[^1];
            var (tb, b) = s[i];
            var (tc, c) = s[i + 1];
            double reachAB = ChampionTracker.MaxRunSpeed * (tb - ta) + 450, reachBC = ChampionTracker.MaxRunSpeed * (tc - tb) + 450;
            bool spike = (b - a).Length > reachAB && (c - b).Length > reachBC && (c - a).Length <= ChampionTracker.MaxRunSpeed * (tc - ta) + 450;
            if (!spike) kept.Add(s[i]);
        }
        kept.Add(s[^1]);

        // Short isolated stretches (a couple of seconds of mixed-up icons in a fight) are left out.
        var runs = new List<List<(double T, Point P)>>();
        foreach (var x in kept)
        {
            if (runs.Count > 0 && runs[^1][^1] is var last && x.T - last.T <= 5 &&
                (x.P - last.P).Length <= ChampionTracker.MaxRunSpeed * (x.T - last.T) + 450)
                runs[^1].Add(x);
            else runs.Add(new List<(double T, Point P)> { x });
        }
        kept = runs.Where(r => r.Count >= 6 && r[^1].T - r[0].T >= 2).SelectMany(r => r).ToList();
        if (kept.Count == 0) return kept;

        var smooth = new List<(double T, Point P)>(kept.Count);
        for (int i = 0; i < kept.Count; i++)
        {
            double x = 0, y = 0, w = 0;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(kept.Count - 1, i + 2); j++)
            {
                if (Math.Abs(kept[j].T - kept[i].T) > 1.2) continue;
                if ((kept[j].P - kept[i].P).Length > 800) continue; // don't blend across a recall or flash
                double k = j == i ? 2 : 1;
                x += kept[j].P.X * k; y += kept[j].P.Y * k; w += k;
            }
            smooth.Add((kept[i].T, new Point(x / w, y / w)));
        }
        return smooth;
    }

    void DrawPaths(DrawingContext dc, GameTimeline g, double t, double r)
    {
        var tracks = R.Tracks;
        if (tracks == null) return;
        double from = PathWindow > 0 ? t - PathWindow : 0;
        var who = SelectedId is int sid ? g.Participants.Where(p => p.Id == sid).ToList() : g.Participants;
        if (PathMode == BoardPathMode.Heatmap) { DrawHeat(dc, g, tracks, who, from, t); return; }

        foreach (var p in who)
        {
            var samples = CleanPath(tracks.Samples(p.Id, from, t));
            var c = SelectedId != null ? Color.FromRgb(255, 214, 10) : p.Team == 100 ? BlueTeam : RedTeam;
            double width = Math.Max(2, r * (SelectedId != null ? 0.2 : 0.13));
            for (int i = 1; i < samples.Count; i++)
            {
                var (ta, pa) = samples[i - 1];
                var (tb, pb) = samples[i];
                double dt = tb - ta;
                // A break in the line for gaps, recalls and deaths rather than a line across the map.
                if (dt > 5 || (pb - pa).Length > ChampionTracker.MaxRunSpeed * dt + 450) continue;
                double age = (t - tb) / Math.Max(1, t - from);
                byte a = (byte)(60 + 170 * (1 - Math.Clamp(age, 0, 1)));
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    WorldToView(pa), WorldToView(pb));
            }
        }
    }

    void DrawHeat(DrawingContext dc, GameTimeline g, GameTracks tracks, List<GameParticipant> who, double from, double t)
    {
        // Rebuilt at most twice a second of game time (and when the selection or window changes).
        string key = $"{SelectedId}|{PathWindow}|{Math.Floor(t * 2)}|{R.Version / 25}";
        if (heatCache?.Key != key)
        {
            var layers = new List<(int, float[])>();
            float max = 0;
            foreach (int team in SelectedId != null ? new[] { 0 } : new[] { 100, 200 })
            {
                var cells = new float[HeatN * HeatN];
                foreach (var p in who.Where(x => team == 0 || x.Team == team))
                {
                    var s = CleanPath(tracks.Samples(p.Id, from, t));
                    for (int i = 0; i < s.Count; i++)
                    {
                        double weight = i + 1 < s.Count ? Math.Min(1, s[i + 1].T - s[i].T) : 0.2; // seconds spent there
                        int cx = (int)((s[i].P.X - MinimapVision.WorldBounds.X) / MinimapVision.WorldBounds.Width * HeatN);
                        int cy = (int)((MinimapVision.WorldBounds.Bottom - s[i].P.Y) / MinimapVision.WorldBounds.Height * HeatN);
                        if ((uint)cx >= HeatN || (uint)cy >= HeatN) continue;
                        // Spread each sample over the neighbouring cells so the heat looks smooth.
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int x = cx + dx, y = cy + dy;
                                if ((uint)x >= HeatN || (uint)y >= HeatN) continue;
                                cells[y * HeatN + x] += (float)(weight * (dx == 0 && dy == 0 ? 1 : 0.35));
                            }
                    }
                }
                max = Math.Max(max, cells.Max());
                layers.Add((team, cells));
            }
            heatCache = (key, layers, max);
        }
        var (_, ls, m) = heatCache.Value;
        if (m <= 0) return;
        var o = WorldToView(new Point(MinimapVision.WorldBounds.X, MinimapVision.WorldBounds.Bottom));
        double cell = MinimapVision.WorldBounds.Width * Scale / HeatN, cellH = MinimapVision.WorldBounds.Height * Scale / HeatN;
        foreach (var (team, cells) in ls)
            for (int y = 0; y < HeatN; y++)
                for (int x = 0; x < HeatN; x++)
                {
                    float v = cells[y * HeatN + x];
                    if (v < m * 0.03) continue; // places only passed through briefly stay clear
                    double k = Math.Pow(v / m, 0.6);
                    // One champion: yellow to red as time spent grows. Both teams: each in its own colour.
                    var c = team == 100 ? BlueTeam : team == 200 ? RedTeam
                        : Color.FromRgb(255, (byte)(214 - 170 * k), (byte)(40 * (1 - k)));
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(25 + 185 * k), c.R, c.G, c.B)), null,
                        new Rect(o.X + x * cell, o.Y + y * cellH, cell + 0.5, cellH + 0.5));
                }
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Where each champion was drawn last frame (for clicking and dragging them).</summary>
    readonly List<(int Id, Point View, double Radius)> placed = new();
    readonly Projector projector = new();

    public double TokenRadius => Math.Clamp(W.Width * Scale * 0.021, 11, 30);

    public int? TokenAt(Point view)
    {
        for (int i = placed.Count - 1; i >= 0; i--)
            if ((placed[i].View - view).Length <= placed[i].Radius + 2) return placed[i].Id;
        return null;
    }

    public ChampState? StateOf(int id)
    {
        var p = R.Game?.P(id);
        return p == null ? null : R.StateAt(p, Now);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Back, null, new Rect(RenderSize));
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        try { RenderMap(dc); }
        finally { dc.Pop(); }
    }

    void RenderMap(DrawingContext dc)
    {
        RequestMap();
        double s = Scale;
        var o = Origin;
        var mapRect = new Rect(o.X, o.Y, W.Width * s, W.Height * s);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        if (mapArt != null)
        {
            if (CleanMap)
            {
                dc.PushOpacity(0.38);
                dc.DrawImage(mapGray!, mapRect);
                dc.Pop();
            }
            else dc.DrawImage(mapArt, mapRect);
        }
        else dc.DrawRectangle(null, new Pen(Brushes.DimGray, 1), mapRect);

        placed.Clear();
        var game = R.Game;
        if (game == null) return;
        double t = Now;
        double r = TokenRadius;

        if (structuresFor != game) MatchBuildings(game);
        for (int i = 0; i < Structures.Length; i++) DrawStructure(dc, Structures[i].Team, Structures[i].Kind, WorldToView(Structures[i].P), r, StructureUp(i, t));

        DrawInhibitorTimers(dc, t, r);
        DrawEvents(dc, game, t, r);
        if (ShowObjectives) DrawObjectives(dc, game, t, r);
        if (PathMode != BoardPathMode.Off) DrawPaths(dc, game, t, r);
        if (ShowCamera) DrawCamera(dc);

        // Champions as they were at this moment.
        if (!double.IsNaN(MovedAt) && Math.Abs(t - MovedAt) > 0.3) { Moved.Clear(); MovedAt = double.NaN; }
        var states = game.Participants.Select(p => R.StateAt(p, t)).ToList();
        if (Trails)
            foreach (var st in states)
                if (!st.Dead && st.Quality == PosQuality.Tracked && !Moved.ContainsKey(st.P.Id)) DrawTrail(dc, st, t, r);
        foreach (var st in states.OrderBy(x => x.Dead ? 0 : 1).ThenBy(x => x.P.Id == SelectedId ? 1 : 0))
        {
            RequestIcon(st.P.ChampionKey);
            var pos = Moved.TryGetValue(st.P.Id, out var m) ? m : st.Pos;
            var v = WorldToView(pos);
            DrawToken(dc, st, v, r, Moved.ContainsKey(st.P.Id));
            placed.Add((st.P.Id, v, r));
        }
        if (HoverId is int h && placed.FirstOrDefault(x => x.Id == h) is { Id: > 0 } hp && game.P(h) is { } hpart)
        {
            var st = states.First(x => x.P.Id == h);
            string extra = st.Dead ? $" · dead, back in {st.RespawnIn:0}s" : st.Quality == PosQuality.Estimated ? " · position estimated from the timeline" : st.Quality == PosQuality.LastSeen ? " · briefly off the minimap, shown where last seen" : "";
            Label(dc, $"{hpart.ChampionName} · lvl {st.Level}{extra}", new Point(hp.View.X, hp.View.Y + hp.Radius + 4), 12, true);
        }
    }

    void DrawStructure(DrawingContext dc, int team, Building kind, Point v, double r, bool up)
    {
        var c = team == 100 ? BlueTeam : RedTeam;
        double size = kind switch { Building.Tower => r * 0.42, Building.Inhibitor => r * 0.5, _ => r * 0.75 };
        var fill = new SolidColorBrush(up ? Color.FromArgb(230, c.R, c.G, c.B) : Color.FromArgb(70, 120, 120, 120));
        var pen = new Pen(new SolidColorBrush(up ? Color.FromArgb(230, 255, 255, 255) : Color.FromArgb(110, 200, 200, 200)), 1.2);
        switch (kind)
        {
            case Building.Tower:
                dc.DrawRoundedRectangle(fill, pen, new Rect(v.X - size, v.Y - size, size * 2, size * 2), size * 0.3, size * 0.3);
                break;
            case Building.Inhibitor:
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(v.X, v.Y - size), true, true);
                    ctx.PolyLineTo(new[] { new Point(v.X + size, v.Y), new Point(v.X, v.Y + size), new Point(v.X - size, v.Y) }, true, true);
                }
                dc.DrawGeometry(fill, pen, g);
                break;
            default:
                dc.DrawEllipse(fill, pen, v, size, size);
                break;
        }
        if (!up)
        {
            var x = new Pen(new SolidColorBrush(Color.FromArgb(160, 230, 230, 230)), 1.4);
            dc.DrawLine(x, new Point(v.X - size * 0.6, v.Y - size * 0.6), new Point(v.X + size * 0.6, v.Y + size * 0.6));
            dc.DrawLine(x, new Point(v.X + size * 0.6, v.Y - size * 0.6), new Point(v.X - size * 0.6, v.Y + size * 0.6));
        }
    }

    static string MonsterName(GameEvent e) => (e.MonsterType ?? "") switch
    {
        "BARON_NASHOR" => "Baron",
        "RIFTHERALD" => "Rift Herald",
        "HORDE" => "Void Grub",
        "ATAKHAN" => "Atakhan",
        "DRAGON" => e.MonsterSubType switch
        {
            "FIRE_DRAGON" => "Infernal Drake", "WATER_DRAGON" => "Ocean Drake", "EARTH_DRAGON" => "Mountain Drake",
            "AIR_DRAGON" => "Cloud Drake", "HEXTECH_DRAGON" => "Hextech Drake", "CHEMTECH_DRAGON" => "Chemtech Drake",
            "ELDER_DRAGON" => "Elder Dragon", _ => "Drake",
        },
        var other => other.Replace('_', ' '),
    };

    void DrawEvents(DrawingContext dc, GameTimeline g, double t, double r)
    {
        foreach (var e in g.Events)
        {
            if (e.Position is not Point wp || e.Time > t) continue;
            double age = t - e.Time;
            var v = WorldToView(wp);
            int killerTeam = g.P(e.KillerId)?.Team ?? (e.TeamId == 100 ? 200 : e.TeamId == 200 ? 100 : 0);
            var c = killerTeam == 100 ? BlueTeam : killerTeam == 200 ? RedTeam : Colors.Gold;
            switch (e.Type)
            {
                case "CHAMPION_KILL" when age < 12:
                {
                    double a = 1 - age / 12;
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * a), c.R, c.G, c.B)), Math.Max(2, r * 0.16)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    double k = r * 0.45;
                    dc.DrawLine(pen, new Point(v.X - k, v.Y - k), new Point(v.X + k, v.Y + k));
                    dc.DrawLine(pen, new Point(v.X + k, v.Y - k), new Point(v.X - k, v.Y + k));
                    if (age < 1.2) dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * (1 - age / 1.2)), 255, 255, 255)), 2), v, r * (0.6 + age), r * (0.6 + age));
                    break;
                }
                case "ELITE_MONSTER_KILL" when age < 25:
                {
                    dc.PushOpacity(1 - age / 25);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B)), new Pen(new SolidColorBrush(c), 2), v, r * 0.9, r * 0.9);
                    Label(dc, MonsterName(e), new Point(v.X, v.Y + r), 12, true, c);
                    dc.Pop();
                    break;
                }
                case "BUILDING_KILL" when age < 8:
                {
                    double a = 1 - age / 8;
                    dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(220 * a), c.R, c.G, c.B)), 2.5), v, r * (0.8 + age * 0.15), r * (0.8 + age * 0.15));
                    break;
                }
            }
        }
    }

    void DrawTrail(DrawingContext dc, ChampState st, double t, double r)
    {
        var pts = R.Trail(st.P.Id, t - 6, t);
        if (pts.Count < 2) return;
        var c = st.P.Team == 100 ? BlueTeam : RedTeam;
        for (int i = 1; i < pts.Count; i++)
        {
            byte a = (byte)(30 + 150.0 * i / pts.Count);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)), Math.Max(2, r * 0.18)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                WorldToView(pts[i - 1]), WorldToView(pts[i]));
        }
    }

    void DrawToken(DrawingContext dc, ChampState st, Point v, double r, bool moved)
    {
        var team = st.P.Team == 100 ? BlueTeam : RedTeam;
        double op = st.Dead ? 0.6 : moved ? 1 : st.Quality switch { PosQuality.Estimated => 0.72, PosQuality.LastSeen => 0.88, _ => 1 };
        dc.PushOpacity(op);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Point(v.X + 1, v.Y + 2), r + 2, r + 2);
        var clip = new EllipseGeometry(v, r, r);
        dc.PushClip(clip);
        if (icons.TryGetValue(st.P.ChampionKey, out var img) && img is { } pair)
        {
            // Data Dragon portraits have a thin frame; zoom in slightly so it doesn't show.
            double z = r * 1.12;
            dc.DrawImage(st.Dead ? pair.Gray : pair.Color, new Rect(v.X - z, v.Y - z, z * 2, z * 2));
        }
        else dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(40, 44, 52)), null, v, r, r);
        dc.Pop();

        var ring = new Pen(new SolidColorBrush(team), Math.Max(2, r * 0.15));
        if (st.Quality == PosQuality.Estimated && !moved) ring.DashStyle = new DashStyle(new[] { 1.6, 1.1 }, 0);
        dc.DrawEllipse(null, ring, v, r, r);
        if (st.P.Id == SelectedId) dc.DrawEllipse(null, new Pen(Brushes.White, 2), v, r + 4, r + 4);
        if (moved) dc.DrawEllipse(Brushes.Gold, new Pen(Brushes.Black, 1), new Point(v.X - r * 0.75, v.Y - r * 0.75), r * 0.2, r * 0.2);

        // Level badge
        var b = new Point(v.X + r * 0.74, v.Y + r * 0.74);
        double br = Math.Max(7, r * 0.4);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(16, 18, 24)), new Pen(new SolidColorBrush(team), 1.2), b, br, br);
        var lvl = Text(st.Level.ToString(CultureInfo.InvariantCulture), br * 1.15, Brushes.White, Bold);
        dc.DrawText(lvl, new Point(b.X - lvl.Width / 2, b.Y - lvl.Height / 2));
        dc.Pop();

        if (st.Dead && st.RespawnIn > 0)
        {
            var txt = Text(Math.Ceiling(st.RespawnIn).ToString(CultureInfo.InvariantCulture), r * 0.85, Brushes.White, Bold);
            var at = new Point(v.X - txt.Width / 2, v.Y - txt.Height / 2);
            dc.DrawText(Text(txt.Text, r * 0.85, Brushes.Black, Bold), new Point(at.X + 1.2, at.Y + 1.2));
            dc.DrawText(txt, at);
        }
    }

    /// <summary>The ground area the replay camera shows right now.</summary>
    void DrawCamera(DrawingContext dc)
    {
        var cam = S.Replay.Latest;
        if (cam == null) return;
        var hwnd = Native.FindLeagueWindow();
        var vp = Native.TryGetClientScreenRect(hwnd, out var lr) ? new Rect(0, 0, lr.Width, lr.Height) : new Rect(0, 0, 1920, 1080);
        projector.Update(cam, vp, S.Settings.Calibration);
        var corners = new[] { new Point(0, 0), new Point(vp.Width, 0), new Point(vp.Width, vp.Height), new Point(0, vp.Height) };
        var pts = new List<Point>();
        foreach (var c in corners)
        {
            if (!projector.TryUnproject(c, out var w)) return;
            pts.Add(WorldToView(w));
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true, true);
            ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 1.5), g);
    }

    /// <summary>Moves the replay camera so it looks at <paramref name="world"/>.</summary>
    public async void LookAt(Point world)
    {
        var cam = S.Replay.Latest;
        if (cam == null) return;
        var hwnd = Native.FindLeagueWindow();
        var vp = Native.TryGetClientScreenRect(hwnd, out var lr) ? new Rect(0, 0, lr.Width, lr.Height) : new Rect(0, 0, 1920, 1080);
        projector.Update(cam, vp, S.Settings.Calibration);
        if (!projector.TryLookAt(out var look)) return;
        var p = cam.Position;
        bool ok = await S.Replay.SetRenderAsync(new
        {
            cameraAttached = false,
            cameraPosition = new { x = p.X + world.X - look.X, y = p.Y, z = p.Z + world.Y - look.Y },
        });
        if (!ok) S.Say("The replay didn't accept the camera move.");
    }

    double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    FormattedText Text(string s, double size, Brush brush, Typeface? face = null) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Face, Math.Max(6, size), brush, Ppd);

    void Label(DrawingContext dc, string s, Point topCentre, double size, bool back, Color? color = null)
    {
        var ft = Text(s, size, new SolidColorBrush(color ?? Colors.White));
        var at = new Point(topCentre.X - ft.Width / 2, topCentre.Y);
        if (back) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 12, 14, 18)), null, new Rect(at.X - 5, at.Y - 1, ft.Width + 10, ft.Height + 2), 4, 4);
        dc.DrawText(ft, at);
    }
}
