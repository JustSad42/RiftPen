using System.Windows;

namespace RiftPen;

/// <summary>A jungle camp on Summoner's Rift (world X, Z) and when it first spawns.</summary>
public sealed record JungleCamp(string Name, string Short, int Side, Point Pos, double FirstSpawn, double Radius);

/// <summary>A ward from Riot's timeline, placed where its owner was standing when they put it down.</summary>
public sealed class WardInfo
{
    public required int Creator { get; init; }
    public required int Team { get; init; }
    /// <summary>YELLOW_TRINKET, SIGHT_WARD, CONTROL_WARD or BLUE_TRINKET.</summary>
    public required string Type { get; init; }
    public required double Placed { get; init; }
    public required Point Pos { get; init; }
    /// <summary>The owner's position at that moment came from the minimap (otherwise it's only estimated).</summary>
    public required bool PosKnown { get; init; }
    /// <summary>When it runs out on its own (infinity for control and farsight wards).</summary>
    public double Expires { get; set; } = double.PositiveInfinity;
    public double? KilledAt { get; set; }
    public int KilledBy { get; set; }
    /// <summary>When it disappears for any reason: cleared, ran out, or replaced by a newer ward over the limit.</summary>
    public double Ends => Math.Min(Expires, KilledAt ?? double.PositiveInfinity);

    public bool AliveAt(double t) => Placed <= t && t < Ends;
    public bool IsControl => Type == "CONTROL_WARD";
    public bool IsFarsight => Type == "BLUE_TRINKET";
    /// <summary>Vision radius in game units.</summary>
    public double Vision => IsFarsight ? 500 : 900;
}

/// <summary>One camp the jungler took, in order, with when they got there.</summary>
public sealed record CampVisit(int Step, JungleCamp Camp, double Arrived, double Left);

/// <summary>How many plates each outer turret has lost, and which towers are down, at one moment.</summary>
public sealed record LaneState(string Lane, int BluePlatesTaken, int RedPlatesTaken, bool[] BlueTowersUp, bool[] RedTowersUp);

/// <summary>
/// Things worked out from Riot's timeline plus the recorded minimap positions: wards (with estimated spots),
/// turret plates and lane state, and each jungler's first clear. Cached per game and recording version.
/// </summary>
public static class RiftInsights
{
    // ---------------------------------------------------------------- map data

    /// <summary>Camp centres on today's Summoner's Rift (world X, Z). Side 100 = blue jungle, 200 = red, 0 = river.</summary>
    public static readonly JungleCamp[] Camps =
    {
        new("Blue Sentinel", "Blue", 100, new(3821, 8101), 90, 650),
        new("Gromp", "Gromp", 100, new(2288, 8448), 90, 600),
        new("Murk Wolves", "Wolves", 100, new(3783, 6495), 90, 600),
        new("Raptors", "Raptors", 100, new(6983, 5487), 90, 600),
        new("Red Brambleback", "Red", 100, new(7765, 4020), 90, 650),
        new("Krugs", "Krugs", 100, new(8394, 2641), 90, 600),
        new("Blue Sentinel", "Blue", 200, new(11032, 6990), 90, 650),
        new("Gromp", "Gromp", 200, new(12703, 6443), 90, 600),
        new("Murk Wolves", "Wolves", 200, new(11059, 8419), 90, 600),
        new("Raptors", "Raptors", 200, new(7852, 9324), 90, 600),
        new("Red Brambleback", "Red", 200, new(7101, 10900), 90, 650),
        new("Krugs", "Krugs", 200, new(6476, 12142), 90, 600),
        new("Rift Scuttler", "Scuttle", 0, new(4400, 9600), 210, 750),
        new("Rift Scuttler", "Scuttle", 0, new(10500, 5170), 210, 750),
    };

    /// <summary>Outer, inner and inhibitor towers per lane (index into <see cref="Towers"/> order: outer, inner, inhib).</summary>
    public static readonly (string Lane, Point[] Blue, Point[] Red)[] LaneTowers =
    {
        ("Top", new Point[] { new(981, 10441), new(1512, 6699), new(1169, 4287) }, new Point[] { new(4318, 13875), new(7943, 13411), new(10481, 13650) }),
        ("Mid", new Point[] { new(5846, 6396), new(5048, 4812), new(3651, 3696) }, new Point[] { new(8955, 8510), new(9767, 10113), new(11134, 11207) }),
        ("Bot", new Point[] { new(10504, 1029), new(6919, 1483), new(4281, 1253) }, new Point[] { new(13866, 4505), new(13327, 8226), new(13624, 10572) }),
    };

