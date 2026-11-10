using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace RiftPen;

// ======================================================================= model

public sealed class GameParticipant
{
    public int Id { get; set; }
    public int ChampionId { get; set; }
    /// <summary>Data Dragon key, e.g. "MonkeyKing" (used for icons and matching the replay).</summary>
    public string ChampionKey { get; set; } = "";
    public string ChampionName { get; set; } = "";
    /// <summary>100 = blue (ORDER), 200 = red (CHAOS).</summary>
    public int Team { get; set; }
    public string Lane { get; set; } = "";
    public string RiotId { get; set; } = "";
    public int[] FinalItems { get; set; } = Array.Empty<int>();
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int FinalCs { get; set; }
    public int GoldEarned { get; set; }
    public int DamageToChampions { get; set; }
    public int VisionScore { get; set; }
}

public sealed class ParticipantFrame
{
    public Point? Position { get; set; }
    public int Level { get; set; }
    public int Xp { get; set; }
    public int CurrentGold { get; set; }
    public int TotalGold { get; set; }
    public int MinionsKilled { get; set; }
    public int JungleMinionsKilled { get; set; }
    public int? DamageToChampions { get; set; }
}

public sealed class GameFrame
{
    public double Time { get; set; }
    public Dictionary<int, ParticipantFrame> Participants { get; set; } = new();
}

public sealed class GameEvent
{
    public double Time { get; set; }
    public string Type { get; set; } = "";
    public Point? Position { get; set; }
    public int KillerId { get; set; }
    public int VictimId { get; set; }
    public int[] Assists { get; set; } = Array.Empty<int>();
    public int ParticipantId { get; set; }
    public int CreatorId { get; set; }
    public int ItemId { get; set; }
    public int SkillSlot { get; set; }
    public int TeamId { get; set; }
    public string? WardType { get; set; }
    public string? MonsterType { get; set; }
    public string? MonsterSubType { get; set; }
    public string? BuildingType { get; set; }
    public string? LaneType { get; set; }
    public string? TowerType { get; set; }
}

/// <summary>Everything RiftPen knows about one game.</summary>
public sealed class GameTimeline
{
    public long GameId { get; set; }
    public string Platform { get; set; } = "";
    public double Length { get; set; }
    public string Source { get; set; } = "";
    public List<GameParticipant> Participants { get; set; } = new();
    public List<GameFrame> Frames { get; set; } = new();
    public List<GameEvent> Events { get; set; } = new();

    public string Key => $"{Platform}-{GameId}";
    /// <summary>A problem worth showing (e.g. the API key failed and the client's data was used).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? LoadNote { get; set; }
    public bool HasItemEvents => Events.Any(e => e.Type == "ITEM_PURCHASED");
    public GameParticipant? P(int id) => Participants.FirstOrDefault(p => p.Id == id);
}

// ======================================================================= replay files

