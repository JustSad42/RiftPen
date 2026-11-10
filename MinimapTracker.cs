using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RiftPen;

/// <summary>
/// Reads champion positions off a minimap screenshot. Champion icons are found by their blue or red team ring,
/// which is the same for every skin. Who each icon is gets decided from its portrait (official art at first, then
/// the icon's real look learned from the minimap itself, so custom skins work) plus where that champion was a
/// moment ago.
/// </summary>
public static class MinimapVision
{
    /// <summary>Riot's documented Summoner's Rift bounds; the minimap shows exactly this square.</summary>
    public static readonly Rect WorldBounds = new(-120, -120, 14990, 15100);

    /// <summary>Radius of the coloured ring around a champion icon, as a fraction of the minimap side.</summary>
    public const double RingRadiusFrac = 0.0423;

    /// <summary>Part of the icon used to tell champions apart (inside the ring), in ring radii.</summary>
    const double InnerFrac = 0.78;

    public static Point ToWorld(Point px, int w, int h) => new(
        WorldBounds.X + px.X / w * WorldBounds.Width,
        WorldBounds.Y + (h - px.Y) / h * WorldBounds.Height);

    public static Point ToPixel(Point world, double w, double h) => new(
        (world.X - WorldBounds.X) / WorldBounds.Width * w,
        h - (world.Y - WorldBounds.Y) / WorldBounds.Height * h);

    /// <summary>1 = blue ring (0,153,224), 2 = red ring (232,61,61), 0 = neither.</summary>
    static byte RingTeam(byte b, byte g, byte r)
    {
        if (r < 60 && g >= 125 && g <= 185 && b >= 195) return 1;
        if (r >= 195 && g >= 25 && g <= 105 && b >= 25 && b <= 105) return 2;
        return 0;
    }

    public sealed record Ring(Point Center, int Team, double Score)
    {
        /// <summary>No other icon overlaps this one, so its portrait is fully visible.</summary>
        public bool Isolated { get; set; } = true;
    }

