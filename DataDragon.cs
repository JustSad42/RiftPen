using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace RiftPen;

public sealed record ChampionInfo(string Id, string Name, int NumericKey = 0);

/// <summary>One range a champion has. Detail lists per-rank values when they differ.</summary>
public sealed record ChampionRange(string Slot, string SpellName, double Radius, string Detail);

/// <summary>
/// Reads champion data from Riot's official public Data Dragon CDN (no account or key needed)
/// and caches it per patch in %APPDATA%\RiftPen\ddragon.
/// </summary>
public static class DataDragon
{
    const string Base = "https://ddragon.leagueoflegends.com";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static string? version;
    static List<ChampionInfo>? champions;

    static string CacheRoot => Path.Combine(AppSettings.Folder, "ddragon");

    public static async Task<string> GetVersionAsync()
    {
        if (version != null) return version;
        try
        {
            var json = await http.GetStringAsync($"{Base}/api/versions.json");
            version = JsonSerializer.Deserialize<string[]>(json)![0];
        }
        catch
        {
            // Offline: fall back to the newest patch we've cached.
            version = Directory.Exists(CacheRoot)
                ? Directory.GetDirectories(CacheRoot).Select(Path.GetFileName).OrderByDescending(v => v).FirstOrDefault()
                : null;
            if (version == null) throw;
        }
        return version!;
    }

    public static async Task<List<ChampionInfo>> GetChampionsAsync()
    {
        if (champions != null) return champions;
        var v = await GetVersionAsync();
        using var doc = JsonDocument.Parse(await GetCachedAsync($"{Base}/cdn/{v}/data/en_US/champion.json", Path.Combine(v, "champion.json")));
        champions = doc.RootElement.GetProperty("data").EnumerateObject()
            .Select(p => new ChampionInfo(p.Value.GetProperty("id").GetString()!, p.Value.GetProperty("name").GetString()!,
                int.TryParse(p.Value.TryGetProperty("key", out var k) ? k.GetString() : null, out var nk) ? nk : 0))
            .OrderBy(c => c.Name)
            .ToList();
        return champions;
    }

    public static async Task<List<ChampionRange>> GetRangesAsync(string id)
    {
        var v = await GetVersionAsync();
        using var doc = JsonDocument.Parse(await GetCachedAsync($"{Base}/cdn/{v}/data/en_US/champion/{id}.json", Path.Combine(v, id + ".json")));
        var champ = doc.RootElement.GetProperty("data").GetProperty(id);
        var result = new List<ChampionRange>();

        if (champ.GetProperty("stats").TryGetProperty("attackrange", out var ar) && ar.GetDouble() > 0)
            result.Add(new ChampionRange("Auto", "Auto attack", ar.GetDouble(), ""));

        string[] slots = { "Q", "W", "E", "R" };
        int i = 0;
        foreach (var spell in champ.GetProperty("spells").EnumerateArray())
        {
            if (i >= slots.Length) break;
            var slot = slots[i++];
            var values = spell.TryGetProperty("range", out var range) && range.ValueKind == JsonValueKind.Array
                ? range.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToList()
                : new List<double>();
            // 0 = no range, 25000+ = global; neither makes a useful circle.
            var useful = values.Where(x => x > 0 && x < 25000).ToList();
            if (useful.Count == 0) continue;
            var distinct = useful.Distinct().ToList();
            result.Add(new ChampionRange(slot, spell.GetProperty("name").GetString() ?? slot, distinct.Max(),
                distinct.Count > 1 ? string.Join("/", useful.Select(x => x.ToString("0"))) : ""));
        }
        return result;
    }

    /// <summary>Local path of a cached image (champion square icon, item icon, or the Summoner's Rift map art).</summary>
    public static async Task<string?> GetImageAsync(string kind, string name)
    {
        try
        {
            var v = await GetVersionAsync();
            string rel = Path.Combine(v, "img", kind, name + ".png");
            var path = Path.Combine(CacheRoot, rel);
            if (File.Exists(path)) return path;
            var bytes = await http.GetByteArrayAsync($"{Base}/cdn/{v}/img/{kind}/{name}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch { return null; }
    }

    public static Task<string?> ChampionIconAsync(string championKey) => GetImageAsync("champion", championKey);
    public static Task<string?> ItemIconAsync(int itemId) => GetImageAsync("item", itemId.ToString());
    public static Task<string?> MapImageAsync() => GetImageAsync("map", "map11");

    static async Task<string> GetCachedAsync(string url, string relative)
    {
        var path = Path.Combine(CacheRoot, relative);
        if (File.Exists(path)) return await File.ReadAllTextAsync(path);
        var text = await http.GetStringAsync(url);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
        return text;
    }
}
