using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Text.Json;

namespace RiftPen;

/// <summary>
/// Polls Riot's official Replay API (https://127.0.0.1:2999/replay/render) for the camera, about 250 times a second
/// on its own thread so map drawings keep up with high-refresh monitors.
/// It only answers while a replay or spectated game is open and EnableReplayApi=1 is set in game.cfg.
/// Nothing here touches game memory.
/// </summary>
public sealed class ReplayApiClient : IDisposable
{
    // Fast while the camera moves; slow once it has been still for a second (saves CPU for RiftPen and the game).
    const double FastIntervalMs = 4, QuietIntervalMs = 33, QuietAfterMs = 1000;
    long lastMoveTicks, boostUntilTicks;
    CameraState? lastPolled;

    /// <summary>Poll at full speed for the next couple of seconds (e.g. when drawing starts).</summary>
    public void Boost() => Interlocked.Exchange(ref boostUntilTicks, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2);

    /// <summary>True while polling fast.</summary>
    public bool Active { get; private set; }

    sealed record Sample(CameraState Cam, long Timestamp, Vec3 Velocity);

    /// <summary>Replay clock from /replay/playback. Stamp = when it was read (Stopwatch ticks).</summary>
    public sealed record PlaybackState(double Time, double Length, bool Paused, double Speed, long Stamp);

    volatile PlaybackState? playback;
    volatile bool refreshPlayback = true;
    long lastPlaybackTicks;

    public PlaybackState? Playback => playback;

    /// <summary>Current replay time in seconds, advanced smoothly between reads.</summary>
    public double? CurrentTime
    {
        get
        {
            var p = playback;
            if (p == null) return null;
            if (p.Paused) return p.Time;
            double elapsed = (Stopwatch.GetTimestamp() - p.Stamp) / (double)Stopwatch.Frequency;
            double t = p.Time + elapsed * p.Speed;
            return p.Length > 0 ? Math.Min(t, p.Length) : t;
        }
    }

    public void SetPaused(bool paused)
    {
        var p = playback;
        if (p != null) playback = p with { Time = CurrentTime ?? p.Time, Paused = paused, Stamp = Stopwatch.GetTimestamp() };
        Post(new { paused });
    }

    public void Seek(double time)
    {
        var p = playback;
        time = Math.Max(0, p is { Length: > 0 } ? Math.Min(time, p.Length) : time);
        if (p != null) playback = p with { Time = time, Stamp = Stopwatch.GetTimestamp() };
        Post(new { time });
    }

    public void SetSpeed(double speed)
    {
        var p = playback;
        if (p != null) playback = p with { Time = CurrentTime ?? p.Time, Speed = speed, Stamp = Stopwatch.GetTimestamp() };
        Post(new { speed });
    }

    void Post(object body)
    {
        var json = JsonSerializer.Serialize(body);
        Task.Run(async () =>
        {
            try
            {
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync("replay/playback", content);
            }
            catch { /* replay closed; nothing to control */ }
            refreshPlayback = true;
        });
    }