    /// <summary>Every champion icon ring on the minimap.</summary>
    public static List<Ring> FindRings(byte[] bgra, int w, int h, double minScore = 0.25)
    {
        double R = w * RingRadiusFrac;
        var team = new byte[w * h];
        for (int i = 0; i < w * h; i++) team[i] = RingTeam(bgra[4 * i], bgra[4 * i + 1], bgra[4 * i + 2]);

        // Sample the circle at a few radii around the expected one, so a pixel or two of error still counts.
        const int A = 48;
        double[] radii = { R - 1.5, R - 0.5, R + 0.5, R + 1.5 };
        int K = radii.Length;
        var ox = new int[A * K];
        var oy = new int[A * K];
        for (int a = 0; a < A; a++)
            for (int k = 0; k < K; k++)
            {
                double ang = 2 * Math.PI * a / A;
                ox[a * K + k] = (int)Math.Round(Math.Cos(ang) * radii[k]);
                oy[a * K + k] = (int)Math.Round(Math.Sin(ang) * radii[k]);
            }

        // Quick reject: a ring needs plenty of ring-coloured pixels in the box around its centre (summed-area table).
        var sat = new int[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            int row = 0;
            for (int x = 0; x < w; x++)
            {
                if (team[y * w + x] != 0) row++;
                sat[(y + 1) * (w + 1) + x + 1] = sat[y * (w + 1) + x + 1] + row;
            }
        }
        int reach = (int)Math.Ceiling(R + 2);
        int minPixels = (int)(A * 0.2);
        int BoxCount(int x, int y)
        {
            int x0 = Math.Max(0, x - reach), y0 = Math.Max(0, y - reach), x1 = Math.Min(w, x + reach + 1), y1 = Math.Min(h, y + reach + 1);
            return sat[y1 * (w + 1) + x1] - sat[y0 * (w + 1) + x1] - sat[y1 * (w + 1) + x0] + sat[y0 * (w + 1) + x0];
        }

        var blue = new float[w * h];
        var red = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                if (BoxCount(x, y) < minPixels) continue;
                int inb = 0, cb = 0, cr = 0;
                for (int a = 0; a < A; a++)
                {
                    bool any = false, hb = false, hr = false;
                    for (int k = 0; k < K; k++)
                    {
                        int px = x + ox[a * K + k], py = y + oy[a * K + k];
                        if ((uint)px >= (uint)w || (uint)py >= (uint)h) continue;
                        any = true;
                        byte t = team[py * w + px];
                        if (t == 1) hb = true; else if (t == 2) hr = true;
                    }
                    if (any) { inb++; if (hb) cb++; if (hr) cr++; }
                }
                // Icons at the very edge (the fountains) are partly cut off; judge them on what is visible.
                if (inb < A * 0.3) continue;
                if (cb >= A * 0.2) blue[y * w + x] = cb / (float)inb;
                if (cr >= A * 0.2) red[y * w + x] = cr / (float)inb;
            }
        });

        var rings = new List<Ring>();
        foreach (var (map, teamId) in new[] { (blue, 100), (red, 200) })
        {
            var peaks = new List<(int X, int Y, float S)>();
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    float s = map[y * w + x];
                    if (s < minScore) continue;
                    bool max = true;
                    for (int dy = -1; dy <= 1 && max; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            if ((dx != 0 || dy != 0) && map[(y + dy) * w + x + dx] > s) { max = false; break; }
                    if (max) peaks.Add((x, y, s));
                }
            var kept = new List<(int X, int Y, float S)>();
            foreach (var p in peaks.OrderByDescending(p => p.S))
                if (kept.All(k => (k.X - p.X) * (k.X - p.X) + (k.Y - p.Y) * (k.Y - p.Y) > R * R * 0.25))
                    kept.Add(p);
            foreach (var p in kept)
            {
                // Centre of the score plateau, for sub-pixel accuracy.
                double sx = 0, sy = 0, sw = 0;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int x = p.X + dx, y = p.Y + dy;
                        if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                        double v = map[y * w + x];
                        if (v < p.S * 0.85) continue;
                        sx += x * v; sy += y * v; sw += v;
                    }
                rings.Add(new Ring(new Point(sx / sw, sy / sw), teamId, p.S));
            }
        }
        foreach (var r in rings)
            r.Isolated = rings.All(o => ReferenceEquals(o, r) || (o.Center - r.Center).Length > 2 * R + 1);
        return rings;
    }

    // ---- Telling champions apart ------------------------------------------------------------------------------

    const int G = 16;
    static readonly (double U, double V)[] Grid = BuildGrid();

    /// <summary>Length of a portrait description (changes only if the sampling grid does).</summary>
    public static int DescriptionLength => Grid.Length * 3;

    static (double, double)[] BuildGrid()
    {
        var list = new List<(double, double)>();
        for (int j = 0; j < G; j++)
            for (int i = 0; i < G; i++)
            {
                double u = (-1 + (2 * i + 1.0) / G) * InnerFrac, v = (-1 + (2 * j + 1.0) / G) * InnerFrac;
                if (u * u + v * v <= InnerFrac * InnerFrac) list.Add((u, v));
            }
        return list.ToArray();
    }

    /// <summary>The portrait inside a ring as a normalised colour vector (compare two with <see cref="Similarity"/>).</summary>
    public static float[] Describe(byte[] bgra, int w, int h, Point centre, double radius)
    {
        var v = new float[Grid.Length * 3];
        for (int n = 0; n < Grid.Length; n++)
            Sample(bgra, w, h, centre.X + Grid[n].U * radius, centre.Y + Grid[n].V * radius, v, n * 3);
        return Normalise(v);
    }

    /// <summary>The same description made from square champion art, whose half-width corresponds to <paramref name="scale"/> ring radii.</summary>
    public static float[] DescribeArt(byte[] bgra, int w, int h, double scale)
    {
        var v = new float[Grid.Length * 3];
        double half = w / 2.0;
        for (int n = 0; n < Grid.Length; n++)
            Sample(bgra, w, h, half - 0.5 + Grid[n].U / scale * half, half - 0.5 + Grid[n].V / scale * half, v, n * 3);
        return Normalise(v);
    }

    /// <summary>Correlation between two descriptions: 1 = identical, around 0 = unrelated.</summary>
    public static double Similarity(float[] a, float[] b)
    {
        double d = 0;
        for (int i = 0; i < a.Length; i++) d += a[i] * b[i];
        return d;
    }

    public static float[] Blend(float[] old, float[] add, double weight)
    {
        var v = new float[old.Length];
        for (int i = 0; i < v.Length; i++) v[i] = (float)(old[i] * (1 - weight) + add[i] * weight);
        return Normalise(v);
    }

    static void Sample(byte[] bgra, int w, int h, double x, double y, float[] dst, int at)
    {
        x = Math.Clamp(x, 0, w - 1.001);
        y = Math.Clamp(y, 0, h - 1.001);
        int x0 = (int)x, y0 = (int)y;
        double fx = x - x0, fy = y - y0;
        for (int c = 0; c < 3; c++)
        {
            double a = bgra[(y0 * w + x0) * 4 + 2 - c], b = bgra[(y0 * w + x0 + 1) * 4 + 2 - c];
            double d = bgra[((y0 + 1) * w + x0) * 4 + 2 - c], e = bgra[((y0 + 1) * w + x0 + 1) * 4 + 2 - c];
            dst[at + c] = (float)((a * (1 - fx) + b * fx) * (1 - fy) + (d * (1 - fx) + e * fx) * fy);
        }
    }

    static float[] Normalise(float[] v)
    {
        double mean = 0;
        foreach (var x in v) mean += x;
        mean /= v.Length;
        double norm = 0;
        for (int i = 0; i < v.Length; i++) { v[i] = (float)(v[i] - mean); norm += v[i] * v[i]; }
        norm = Math.Sqrt(norm);
        if (norm > 1e-6) for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return v;
    }

    /// <summary>BGRA pixels of an image file (Data Dragon art).</summary>
    public static (byte[] Pixels, int W, int H) LoadPixels(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }
}