/// <summary>Reads League's .rofl replay files to find which game the open replay is.</summary>
public static class ReplayFiles
{
    public sealed record Info(string Path, string Platform, long GameId, double LengthSec, string[] Champions);

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "League of Legends", "Replays");

    static readonly Dictionary<string, (DateTime Stamp, Info? Info)> cache = new();

    /// <summary>Platform and game id from a name like "EUW1-8037918339.rofl".</summary>
    public static (string Platform, long GameId)? ParseName(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        int dash = name.LastIndexOf('-');
        if (dash <= 0 || !long.TryParse(name[(dash + 1)..], out var id)) return null;
        return (name[..dash].ToUpperInvariant(), id);
    }

    public static Info? Read(string path)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (cache.TryGetValue(path, out var c) && c.Stamp == stamp) return c.Info;
            var info = ReadUncached(path);
            cache[path] = (stamp, info);
            return info;
        }
        catch { return null; }
    }

    static Info? ReadUncached(string path)
    {
        if (ParseName(path) is not var (platform, gameId)) return null;
        // Newer replays keep the metadata JSON at the end of the file, older ones near the start.
        using var fs = File.OpenRead(path);
        string? json = FindMetadata(fs, fromEnd: true) ?? FindMetadata(fs, fromEnd: false);
        if (json == null) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double length = root.TryGetProperty("gameLength", out var gl) ? gl.GetDouble() / 1000.0 : 0;
        var champs = new List<string>();
        if (root.TryGetProperty("statsJson", out var sj) && sj.ValueKind == JsonValueKind.String)
        {
            using var stats = JsonDocument.Parse(sj.GetString()!);
            foreach (var p in stats.RootElement.EnumerateArray())
                if (p.TryGetProperty("SKIN", out var skin)) champs.Add(skin.GetString() ?? "");
        }
        return new Info(path, platform, gameId, length, champs.ToArray());
    }

    static string? FindMetadata(FileStream fs, bool fromEnd)
    {
        long len = Math.Min(fs.Length, 4 * 1024 * 1024);
        fs.Position = fromEnd ? fs.Length - len : 0;
        var buf = new byte[len];
        int read = 0;
        while (read < len) { int r = fs.Read(buf, read, (int)len - read); if (r <= 0) break; read += r; }
        var text = Encoding.UTF8.GetString(buf, 0, read);
        int start = text.IndexOf("{\"gameLength\"", StringComparison.Ordinal);
        if (start < 0) return null;
        // Walk to the matching closing brace (strings may contain braces).
        int depth = 0;
        bool inString = false;
        for (int i = start; i < text.Length; i++)
        {
            char ch = text[i];
            if (inString)
            {
                if (ch == '\\') i++;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return text.Substring(start, i - start + 1);
        }
        return null;
    }

    /// <summary>The replay file whose champions and length match the open replay.</summary>
    public static Info? FindMatch(double replayLength, IEnumerable<string> championKeys)
    {
        if (!Directory.Exists(Folder)) return null;
        var wanted = championKeys.Select(k => k.ToLowerInvariant()).OrderBy(k => k).ToArray();
        Info? best = null;
        double bestDiff = double.MaxValue;
        foreach (var f in Directory.GetFiles(Folder, "*.rofl").OrderByDescending(File.GetLastWriteTime))
        {
            var info = Read(f);
            if (info == null) continue;
            var champs = info.Champions.Select(c => c.ToLowerInvariant()).OrderBy(c => c).ToArray();
            if (wanted.Length > 0 && !champs.SequenceEqual(wanted)) continue;
            double diff = Math.Abs(info.LengthSec - replayLength);
            if (diff < 30 && diff < bestDiff) { best = info; bestDiff = diff; }
        }
        return best;
    }
}

// ======================================================================= loading

/// <summary>
/// Loads a game's timeline from the League client (no key needed: per-minute positions/gold/XP/CS, kills,
/// towers, objectives), or from Riot's web API when a key is set (adds item purchases, wards, skill order,
/// damage). Results are cached in %APPDATA%\RiftPen\games.
/// </summary>
public static class GameLoader
{
    public static string CacheFolder
    {
        get
        {
            var d = Path.Combine(AppSettings.Folder, "games");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static async Task<GameTimeline> LoadAsync(string platform, long gameId, CancellationToken ct = default)
    {
        var S = AppState.I;
        var cache = Path.Combine(CacheFolder, $"{platform}-{gameId}.json");
        string? apiKey = S.Settings.RiotApiKey?.Trim();

        // A cached copy is reused unless a key is now set and the cache lacks the extra API detail.
        if (File.Exists(cache))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<GameTimeline>(await File.ReadAllTextAsync(cache, ct));
                if (cached != null && (string.IsNullOrEmpty(apiKey) || cached.Source == "Riot API")) return cached;
            }
            catch { /* refetch */ }
        }

        GameTimeline? game = null;
        Exception? apiError = null;
        if (!string.IsNullOrEmpty(apiKey))
        {
            try { game = await LoadFromRiotApiAsync(platform, gameId, apiKey, ct); }
            catch (Exception ex) { apiError = ex; }
        }
        if (game == null)
        {
            try { game = await LoadFromClientAsync(platform, gameId, ct); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    apiError != null ? $"Riot API: {apiError.Message}. League client: {ex.Message}" : $"League client: {ex.Message}");
            }
        }

