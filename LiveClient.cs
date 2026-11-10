using System.Net.Http;
using System.Net.Security;
using System.Text.Json;

namespace RiftPen;

/// <summary>One player as the game reports them right now (items, level, KDA…) during a replay or spectated game.</summary>
public sealed record LivePlayer(
    string ChampionKey, string ChampionName, int Team, string Position, string RiotId, int Level, bool IsDead, double RespawnTimer,
    int Kills, int Deaths, int Assists, int CreepScore, double WardScore, int[] Items, int ItemValue, string[] Spells);

/// <summary>
/// Polls Riot's Live Client Data API (https://127.0.0.1:2999/liveclientdata/playerlist), which also works in replays.
/// It gives every player's items, level, KDA and CS at the replay's current moment.
/// </summary>
public sealed class LiveClient : IDisposable
{
    readonly HttpClient http;
    readonly CancellationTokenSource cts = new();
    volatile IReadOnlyList<LivePlayer> players = Array.Empty<LivePlayer>();
    Task? loop;

    public IReadOnlyList<LivePlayer> Players => players;
    public DateTime Updated { get; private set; }

    public LiveClient()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        http = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1:2999/"), Timeout = TimeSpan.FromSeconds(2) };
    }

    public void Start() => loop ??= Task.Run(() => Loop(cts.Token));

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (AppState.I.Replay.Connected)
            {
                try
                {
                    var json = await http.GetStringAsync("liveclientdata/playerlist", ct);
                    players = Parse(json);
                    Updated = DateTime.Now;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch { /* not available right now */ }
            }
            else if (players.Count > 0) players = Array.Empty<LivePlayer>();
            try { await Task.Delay(700, ct); } catch { break; }
        }
    }

    static IReadOnlyList<LivePlayer> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<LivePlayer>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            string raw = S(p, "rawChampionName");
            string key = raw.StartsWith("game_character_displayname_") ? raw["game_character_displayname_".Length..] : S(p, "championName");
            var items = new List<int>();
            int value = 0;
            if (p.TryGetProperty("items", out var its))
                foreach (var it in its.EnumerateArray())
                {
                    items.Add(I(it, "itemID"));
                    value += I(it, "price") * Math.Max(1, I(it, "count"));
                }
            var sc = p.TryGetProperty("scores", out var s) ? s : default;
            var spells = new List<string>();
            if (p.TryGetProperty("summonerSpells", out var ss))
                foreach (var slot in ss.EnumerateObject())
                    spells.Add(S(slot.Value, "displayName"));
            list.Add(new LivePlayer(
                key, S(p, "championName"), S(p, "team") == "CHAOS" ? 200 : 100, S(p, "position"),
                S(p, "riotId").Length > 0 ? S(p, "riotId") : S(p, "summonerName"),
                I(p, "level"), p.TryGetProperty("isDead", out var d) && d.ValueKind == JsonValueKind.True,
                p.TryGetProperty("respawnTimer", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetDouble() : 0,
                sc.ValueKind == JsonValueKind.Object ? I(sc, "kills") : 0,
                sc.ValueKind == JsonValueKind.Object ? I(sc, "deaths") : 0,
                sc.ValueKind == JsonValueKind.Object ? I(sc, "assists") : 0,
                sc.ValueKind == JsonValueKind.Object ? I(sc, "creepScore") : 0,
                sc.ValueKind == JsonValueKind.Object && sc.TryGetProperty("wardScore", out var ws) ? ws.GetDouble() : 0,
                items.ToArray(), value, spells.ToArray()));
        }
        return list;
    }

    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static int I(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;

    public void Dispose()
    {
        cts.Cancel();
        http.Dispose();
    }
}