/// <summary>
/// Keeps track of who is who on the minimap from one screenshot to the next.
/// </summary>
public sealed class ChampionTracker
{
    sealed class Champ
    {
        public required GameParticipant P;
        public readonly List<float[]> Art = new();
        public float[]? Learned;
        public int LearnCount;
        /// <summary>Looks learned in earlier replays (one per skin seen), from <see cref="IconLibrary"/>.</summary>
        public readonly List<float[]> Known = new();
        /// <summary>A saved look clearly matched this game's icon, so it counts as learned already.</summary>
        public bool KnownMatched;
        public Point? LastWorld;
        public double LastTime = double.NaN;
        public double LastReal = double.NaN;
    }

    /// <summary>A champion seen on the minimap. Confident = the portrait clearly matched.</summary>
    public sealed record Sighting(int ParticipantId, string ChampionKey, Point World, Point Pixel, double Appearance, bool Confident)
    {
        /// <summary>Not visible itself; assumed to be under the pile of icons it disappeared into.</summary>
        public bool Hidden { get; init; }
    }

    readonly List<Champ> champs = new();
    readonly GameTimeline game;

    /// <summary>Learned icons need this many clear sightings before they replace the official art.</summary>
    const int LearnedAfter = 3;

    ChampionTracker(GameTimeline game) { this.game = game; }

    public static async Task<ChampionTracker> CreateAsync(GameTimeline game)
    {
        var t = new ChampionTracker(game);
        foreach (var p in game.Participants)
        {
            var c = new Champ { P = p };
            try
            {
                var path = await DataDragon.ChampionIconAsync(p.ChampionKey);
                if (path != null)
                {
                    var (px, w, h) = MinimapVision.LoadPixels(path);
                    foreach (double s in new[] { 0.9, 1.0, 1.1 })
                        c.Art.Add(MinimapVision.DescribeArt(px, w, h, s));
                }
            }
            catch { /* no art: identify by position and elimination only */ }
            c.Known.AddRange(IconLibrary.Shared.LooksFor(p.ChampionKey));
            t.champs.Add(c);
        }
        return t;
    }

    /// <summary>How many champions' real minimap icons are known (learned this game, or recognised from an earlier one).</summary>
    public int LearnedCount => champs.Count(c => c.LearnCount >= LearnedAfter || c.KnownMatched);

    /// <summary>Champions that started this game with saved looks from earlier replays.</summary>
    public int PreloadedCount => champs.Count(c => c.Known.Count > 0);