        await DataDragonNames(game);
        if (apiError != null) game.LoadNote = $"Your Riot API key didn't work ({apiError.Message}), so this uses the League client's data.";
        try { await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(game), ct); } catch { }
        return game;
    }

    // ---------------------------------------------------------------- League client (LCU)

    static (int Port, string Password)? Lockfile()
    {
        var installDir = Path.GetDirectoryName(Path.GetDirectoryName(AppState.I.Settings.GameCfgPath) ?? "") ?? @"C:\Riot Games\League of Legends";
        foreach (var candidate in new[] { Path.Combine(installDir, "lockfile"), @"C:\Riot Games\League of Legends\lockfile" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                using var fs = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var parts = new StreamReader(fs).ReadToEnd().Split(':');
                if (parts.Length >= 4 && int.TryParse(parts[2], out var port)) return (port, parts[3]);
            }
            catch { }
        }
        return null;
    }

    static HttpClient LocalClient(string? basicPassword)
    {
        var handler = new SocketsHttpHandler
        {
            // The League client serves a self-signed certificate on localhost only.
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        if (basicPassword != null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + basicPassword)));
        return http;
    }

    static async Task<GameTimeline> LoadFromClientAsync(string platform, long gameId, CancellationToken ct)
    {
        var lf = Lockfile() ?? throw new InvalidOperationException("the League client isn't running");
        using var http = LocalClient(lf.Password);
        var baseUrl = $"https://127.0.0.1:{lf.Port}/lol-match-history/v1";
        var gameJson = await http.GetStringAsync($"{baseUrl}/games/{gameId}", ct);
        var timelineJson = await http.GetStringAsync($"{baseUrl}/game-timelines/{gameId}", ct);
        var game = new GameTimeline { GameId = gameId, Platform = platform, Source = "League client" };
        ParseGameV4(game, gameJson);
        ParseTimeline(game, timelineJson);
        return game;
    }

    static void ParseGameV4(GameTimeline game, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        game.Length = r.TryGetProperty("gameDuration", out var gd) ? gd.GetDouble() : 0;
        var names = new Dictionary<int, string>();
        if (r.TryGetProperty("participantIdentities", out var ids))
            foreach (var pi in ids.EnumerateArray())
            {
                var pl = pi.GetProperty("player");
                string n = Str(pl, "gameName");
                string tag = Str(pl, "tagLine");
                names[pi.GetProperty("participantId").GetInt32()] = n.Length > 0 ? (tag.Length > 0 ? $"{n}#{tag}" : n) : Str(pl, "summonerName");
            }
        foreach (var p in r.GetProperty("participants").EnumerateArray())
        {
            int id = p.GetProperty("participantId").GetInt32();
            var st = p.GetProperty("stats");
            var gp = new GameParticipant
            {
                Id = id,
                ChampionId = p.GetProperty("championId").GetInt32(),
                Team = p.GetProperty("teamId").GetInt32(),
                RiotId = names.GetValueOrDefault(id, ""),
                Lane = p.TryGetProperty("timeline", out var tl) ? Str(tl, "lane") : "",
                FinalItems = Enumerable.Range(0, 7).Select(i => Int(st, $"item{i}")).ToArray(),
                Kills = Int(st, "kills"), Deaths = Int(st, "deaths"), Assists = Int(st, "assists"),
                FinalCs = Int(st, "totalMinionsKilled") + Int(st, "neutralMinionsKilled"),
                GoldEarned = Int(st, "goldEarned"),
                DamageToChampions = Int(st, "totalDamageDealtToChampions"),
                VisionScore = Int(st, "visionScore"),
            };
            game.Participants.Add(gp);
        }
    }

    // ---------------------------------------------------------------- Riot web API (match-v5)

    static string RegionFor(string platform) => platform.ToUpperInvariant() switch
    {
        "NA1" or "BR1" or "LA1" or "LA2" => "americas",
        "KR" or "JP1" => "asia",
        "OC1" or "PH2" or "SG2" or "TH2" or "TW2" or "VN2" => "sea",
        _ => "europe",
    };

    static async Task<GameTimeline> LoadFromRiotApiAsync(string platform, long gameId, string key, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("X-Riot-Token", key);
        var matchId = $"{platform.ToUpperInvariant()}_{gameId}";
        var baseUrl = $"https://{RegionFor(platform)}.api.riotgames.com/lol/match/v5/matches/{matchId}";
        async Task<string> Get(string url)
        {
            using var resp = await http.GetAsync(url, ct);
            if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403) throw new InvalidOperationException("the API key was refused (expired? Developer keys last 24 hours)");
            if ((int)resp.StatusCode == 404) throw new InvalidOperationException("Riot doesn't have this game (too old, or a custom/practice game)");
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
        var matchJson = await Get(baseUrl);
        var timelineJson = await Get(baseUrl + "/timeline");

        var game = new GameTimeline { GameId = gameId, Platform = platform, Source = "Riot API" };
        using (var doc = JsonDocument.Parse(matchJson))
        {
            var info = doc.RootElement.GetProperty("info");
            game.Length = info.TryGetProperty("gameDuration", out var gd) ? gd.GetDouble() : 0;
            if (game.Length > 100000) game.Length /= 1000; // very old games reported milliseconds
            foreach (var p in info.GetProperty("participants").EnumerateArray())
            {
                string n = Str(p, "riotIdGameName"), tag = Str(p, "riotIdTagline");
                game.Participants.Add(new GameParticipant
                {
                    Id = p.GetProperty("participantId").GetInt32(),
                    ChampionId = Int(p, "championId"),
                    ChampionKey = Str(p, "championName"),
                    Team = Int(p, "teamId"),
                    Lane = Str(p, "teamPosition"),
                    RiotId = n.Length > 0 ? (tag.Length > 0 ? $"{n}#{tag}" : n) : Str(p, "summonerName"),
                    FinalItems = Enumerable.Range(0, 7).Select(i => Int(p, $"item{i}")).ToArray(),
                    Kills = Int(p, "kills"), Deaths = Int(p, "deaths"), Assists = Int(p, "assists"),
                    FinalCs = Int(p, "totalMinionsKilled") + Int(p, "neutralMinionsKilled"),
                    GoldEarned = Int(p, "goldEarned"),
                    DamageToChampions = Int(p, "totalDamageDealtToChampions"),
                    VisionScore = Int(p, "visionScore"),
                });
            }
        }
        ParseTimeline(game, timelineJson);
        return game;
    }

    // ---------------------------------------------------------------- shared timeline parsing (v4 + v5)

    static void ParseTimeline(GameTimeline game, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("info", out var info)) root = info; // v5 wraps everything in "info"
        foreach (var f in root.GetProperty("frames").EnumerateArray())
        {
            var frame = new GameFrame { Time = Int(f, "timestamp") / 1000.0 };
            if (f.TryGetProperty("participantFrames", out var pfs))
                foreach (var prop in pfs.EnumerateObject())
                {
                    var pf = prop.Value;
                    int id = pf.TryGetProperty("participantId", out var pid) ? pid.GetInt32() : int.Parse(prop.Name);
                    frame.Participants[id] = new ParticipantFrame
                    {
                        Position = Pos(pf),
                        Level = Int(pf, "level"),
                        Xp = Int(pf, "xp"),
                        CurrentGold = Int(pf, "currentGold"),
                        TotalGold = Int(pf, "totalGold"),
                        MinionsKilled = Int(pf, "minionsKilled"),
                        JungleMinionsKilled = Int(pf, "jungleMinionsKilled"),
                        DamageToChampions = pf.TryGetProperty("damageStats", out var ds) ? Int(ds, "totalDamageDoneToChampions") : null,
                    };
                }
            game.Frames.Add(frame);
            if (!f.TryGetProperty("events", out var evs)) continue;
            foreach (var e in evs.EnumerateArray())
            {
                game.Events.Add(new GameEvent
                {
                    Time = Int(e, "timestamp") / 1000.0,
                    Type = Str(e, "type"),
                    Position = Pos(e),
                    KillerId = Int(e, "killerId"),
                    VictimId = Int(e, "victimId"),
                    Assists = e.TryGetProperty("assistingParticipantIds", out var a) && a.ValueKind == JsonValueKind.Array
                        ? a.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>(),
                    ParticipantId = Int(e, "participantId"),
                    CreatorId = Int(e, "creatorId"),
                    ItemId = Int(e, "itemId"),
                    SkillSlot = Int(e, "skillSlot"),
                    TeamId = Int(e, "teamId"),
                    WardType = NullIfEmpty(Str(e, "wardType")),
                    MonsterType = NullIfEmpty(Str(e, "monsterType")),
                    MonsterSubType = NullIfEmpty(Str(e, "monsterSubType")),
                    BuildingType = NullIfEmpty(Str(e, "buildingType")),
                    LaneType = NullIfEmpty(Str(e, "laneType")),
                    TowerType = NullIfEmpty(Str(e, "towerType")),
                });
            }
        }
        game.Events.Sort((x, y) => x.Time.CompareTo(y.Time));
        if (game.Length <= 0 && game.Frames.Count > 0) game.Length = game.Frames[^1].Time;
    }

    /// <summary>Fills in champion keys/names from Data Dragon.</summary>
    static async Task DataDragonNames(GameTimeline game)
    {
        try
        {
            var champs = await DataDragon.GetChampionsAsync();
            foreach (var p in game.Participants)
            {
                var c = champs.FirstOrDefault(x => x.NumericKey == p.ChampionId) ??
                        champs.FirstOrDefault(x => string.Equals(x.Id, p.ChampionKey, StringComparison.OrdinalIgnoreCase));
                if (c == null) continue;
                p.ChampionKey = c.Id;
                p.ChampionName = c.Name;
            }
        }
        catch { /* offline: keep what we have */ }
        foreach (var p in game.Participants)
            if (string.IsNullOrEmpty(p.ChampionName)) p.ChampionName = p.ChampionKey.Length > 0 ? p.ChampionKey : $"Champion {p.ChampionId}";
    }

    static Point? Pos(JsonElement e) =>
        e.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Object
            ? new Point(Int(p, "x"), Int(p, "y")) : null;

    static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
