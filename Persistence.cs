using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace RiftPen;

/// <summary>On-disk form of a drawing.</summary>
public sealed class ItemDto
{
    public ToolKind Kind { get; set; }
    public Space Space { get; set; }
    public string Color { get; set; } = "#FFFFFFFF";
    public double Thickness { get; set; }
    public MarkerKind Marker { get; set; }
    public string? Text { get; set; }
    public double Radius { get; set; }
    public double[] P { get; set; } = Array.Empty<double>();
    public string? Replay { get; set; }
    public double? T0 { get; set; }
    public double? T1 { get; set; }
}

/// <summary>Autosave of all drawings, plus reusable "boards" (saved layouts).</summary>
public static class Persistence
{
    static readonly JsonSerializerOptions Json = new();

    public static string AutosavePath => Path.Combine(AppSettings.Folder, "drawings.json");

    public static string BoardsFolder
    {
        get
        {
            var d = Path.Combine(AppSettings.Folder, "boards");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static ItemDto ToDto(DrawItem it) => new()
    {
        Kind = it.Kind, Space = it.Space, Color = it.Color.ToString(), Thickness = it.Thickness, Marker = it.Marker,
        Text = it.Text, Radius = it.Radius, P = it.Points.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
        Replay = it.ReplayKey, T0 = it.TimeStart, T1 = it.TimeEnd,
    };

    public static DrawItem FromDto(ItemDto d)
    {
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(d.Color); }
        catch { c = Colors.White; }
        var it = new DrawItem
        {
            Kind = d.Kind, Space = d.Space, Color = c, Thickness = d.Thickness, Marker = d.Marker, Text = d.Text, Radius = d.Radius,
            ReplayKey = d.Replay, TimeStart = d.T0, TimeEnd = d.T1,
        };
        for (int i = 0; i + 1 < d.P.Length; i += 2) it.Points.Add(new Point(d.P[i], d.P[i + 1]));
        return it;
    }

    public static void Save(string path, IEnumerable<DrawItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(items.Select(ToDto).ToList(), Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static List<DrawItem> Load(string path)
    {
        var list = JsonSerializer.Deserialize<List<ItemDto>>(File.ReadAllText(path), Json) ?? new();
        return list.Select(FromDto).Where(i => i.Points.Count > 0 && Enum.IsDefined(i.Kind)).ToList();
    }

    /// <summary>Most recently used boards first.</summary>
    public static List<string> RecentBoards(int max = 8) =>
        Directory.GetFiles(BoardsFolder, "*.rpboard")
            .OrderByDescending(File.GetLastWriteTime)
            .Take(max)
            .ToList();
}