    static double BestOf(List<float[]> looks, float[] desc) => looks.Count == 0 ? -1 : looks.Max(a => MinimapVision.Similarity(a, desc));

    double Appearance(Champ c, float[] desc)
    {
        // Saved looks were each confirmed several times before, so they're trusted like a look learned this game.
        double known = BestOf(c.Known, desc);
        if (c.Learned != null && c.LearnCount >= LearnedAfter) return Math.Max(MinimapVision.Similarity(c.Learned, desc), known);
        double best = Math.Max(Math.Max(BestOf(c.Art, desc), 0), known);
        // A partly learned icon can already vouch for a match, but not yet against one.
        if (c.Learned != null) best = Math.Max(best, MinimapVision.Similarity(c.Learned, desc));
        return best;
    }

    /// <summary>
    /// Saves a champion's learned look for later replays, unless it looks more like another champion in this game
    /// (a sign the identification went wrong, which would then repeat in every later game).
    /// </summary>
    void Remember(Champ c)
    {
        if (c.Learned == null) return;
        foreach (var o in champs)
        {
            if (o == c) continue;
            if (BestOf(o.Art, c.Learned) >= 0.75) return;
            if (o.Learned != null && o.LearnCount >= LearnedAfter && MinimapVision.Similarity(o.Learned, c.Learned) >= 0.85) return;
        }
        IconLibrary.Shared.Remember(c.P.ChampionKey, c.Learned);
    }

    /// <summary>Where the per-minute timeline says the champion roughly is.</summary>
    Point? TimelineGuess(int id, double time)
    {
        GameFrame? a = null, b = null;
        foreach (var f in game.Frames)
        {
            if (!f.Participants.TryGetValue(id, out var pf) || pf.Position == null) continue;
            if (f.Time <= time) a = f;
            else { b = f; break; }
        }
        var pa = a?.Participants[id].Position;
        var pb = b?.Participants[id].Position;
        if (pa is Point A && pb is Point B)
        {
            double k = (time - a!.Time) / Math.Max(1, b!.Time - a.Time);
            return new Point(A.X + (B.X - A.X) * k, A.Y + (B.Y - A.Y) * k);
        }
        return pa ?? pb;
    }

    /// <summary>Forget recent positions (after a seek, nothing about "a moment ago" holds).</summary>
    public void ResetMotion()
    {
        foreach (var c in champs) { c.LastWorld = null; c.LastTime = double.NaN; c.LastReal = double.NaN; }
    }