    /// <summary>Outer turrets drop their plates (and the extra armour) at 14:00.</summary>
    public const double PlatesFall = 840;
    public const int PlatesPerTurret = 5;

    // ---------------------------------------------------------------- cache

    static GameTimeline? cachedFor;
    static int cachedVersion = -1;
    static List<WardInfo> wards = new();
    static readonly Dictionary<int, List<CampVisit>> routes = new();

    static void Ensure(GameTimeline g)
    {
        var R = RiftSession.I;
        // The recording keeps growing while the replay plays; rebuild now and then, not every frame.
        int v = R.Version / 20;
        if (cachedFor == g && cachedVersion == v) return;
        cachedFor = g;
        cachedVersion = v;
        wards = BuildWards(g, (p, t) => R.StateAt(p, t));
        routes.Clear();
    }

    public static void Invalidate() => cachedFor = null;

    // ---------------------------------------------------------------- wards

    public static IReadOnlyList<WardInfo> Wards(GameTimeline g)
    {
        Ensure(g);
        return wards;
    }

    static readonly HashSet<string> WardTypes = new() { "YELLOW_TRINKET", "SIGHT_WARD", "CONTROL_WARD", "BLUE_TRINKET" };

    /// <summary>
    /// Wards from the timeline. Riot gives who placed a ward and when, not where, so each one sits where its owner
    /// was at that moment (wards land within ~600 units of the champion). Lifetimes follow today's rules: stealth
    /// trinket 90–120 s by level, support-item wards 150 s, control and farsight wards until cleared; a player has at
    /// most 3 stealth wards and 1 control ward out, and placing another removes the oldest.
    /// </summary>
    public static List<WardInfo> BuildWards(GameTimeline g, Func<GameParticipant, double, ChampState> stateAt)
    {
        var list = new List<WardInfo>();
        foreach (var e in g.Events.Where(e => e.Type is "WARD_PLACED" or "WARD_KILL").OrderBy(e => e.Time))
        {
            if (e.Type == "WARD_PLACED")
            {
                if (e.WardType is not string type || !WardTypes.Contains(type) || g.P(e.CreatorId) is not { } owner) continue;
                var st = stateAt(owner, e.Time);
                var w = new WardInfo
                {
                    Creator = owner.Id, Team = owner.Team, Type = type, Placed = e.Time, Pos = st.Pos,
                    PosKnown = st.Quality != PosQuality.Estimated,
                };
                if (type == "YELLOW_TRINKET") w.Expires = e.Time + 90 + 30.0 * (Math.Clamp(st.Level, 1, 18) - 1) / 17;
                else if (type == "SIGHT_WARD") w.Expires = e.Time + 150;

                // Over the limit: the owner's oldest ward of that kind disappears.
                var mine = list.Where(x => x.Creator == owner.Id && x.AliveAt(e.Time) && (x.IsControl == w.IsControl) && !x.IsFarsight && !w.IsFarsight)
                               .OrderBy(x => x.Placed).ToList();
                int limit = w.IsControl ? 1 : 3;
                while (!w.IsFarsight && mine.Count >= limit)
                {
                    mine[0].Expires = Math.Min(mine[0].Expires, e.Time);
                    mine.RemoveAt(0);
                }
                list.Add(w);
            }
            else
            {
                // Cleared: the enemy ward of that kind closest to whoever cleared it (or the oldest one).
                if (g.P(e.KillerId) is not { } killer) continue;
                var near = stateAt(killer, e.Time);
                var candidates = list.Where(x => x.Team != killer.Team && x.AliveAt(e.Time) && (e.WardType == null || e.WardType == "UNDEFINED" || x.Type == e.WardType)).ToList();
                if (candidates.Count == 0) continue;
                var hit = near.Quality != PosQuality.Estimated
                    ? candidates.OrderBy(x => (x.Pos - near.Pos).Length).First()
                    : candidates.OrderBy(x => x.Placed).First();
                hit.KilledAt = e.Time;
                hit.KilledBy = killer.Id;
            }
        }
        return list;
    }

    // ---------------------------------------------------------------- plates and lanes

    /// <summary>Plates each turret has lost by <paramref name="t"/>, keyed by turret position.</summary>
    public static Dictionary<Point, int> PlatesLost(GameTimeline g, double t)
    {
        var lost = new Dictionary<Point, int>();
        foreach (var e in g.Events)
        {
            if (e.Type != "TURRET_PLATE_DESTROYED" || e.Time > t || e.Position is not Point p) continue;
            var turret = NearestOuter(p, e.TeamId);
            if (turret is not Point tp) continue;
            lost[tp] = Math.Min(PlatesPerTurret, lost.GetValueOrDefault(tp) + 1);
        }
        return lost;
    }

