using System.Windows;
using System.Windows.Threading;

namespace RiftPen;

/// <summary>Where a champion's board position comes from.</summary>
public enum PosQuality { Tracked, LastSeen, Estimated }

/// <summary>A champion at one moment, as the Rift Board shows it.</summary>
public readonly record struct ChampState(GameParticipant P, Point Pos, PosQuality Quality, bool Dead, double RespawnIn, int Level);

/// <summary>
/// Follows the replay that's open in League: identifies its game, loads Riot's timeline for it, and records every
/// champion's position from the minimap while the replay plays. The Rift Board reads everything from here.
/// </summary>
public sealed class RiftSession
{
    public static RiftSession I { get; } = new();

    readonly AppState S = AppState.I;
    public LiveClient Live { get; } = new();
    public GameTimeline? Game { get; private set; }
    public GameTracks? Tracks { get; private set; }

    /// <summary>What's happening (empty once a game is loaded).</summary>
    public string Status { get; private set; } = "Open a replay in League to start.";
    /// <summary>Minimap tracking state, for the board's status line.</summary>
    public string TrackStatus { get; private set; } = "";
    /// <summary>Bumped whenever new positions are recorded.</summary>
    public int Version => version;
    public event Action? Changed;

    ChampionTracker? tracker;
    Dispatcher? ui;
    double loadedLength;
    int version;
    Task? loop;
    readonly CancellationTokenSource cts = new();

    public static readonly Point BlueFountain = new(400, 420), RedFountain = new(14340, 14390);

    public void Start(Dispatcher dispatcher)
    {
        ui = dispatcher;
        Live.Start();
        loop ??= Task.Run(() => Loop(cts.Token));
    }

    public void Stop()
    {
        cts.Cancel();
        try { loop?.Wait(1500); } catch { }
        Tracks?.Save();
        IconLibrary.Shared.Save();
        Live.Dispose();
    }

    volatile bool reload;

    /// <summary>Loads the game data again (e.g. after entering a Riot API key).</summary>
    public void Reload() => reload = true;

    void SetStatus(string s)
    {
        if (Status == s) return;
        Status = s;
        Raise();
    }

    void Raise() => ui?.BeginInvoke(() => Changed?.Invoke());

    void Unload()
    {
        Tracks?.Save();
        IconLibrary.Shared.Save();
        Game = null;
        Tracks = null;
        tracker = null;
        keyframes.Clear();
        Raise();
    }