    /// <summary>
    /// Finds every visible champion in a minimap capture taken at replay time <paramref name="time"/>.
    /// <paramref name="isDead"/> says who can't be on the map right now (from the live game data).
    /// </summary>
    public List<Sighting> Process(byte[] bgra, int w, int h, double time, Func<string, bool>? isDead, out List<MinimapVision.Ring> rings)
    {
        rings = MinimapVision.FindRings(bgra, w, h);
        double R = w * MinimapVision.RingRadiusFrac;
        var result = new List<Sighting>();

        var seenIds = new HashSet<int>();
        foreach (int team in new[] { 100, 200 })
        {
            var cs = champs.Where(c => c.P.Team == team).ToList();
            var ks = rings.Where(r => r.Team == team).OrderByDescending(r => r.Score).Take(9).ToList();
            if (ks.Count == 0) continue;

            var desc = ks.Select(k => MinimapVision.Describe(bgra, w, h, k.Center, R)).ToList();
            var world = ks.Select(k => MinimapVision.ToWorld(k.Center, w, h)).ToList();
            var app = new double[cs.Count, ks.Count];
            var pen = new double[cs.Count, ks.Count];
            for (int i = 0; i < cs.Count; i++)
            {
                var c = cs[i];
                // Where this champion should be: where we last saw it, or else the timeline's rough guess.
                Point? expected; double allow, weight;
                double dt = Math.Abs(time - c.LastTime);
                if (c.LastWorld != null && dt <= 8) { expected = c.LastWorld; allow = 500 + 550 * dt; weight = 1 / 1500.0; }
                else { expected = TimelineGuess(c.P.Id, time); allow = 2500; weight = 1 / 5000.0; }
                // The live data says dead: unlikely on the map, but it can lag a second behind after a seek.
                double deadPenalty = isDead?.Invoke(c.P.ChampionKey) == true ? 0.5 : 0;
                for (int k = 0; k < ks.Count; k++)
                {
                    app[i, k] = Appearance(c, desc[k]);
                    pen[i, k] = (expected is Point e ? Math.Max(0, (world[k] - e).Length - allow) * weight : 0)
                              + deadPenalty
                              + Math.Max(0, 0.4 - ks[k].Score) * 1.5; // faint ring (mostly covered by other icons)
                }
            }

            // Best overall assignment (each icon to at most one champion; a champion may be unseen).
            const double Unseen = -0.5;
            int nC = cs.Count, nK = ks.Count;
            var cur = new int[nC];
            var best = new int[nC];
            double bestSum = double.NegativeInfinity;
            var used = new bool[nK];
            void Search(int i, double sum)
            {
                if (i == nC)
                {
                    if (sum > bestSum) { bestSum = sum; Array.Copy(cur, best, nC); }
                    return;
                }
                cur[i] = -1;
                Search(i + 1, sum + Unseen);
                for (int k = 0; k < nK; k++)
                {
                    if (used[k]) continue;
                    used[k] = true;
                    cur[i] = k;
                    Search(i + 1, sum + app[i, k] - pen[i, k]);
                    used[k] = false;
                }
            }
            Search(0, 0);

            // Which matches are clear enough to learn the icon from.
            var clear = new bool[nC];
            for (int i = 0; i < nC; i++)
            {
                int k = best[i];
                if (k < 0) continue;
                double other = Enumerable.Range(0, nC).Where(j => j != i).Select(j => app[j, k]).DefaultIfEmpty(-1).Max();
                clear[i] = app[i, k] >= 0.72 && app[i, k] - other >= 0.15 && pen[i, k] < 0.1;
            }
            // Elimination: every icon on this team is accounted for and all the others are clear, so the last one is too
            // (this is how a champion with a custom skin gets learned).
            int assigned = best.Count(k => k >= 0);
            int alive = cs.Count(c => isDead?.Invoke(c.P.ChampionKey) != true);
            if (assigned == nK && nK == alive)
                for (int i = 0; i < nC; i++)
                    if (best[i] >= 0 && !clear[i] && pen[i, best[i]] < 0.1 &&
                        Enumerable.Range(0, nC).All(j => j == i || best[j] < 0 || clear[j]))
                        clear[i] = true;

            for (int i = 0; i < nC; i++)
            {
                int k = best[i];
                if (k < 0) continue;
                var c = cs[i];
                if (clear[i] && ks[k].Isolated)
                {
                    c.Learned = c.Learned == null ? (float[])desc[k].Clone() : MinimapVision.Blend(c.Learned, desc[k], 0.2);
                    c.LearnCount++;
                    if (!c.KnownMatched && BestOf(c.Known, desc[k]) >= 0.85) c.KnownMatched = true;
                    // Keep it for later replays once it's settled, and refresh it now and then.
                    if (c.LearnCount == LearnedAfter || c.LearnCount % 25 == 0) Remember(c);
                }
                c.LastWorld = world[k];
                c.LastTime = time;
                c.LastReal = time;
                seenIds.Add(c.P.Id);
                result.Add(new Sighting(c.P.Id, c.P.ChampionKey, world[k], ks[k].Center, app[i, k], clear[i] || app[i, k] >= 0.6));
            }
        }

        // Champions buried under a pile of icons: if one vanished right where a group of icons is, it's in that group.
        foreach (var c in champs)
        {
            if (seenIds.Contains(c.P.Id) || c.LastWorld is not Point last || isDead?.Invoke(c.P.ChampionKey) == true) continue;
            if (Math.Abs(time - c.LastTime) > 3 || Math.Abs(time - c.LastReal) > 20) continue;
            var lastPx = MinimapVision.ToPixel(last, w, h);
            var pile = rings.OrderBy(r => (r.Center - lastPx).Length).FirstOrDefault();
            if (pile == null || (pile.Center - lastPx).Length > 2.2 * R) continue;
            var pos = MinimapVision.ToWorld(pile.Center, w, h);
            c.LastWorld = pos;
            c.LastTime = time;
            result.Add(new Sighting(c.P.Id, c.P.ChampionKey, pos, pile.Center, 0, false) { Hidden = true });
        }
        return result;
    }
}