    static Point? NearestOuter(Point p, int ownerTeam)
    {
        Point? best = null;
        double bestD = 1500;
        foreach (var (_, blue, red) in LaneTowers)
            foreach (var (team, outer) in new[] { (100, blue[0]), (200, red[0]) })
            {
                if (ownerTeam != 0 && ownerTeam != team) continue;
                double d = (outer - p).Length;
                if (d < bestD) { bestD = d; best = outer; }
            }
        return best;
    }

    static bool TowerDown(GameTimeline g, Point tower, double t) =>
        g.Events.Any(e => e.Type == "BUILDING_KILL" && e.BuildingType == "TOWER_BUILDING" && e.Time <= t && e.Position is Point p && (p - tower).Length < 600);

    public static List<LaneState> Lanes(GameTimeline g, double t)
    {
        var lost = PlatesLost(g, t);
        var result = new List<LaneState>();
        foreach (var (lane, blue, red) in LaneTowers)
        {
            result.Add(new LaneState(lane,
                BluePlatesTaken: lost.GetValueOrDefault(red[0]),   // blue takes plates off red's turret
                RedPlatesTaken: lost.GetValueOrDefault(blue[0]),
                BlueTowersUp: blue.Select(p => !TowerDown(g, p, t)).ToArray(),
                RedTowersUp: red.Select(p => !TowerDown(g, p, t)).ToArray()));
        }
        return result;
    }

    // ---------------------------------------------------------------- jungle routes

    /// <summary>Each team's jungler: Riot's position tag, or whoever had the most jungle CS at 4:00.</summary>
    public static List<GameParticipant> Junglers(GameTimeline g)
    {
        var list = new List<GameParticipant>();
        foreach (int team in new[] { 100, 200 })
        {
            var mates = g.Participants.Where(p => p.Team == team).ToList();
            var j = mates.FirstOrDefault(p => p.Lane == "JUNGLE");
            if (j == null)
            {
                var frame = g.Frames.LastOrDefault(f => f.Time <= 240) ?? g.Frames.FirstOrDefault();
                j = mates.OrderByDescending(p => frame?.Participants.GetValueOrDefault(p.Id)?.JungleMinionsKilled ?? 0).FirstOrDefault();
            }
            if (j != null) list.Add(j);
        }
        return list;
    }

    /// <summary>How far into the game a jungle route is followed.</summary>
    public const double RouteUntil = 240;

    /// <summary>
    /// The camps a jungler took in the first four minutes, in order, from their recorded minimap positions: a camp
    /// counts once they've spent a few seconds at it after it spawned. Empty if that part wasn't recorded.
    /// </summary>
    public static List<CampVisit> Route(GameTimeline g, int id)
    {
        Ensure(g);
        if (routes.TryGetValue(id, out var cached)) return cached;
        var route = new List<CampVisit>();
        routes[id] = route;
        var tracks = RiftSession.I.Tracks;
        if (tracks == null) return route;
        var s = tracks.Samples(id, 60, RouteUntil);
        return routes[id] = RouteFrom(s);
    }

    /// <summary>Works out camp visits from timed positions (separate so the self-test can feed it made-up paths).</summary>
    public static List<CampVisit> RouteFrom(List<(double T, Point P)> s)
    {
        var route = new List<CampVisit>();
        JungleCamp? at = null;
        double since = 0, last = 0;
        void Close()
        {
            if (at == null) return;
            // A camp takes several seconds to clear; walking past doesn't count, and a camp can't be taken
            // again before it respawns (buffs 5:00, scuttle 2:30, small camps 2:15).
            double respawn = at.Short is "Blue" or "Red" ? 300 : at.Side == 0 ? 150 : 135;
            double took = Math.Max(since, at.FirstSpawn);
            bool again = route.Any(v => v.Camp == at && took - v.Arrived < respawn);
            if (last - took >= 5 && !again)
                route.Add(new CampVisit(route.Count + 1, at, took, last));
            at = null;
        }
        for (int i = 0; i < s.Count; i++)
        {
            var (t, p) = s[i];
            var camp = Camps.Where(c => t >= c.FirstSpawn - 3 && (c.Pos - p).Length <= c.Radius).OrderBy(c => (c.Pos - p).Length).FirstOrDefault();
            bool gap = i > 0 && t - s[i - 1].T > 4;
            if (camp != at || gap)
            {
                Close();
                if (camp != null) { at = camp; since = t; }
            }
            last = t;
        }
        Close();
        return route;
    }

    /// <summary>"1:32" style clock.</summary>
    public static string Clock(double t) => AppState.FormatTime(t);
}