    /// <summary>Changes a /replay/render setting (e.g. {"interfaceMinimap": false}). Returns false if the game refused.</summary>
    public async Task<bool> SetRenderAsync(object body)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("replay/render", content);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    void PollPlayback(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "replay/playback");
            using var resp = http.Send(req, ct);
            if (!resp.IsSuccessStatusCode) return;
            using var stream = resp.Content.ReadAsStream(ct);
            using var doc = JsonDocument.Parse(stream);
            var r = doc.RootElement;
            double D(string name, double fallback) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
            bool paused = r.TryGetProperty("paused", out var pz) && pz.ValueKind == JsonValueKind.True;
            playback = new PlaybackState(D("time", 0), D("length", 0), paused, D("speed", 1), Stopwatch.GetTimestamp());
        }
        catch (OperationCanceledException) { throw; }
        catch { /* keep the last reading */ }
    }

    readonly HttpClient http;
    readonly CancellationTokenSource cts = new();
    readonly Queue<(Vec3 Pos, long T)> history = new();
    volatile Sample? latest;
    volatile string lastError = "Starting…";
    Thread? thread;

    public CameraState? Latest => latest?.Cam;
    public bool Connected => latest != null;
    public string LastError => lastError;
    public double PollsPerSecond { get; private set; }

    public ReplayApiClient()
    {
        var handler = new SocketsHttpHandler
        {
            // The game serves a self-signed certificate on localhost only.
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        };
        http = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1:2999/"), Timeout = TimeSpan.FromSeconds(1) };
    }

    public void Start()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "RiftPen camera poll", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>The camera, optionally pushed forward along its current motion by <paramref name="lookAheadMs"/>.</summary>
    public CameraState? Predict(double lookAheadMs)
    {
        var s = latest;
        if (s == null) return null;
        if (lookAheadMs <= 0) return s.Cam;
        double ageMs = (Stopwatch.GetTimestamp() - s.Timestamp) * 1000.0 / Stopwatch.Frequency;
        double t = Math.Clamp(ageMs + lookAheadMs, 0, 80) / 1000.0;
        return s.Cam with { Position = s.Cam.Position + s.Velocity * t };
    }

    void Loop()
    {
        Native.timeBeginPeriod(1); // lets Thread.Sleep(1) actually sleep ~1 ms
        var ct = cts.Token;
        int count = 0;
        long rateStart = Stopwatch.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "replay/render");
                    using var resp = http.Send(req, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Disconnect($"The game answered {(int)resp.StatusCode}. This is a live game, or the Replay API is off.");
                        ct.WaitHandle.WaitOne(1500);
                        continue;
                    }
                    using var stream = resp.Content.ReadAsStream(ct);
                    using var doc = JsonDocument.Parse(stream);
                    var cam = Parse(doc.RootElement);
                    long now = Stopwatch.GetTimestamp();
                    latest = new Sample(cam, now, Velocity(cam.Position, now));
                    if (!CameraState.SameView(cam, lastPolled)) lastMoveTicks = now;
                    lastPolled = cam;
                    lastError = "";
                    count++;
                    // The replay clock changes slowly; read it ~20 times a second, or right after we changed it.
                    if (refreshPlayback || (now - lastPlaybackTicks) * 1000.0 / Stopwatch.Frequency >= 50)
                    {
                        refreshPlayback = false;
                        lastPlaybackTicks = now;
                        PollPlayback(ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Disconnect(ex is HttpRequestException or TaskCanceledException or IOException
                        ? "No replay open (nothing answering on port 2999)."
                        : "Couldn't read the camera: " + ex.Message);
                    ct.WaitHandle.WaitOne(1500);
                    continue;
                }

                long nowT = Stopwatch.GetTimestamp();
                double sinceRate = (nowT - rateStart) / (double)Stopwatch.Frequency;
                if (sinceRate >= 1) { PollsPerSecond = count / sinceRate; count = 0; rateStart = nowT; }

                // Wait out the rest of the interval.
                long t = Stopwatch.GetTimestamp();
                bool moving = (t - lastMoveTicks) * 1000.0 / Stopwatch.Frequency < QuietAfterMs;
                Active = moving || t < Interlocked.Read(ref boostUntilTicks);
                double interval = Active ? FastIntervalMs : QuietIntervalMs;
                while (!ct.IsCancellationRequested &&
                       (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency < interval)
                    Thread.Sleep(1);
            }
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
    }

    void Disconnect(string why)
    {
        latest = null;
        playback = null;
        refreshPlayback = true;
        lastError = why;
        history.Clear();
        PollsPerSecond = 0;
    }

    /// <summary>Camera speed (units/second) over roughly the last 30 ms.</summary>
    Vec3 Velocity(Vec3 pos, long now)
    {
        long window = Stopwatch.Frequency * 30 / 1000;
        if (history.Count > 0)
        {
            var last = history.Last();
            var jump = pos - last.Pos;
            if (Math.Abs(jump.X) + Math.Abs(jump.Z) > 3000) history.Clear(); // camera snapped somewhere new
        }
        history.Enqueue((pos, now));
        while (history.Count > 2 && now - history.Peek().T > window) history.Dequeue();
        var oldest = history.Peek();
        double dt = (now - oldest.T) / (double)Stopwatch.Frequency;
        return dt < 0.008 ? default : (pos - oldest.Pos) * (1 / dt);
    }

    static CameraState Parse(JsonElement r)
    {
        var p = r.GetProperty("cameraPosition");
        var q = r.GetProperty("cameraRotation");
        double fov = r.TryGetProperty("fieldOfView", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 45;
        string mode = r.TryGetProperty("cameraMode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        bool attached = r.TryGetProperty("cameraAttached", out var a) && a.ValueKind == JsonValueKind.True;
        return new CameraState(
            new Vec3(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(), p.GetProperty("z").GetDouble()),
            q.GetProperty("x").GetDouble(), q.GetProperty("y").GetDouble(), q.GetProperty("z").GetDouble(),
            fov, mode, attached);
    }

    public void Dispose()
    {
        cts.Cancel();
        thread?.Join(500);
        http.Dispose();
    }
}