/// <summary>
/// Minimap icons learned in earlier replays, so a new replay recognises champions from the first second.
/// Each champion keeps up to four looks (one per skin it has been seen in); a look is only saved after it was
/// confirmed several times in one game.
/// </summary>
public sealed class IconLibrary
{
    public sealed class Look
    {
        public required float[] V;
        public int Uses;
        public DateTime LastUsed;
    }

    const int MaxLooks = 4;
    const int Magic = 0x31495052; // "RPI1"
    readonly Dictionary<string, List<Look>> byChampion = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();

    public static IconLibrary Shared { get; } = Load();
    public bool Dirty { get; private set; }

    static string FilePath => Path.Combine(AppSettings.Folder, "learned-icons.bin");

    public int ChampionCount { get { lock (gate) return byChampion.Count; } }

    public List<float[]> LooksFor(string championKey)
    {
        lock (gate)
            return byChampion.TryGetValue(championKey, out var l) ? l.Select(x => (float[])x.V.Clone()).ToList() : new List<float[]>();
    }

    /// <summary>Adds a champion's look, or refines the saved one it matches (same skin).</summary>
    public void Remember(string championKey, float[] look)
    {
        if (look.Length != MinimapVision.DescriptionLength) return;
        lock (gate)
        {
            if (!byChampion.TryGetValue(championKey, out var list)) byChampion[championKey] = list = new List<Look>();
            var same = list.Select(l => (Look: l, Sim: MinimapVision.Similarity(l.V, look))).OrderByDescending(x => x.Sim).FirstOrDefault();
            if (same.Look != null && same.Sim >= 0.9)
            {
                same.Look.V = MinimapVision.Blend(same.Look.V, look, 0.3);
                same.Look.Uses++;
                same.Look.LastUsed = DateTime.Now;
            }
            else
            {
                if (list.Count >= MaxLooks) list.Remove(list.OrderBy(l => l.Uses).ThenBy(l => l.LastUsed).First());
                list.Add(new Look { V = (float[])look.Clone(), Uses = 1, LastUsed = DateTime.Now });
            }
            Dirty = true;
        }
    }

    public void Clear()
    {
        lock (gate) { byChampion.Clear(); Dirty = true; }
        Save();
    }

    static IconLibrary Load()
    {
        var lib = new IconLibrary();
        try
        {
            if (!File.Exists(FilePath)) return lib;
            using var r = new BinaryReader(File.OpenRead(FilePath));
            if (r.ReadInt32() != Magic) return lib;
            int champions = r.ReadInt32();
            for (int i = 0; i < champions; i++)
            {
                string key = r.ReadString();
                int n = r.ReadInt32();
                var list = new List<Look>();
                for (int k = 0; k < n; k++)
                {
                    int uses = r.ReadInt32();
                    var last = new DateTime(r.ReadInt64());
                    int len = r.ReadInt32();
                    var v = new float[len];
                    for (int j = 0; j < len; j++) v[j] = r.ReadSingle();
                    // Saved by an older version with a different sampling grid: no longer comparable.
                    if (len == MinimapVision.DescriptionLength) list.Add(new Look { V = v, Uses = uses, LastUsed = last });
                }
                if (list.Count > 0) lib.byChampion[key] = list;
            }
        }
        catch (Exception ex) { AppSettings.LogError(ex); }
        return lib;
    }

    public void Save()
    {
        try
        {
            lock (gate)
            {
                if (!Dirty) return;
                Directory.CreateDirectory(AppSettings.Folder);
                var tmp = FilePath + ".tmp";
                using (var w = new BinaryWriter(File.Create(tmp)))
                {
                    w.Write(Magic);
                    w.Write(byChampion.Count);
                    foreach (var (key, list) in byChampion)
                    {
                        w.Write(key);
                        w.Write(list.Count);
                        foreach (var l in list)
                        {
                            w.Write(l.Uses);
                            w.Write(l.LastUsed.Ticks);
                            w.Write(l.V.Length);
                            foreach (var f in l.V) w.Write(f);
                        }
                    }
                }
                File.Move(tmp, FilePath, overwrite: true);
                Dirty = false;
            }
        }
        catch (Exception ex) { AppSettings.LogError(ex); }
    }
}