    async Task Loop(CancellationToken ct)
    {
        double failedFor = -1;
        DateTime retryAt = DateTime.MinValue, lastSave = DateTime.Now;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (reload)
                {
                    reload = false;
                    if (Game != null) Unload();
                    failedFor = -1;
                }
                var pb = S.Replay.Playback;
                if (pb == null || pb.Length < 1)
                {
                    if (Game != null) Unload();
                    SetStatus("Open a replay in League to start. The Rift Board follows whichever replay is open.");
                    await Task.Delay(1000, ct);
                    continue;
                }
                if (Game != null && Math.Abs(pb.Length - loadedLength) > 0.5) Unload(); // a different replay

                if (Game == null)
                {
                    if (Math.Abs(failedFor - pb.Length) < 0.5 && DateTime.Now < retryAt) { await Task.Delay(1000, ct); continue; }
                    var players = Live.Players;
                    if (players.Count == 0) { SetStatus("Reading the replay…"); await Task.Delay(500, ct); continue; }
                    var match = ReplayFiles.FindMatch(pb.Length, players.Select(p => p.ChampionKey));
                    if (match == null)
                    {
                        failedFor = pb.Length;
                        retryAt = DateTime.Now.AddSeconds(15);
                        SetStatus($"Couldn't find this replay's .rofl file in {ReplayFiles.Folder}. Open replays from the League client's match history.");
                        continue;
                    }
                    SetStatus("Loading the game's timeline…");
                    try
                    {
                        var game = await GameLoader.LoadAsync(match.Platform, match.GameId, ct);
                        var tr = await ui!.InvokeAsync(() => ChampionTracker.CreateAsync(game)).Task.Unwrap();
                        Tracks = GameTracks.Load(game.Key);
                        tracker = tr;
                        BuildKeyframes(game);
                        loadedLength = pb.Length;
                        Game = game;
                        SetStatus(game.LoadNote ?? "");
                        Raise();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        failedFor = pb.Length;
                        retryAt = DateTime.Now.AddSeconds(30);
                        SetStatus("Couldn't load this game's data: " + ex.Message);
                    }
                    continue;
                }

                await RecordStep(pb, ct);
                if (Tracks is { Dirty: true } && (DateTime.Now - lastSave).TotalSeconds > 15)
                {
                    Tracks.Save();
                    IconLibrary.Shared.Save();
                    lastSave = DateTime.Now;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                AppSettings.LogError(ex);
                try { await Task.Delay(1000, ct); } catch { break; }
            }
        }
    }

    // ---------------------------------------------------------------- recording from the minimap

    double lastT = double.NaN, pausedAt = double.NaN;
    int pausedCaptures;

    void SetTrack(string s)
    {
        if (TrackStatus == s) return;
        TrackStatus = s;
        Raise();
    }

    async Task RecordStep(ReplayApiClient.PlaybackState pb, CancellationToken ct)
    {
        if (!S.Settings.TrackMinimap) { SetTrack("Minimap tracking is off (Settings → Rift Board)."); await Task.Delay(500, ct); return; }
        if (pb.Seeking) { await Task.Delay(150, ct); return; }
        double t = S.Replay.CurrentTime ?? pb.Time;

        // A jump in time (seek): "where it was a moment ago" no longer applies.
        if (!double.IsNaN(lastT) && (t < lastT - 0.5 || t > lastT + 3 * Math.Max(1, pb.Speed) + 1)) tracker!.ResetMotion();
        lastT = t;

        if (pb.Paused)
        {
            // Paused: capture a few times after each change (the minimap redraws a moment after a seek), then rest.
            if (Math.Abs(t - pausedAt) < 0.01)
            {
                if (pausedCaptures >= 3) { await Task.Delay(150, ct); return; }
            }
            else
            {
                pausedAt = t;
                pausedCaptures = 0;
                await Task.Delay(350, ct);
            }
            pausedCaptures++;
        }
        else pausedAt = double.NaN;

        if (MinimapScreenRect() is not Native.RECT rect)
        {
            SetTrack("Set where League's minimap is (Settings → Replay → Detect minimap) to record champion positions.");
            await Task.Delay(1000, ct);
            return;
        }
        if (!MinimapUncovered(rect))
        {
            SetTrack("League's minimap is covered or minimized, so positions aren't being recorded.");
            await Task.Delay(500, ct);
            return;
        }
        var px = Screenshot.CaptureBgra(rect);
        t = S.Replay.CurrentTime ?? t;
        if (px == null) { await Task.Delay(300, ct); return; }

        var seen = tracker!.Process(px, rect.Width, rect.Height, t, IsDeadLive, out _);
        foreach (var s in tracker.Plausible(seen, t)) Tracks!.Add(s.ParticipantId, t, s.World);
        Interlocked.Increment(ref version);
        SetTrack(seen.Count == 0
            ? "No champion icons found on the minimap (is it showing?)."
            : $"Recording positions from the minimap · knows {tracker.LearnedCount}/{Game!.Participants.Count} icons" +
              (tracker.PreloadedCount > 0 ? $" ({tracker.PreloadedCount} remembered from earlier replays)" : ""));
        await Task.Delay(pb.Paused ? 300 : 180, ct);
    }

    bool IsDeadLive(string key) => Live.Players.FirstOrDefault(p => p.ChampionKey == key)?.IsDead == true;

    Native.RECT? MinimapScreenRect()
    {
        var frac = S.Settings.MinimapRect;
        if (frac is not { Length: 4 }) return null;
        var hwnd = Native.FindLeagueWindow();
        if (hwnd == IntPtr.Zero || Native.IsIconic(hwnd) || !Native.TryGetClientScreenRect(hwnd, out var league)) return null;
        var r = new Native.RECT
        {
            Left = league.Left + (int)Math.Round(frac[0] * league.Width),
            Top = league.Top + (int)Math.Round(frac[1] * league.Height),
        };
        r.Right = r.Left + (int)Math.Round(frac[2] * league.Width);
        r.Bottom = r.Top + (int)Math.Round(frac[3] * league.Height);
        return r.Width > 40 && r.Height > 40 ? r : null;
    }

    /// <summary>The League window is what's showing at the minimap (RiftPen's own see-through overlay doesn't count).</summary>
    static bool MinimapUncovered(Native.RECT r)
    {
        var league = Native.FindLeagueWindow();
        foreach (var (x, y) in new[] { (r.Left + r.Width / 2, r.Top + r.Height / 2), (r.Left + 4, r.Top + 4), (r.Right - 4, r.Bottom - 4) })
        {
            var w = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = x, Y = y }), 2 /* GA_ROOT */);
            if (w == league) continue;
            Native.GetWindowThreadProcessId(w, out uint pid);
            bool ours = pid == (uint)Environment.ProcessId;
            if (ours && (Native.GetWindowLongPtr(w, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_LAYERED) != 0) continue;
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- the game at a given moment

    /// <summary>Rough positions from Riot's timeline (each minute, plus every kill), for parts not recorded.</summary>
    readonly Dictionary<int, List<(double T, Point P)>> keyframes = new();

    void BuildKeyframes(GameTimeline g)
    {
        keyframes.Clear();
        foreach (var p in g.Participants) keyframes[p.Id] = new();
        foreach (var f in g.Frames)
            foreach (var (id, pf) in f.Participants)
                if (pf.Position is Point pos && keyframes.TryGetValue(id, out var l)) l.Add((f.Time, pos));
        foreach (var e in g.Events)
        {
            if (e.Position is not Point pos) continue;
            if (e.Type == "CHAMPION_KILL")
            {
                foreach (var id in e.Assists.Append(e.KillerId).Append(e.VictimId))
                    if (keyframes.TryGetValue(id, out var l)) l.Add((e.Time, pos));
                // After respawning the victim starts at their fountain.
                if (g.P(e.VictimId) is { } v && keyframes.TryGetValue(v.Id, out var vl))
                    vl.Add((e.Time + DeathSeconds(LevelAt(v.Id, e.Time), e.Time), v.Team == 100 ? BlueFountain : RedFountain));
            }
            else if (e.Type is "BUILDING_KILL" or "ELITE_MONSTER_KILL" && keyframes.TryGetValue(e.KillerId, out var kl))
                kl.Add((e.Time, pos));
        }
        foreach (var l in keyframes.Values) l.Sort((a, b) => a.T.CompareTo(b.T));
    }

    Point Estimate(int id, double t)
    {
        if (!keyframes.TryGetValue(id, out var l) || l.Count == 0)
            return Game?.P(id)?.Team == 200 ? RedFountain : BlueFountain;
        if (t <= l[0].T) return l[0].P;
        for (int i = 1; i < l.Count; i++)
            if (l[i].T >= t)
            {
                var (ta, pa) = l[i - 1];
                var (tb, pb) = l[i];
                double k = tb - ta < 1e-6 ? 1 : (t - ta) / (tb - ta);
                return new Point(pa.X + (pb.X - pa.X) * k, pa.Y + (pb.Y - pa.Y) * k);
            }
        return l[^1].P;
    }

    public int LevelAt(int id, double t)
    {
        int level = 1;
        if (Game == null) return level;
        foreach (var f in Game.Frames)
        {
            if (f.Time > t) break;
            if (f.Participants.TryGetValue(id, out var pf) && pf.Level > 0) level = pf.Level;
        }
        return level;
    }

    /// <summary>League's death timer: base by level, longer as the game goes on (patch 14+ values, close enough for a board).</summary>
    public static double DeathSeconds(int level, double gameTime)
    {
        double[] brw = { 6, 6, 8, 8, 10, 12, 16, 21, 26, 32.5, 35, 37.5, 40, 42.5, 45, 47.5, 50, 52.5 };
        double b = brw[Math.Clamp(level, 1, 18) - 1];
        double min = gameTime / 60, tif = 0;
        if (min > 15) tif += Math.Min(min - 15, 15) * 2 * 0.00425;
        if (min > 30) tif += Math.Min(min - 30, 15) * 2 * 0.003;
        if (min > 45) tif += (min - 45) * 2 * 0.0145;
        return b * (1 + Math.Min(tif, 0.5));
    }

    /// <summary>Whether the live game data describes moment <paramref name="t"/> (the board is at the replay's moment).</summary>
    public bool LiveAt(double t) => S.Replay.CurrentTime is double now && Math.Abs(now - t) < 1.5 && Live.Players.Count > 0;

    public ChampState StateAt(GameParticipant p, double t)
    {
        bool dead = false;
        double respawn = 0;
        int level;
        var lp = LiveAt(t) ? Live.Players.FirstOrDefault(x => x.ChampionKey == p.ChampionKey) : null;
        Point? deathPos = null;
        if (lp != null)
        {
            dead = lp.IsDead;
            respawn = lp.RespawnTimer;
            level = lp.Level;
        }
        else level = LevelAt(p.Id, t);

        if (Game != null)
        {
            var death = Game.Events.LastOrDefault(e => e.Type == "CHAMPION_KILL" && e.VictimId == p.Id && e.Time <= t);
            if (death != null)
            {
                double until = death.Time + DeathSeconds(LevelAt(p.Id, death.Time), death.Time);
                if (lp == null && t < until) { dead = true; respawn = until - t; }
                if (dead) deathPos = death.Position;
            }
        }

        if (Tracks != null && !dead && Tracks.Lookup(p.Id, t, out var pos) is var fix and not GameTracks.Fix.None)
            return new ChampState(p, pos, fix == GameTracks.Fix.Seen ? PosQuality.Tracked : PosQuality.LastSeen, dead, respawn, level);
        return new ChampState(p, deathPos ?? Estimate(p.Id, t), PosQuality.Estimated, dead, respawn, level);
    }

    /// <summary>Team gold (blue minus red) at <paramref name="t"/>, from the per-minute timeline.</summary>
    public double GoldDiffAt(double t)
    {
        if (Game == null || Game.Frames.Count == 0) return 0;
        double Diff(GameFrame f) => f.Participants.Sum(kv => (Game.P(kv.Key)?.Team == 100 ? 1 : -1) * kv.Value.TotalGold);
        var frames = Game.Frames;
        if (t <= frames[0].Time) return Diff(frames[0]);
        for (int i = 1; i < frames.Count; i++)
            if (frames[i].Time >= t)
            {
                double k = (t - frames[i - 1].Time) / Math.Max(1, frames[i].Time - frames[i - 1].Time);
                return Diff(frames[i - 1]) + (Diff(frames[i]) - Diff(frames[i - 1])) * k;
            }
        return Diff(frames[^1]);
    }

    /// <summary>A participant's numbers from the timeline frame just before <paramref name="t"/>.</summary>
    public ParticipantFrame? FrameAt(int id, double t)
    {
        ParticipantFrame? last = null;
        if (Game == null) return null;
        foreach (var f in Game.Frames)
        {
            if (f.Time > t) break;
            if (f.Participants.TryGetValue(id, out var pf)) last = pf;
        }
        return last;
    }

    /// <summary>Recorded path of a champion between two moments (for trails).</summary>
    public List<Point> Trail(int id, double from, double to)
    {
        var list = new List<Point>();
        if (Tracks == null) return list;
        for (double t = from; t <= to; t += 0.25)
            if (Tracks.TryGet(id, t, out var p, maxGap: 4.5)) list.Add(p);
        return list;
    }
}