/// <summary>
/// Champion positions recorded from the minimap while a replay plays, kept per game on disk so a part of the game
/// you've watched once can be scrubbed on the Rift Board later.
/// </summary>
public sealed class GameTracks
{
    public readonly record struct Sample(double T, float X, float Y);

    readonly Dictionary<int, List<Sample>> byId = new();
    readonly object gate = new();
    public string Key { get; }
    public bool Dirty { get; private set; }

    public GameTracks(string key) { Key = key; }

    static string FilePath(string key) => Path.Combine(GameLoader.CacheFolder, key + ".tracks.bin");

    public void Add(int id, double t, Point p)
    {
        lock (gate)
        {
            if (!byId.TryGetValue(id, out var list)) byId[id] = list = new List<Sample>();
            // Re-watching a moment replaces what was recorded there before.
            int i = Lower(list, t - 0.15);
            while (i < list.Count && list[i].T <= t + 0.15) list.RemoveAt(i);
            list.Insert(i, new Sample(t, (float)p.X, (float)p.Y));
            Dirty = true;
        }
    }

    static int Lower(List<Sample> list, double t)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (list[mid].T < t) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>Position at time <paramref name="t"/> if it was recorded (between two close samples, or right next to one).</summary>
    public bool TryGet(int id, double t, out Point p, double maxGap = 3, double maxNear = 0.75)
    {
        p = default;
        lock (gate)
        {
            if (!byId.TryGetValue(id, out var list) || list.Count == 0) return false;
            int i = Lower(list, t);
            Sample? after = i < list.Count ? list[i] : null;
            Sample? before = i > 0 ? list[i - 1] : null;
            if (before is Sample a && after is Sample b && b.T - a.T <= maxGap)
            {
                double k = b.T - a.T < 1e-6 ? 0 : (t - a.T) / (b.T - a.T);
                p = new Point(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);
                return true;
            }
            Sample? near = null;
            if (before is Sample x && t - x.T <= maxNear) near = x;
            if (after is Sample y && y.T - t <= maxNear && (near == null || y.T - t < t - near.Value.T)) near = y;
            if (near is Sample n) { p = new Point(n.X, n.Y); return true; }
            return false;
        }
    }

    /// <summary>Stretches of the game where positions were recorded (for drawing on the timeline).</summary>
    public List<(double Start, double End)> Coverage(double gap = 3)
    {
        lock (gate)
        {
            var times = byId.Values.SelectMany(l => l.Select(s => s.T)).OrderBy(t => t).ToList();
            var spans = new List<(double, double)>();
            foreach (var t in times)
            {
                if (spans.Count > 0 && t - spans[^1].Item2 <= gap) spans[^1] = (spans[^1].Item1, t);
                else spans.Add((t, t));
            }
            return spans;
        }
    }

    public int SampleCount { get { lock (gate) return byId.Values.Sum(l => l.Count); } }

    public static GameTracks Load(string key)
    {
        var t = new GameTracks(key);
        try
        {
            var path = FilePath(key);
            if (!File.Exists(path)) return t;
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != 0x31545052) return t; // "RPT1"
            int ids = r.ReadInt32();
            for (int i = 0; i < ids; i++)
            {
                int id = r.ReadInt32(), n = r.ReadInt32();
                var list = new List<Sample>(n);
                for (int k = 0; k < n; k++) list.Add(new Sample(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                t.byId[id] = list;
            }
        }
        catch (Exception ex) { AppSettings.LogError(ex); }
        return t;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(GameLoader.CacheFolder);
            var tmp = FilePath(Key) + ".tmp";
            lock (gate)
            {
                using (var w = new BinaryWriter(File.Create(tmp)))
                {
                    w.Write(0x31545052);
                    w.Write(byId.Count);
                    foreach (var (id, list) in byId)
                    {
                        w.Write(id);
                        w.Write(list.Count);
                        foreach (var s in list) { w.Write((float)s.T); w.Write(s.X); w.Write(s.Y); }
                    }
                }
                Dirty = false;
            }
            File.Move(tmp, FilePath(Key), overwrite: true);
        }
        catch (Exception ex) { AppSettings.LogError(ex); }
    }
}
