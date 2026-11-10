using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace RiftPen;

/// <summary>How to read the Replay API camera. Defaults assume Riot's yaw/pitch/roll in degrees.</summary>
public sealed class CalibrationSettings
{
    public double YawOffset { get; set; }
    public bool InvertYaw { get; set; }
    public bool InvertPitch { get; set; }
    public bool SwapYawPitch { get; set; }
    public bool FovIsHorizontal { get; set; }
    public double FovScale { get; set; } = 1.0;
    /// <summary>Height of the ground plane drawings sit on (Summoner's Rift lanes are roughly 50).</summary>
    public double GroundHeight { get; set; } = 50;
}

/// <summary>A named circle size in game units (e.g. champion vision = 1350).</summary>
public sealed class RangePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New range";
    public double Radius { get; set; } = 500;
    public string Color { get; set; } = "#FFFFFF";
}

public sealed class ToolStyle
{
    public string Color { get; set; } = "#FFFF3B30";
    public double Thickness { get; set; } = 4;
}

public sealed class AppSettings
{
    public CalibrationSettings Calibration { get; set; } = new();
    public string GameCfgPath { get; set; } = @"C:\Riot Games\League of Legends\Config\game.cfg";
    public double? ToolbarLeft { get; set; }
    public double? ToolbarTop { get; set; }
    public bool ToolbarCollapsed { get; set; }
    public string Color { get; set; } = "#FFFF3B30";
    public double Thickness { get; set; } = 4;
    public bool ShowGrid { get; set; }
    public bool HideWorldWhenLeagueNotFocused { get; set; } = true;

    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = new();
    public ObservableCollection<RangePreset> RangePresets { get; set; } = DefaultRanges();
    public bool ShowRangeLabels { get; set; } = true;

    /// <summary>GPU-composited overlay (smooth at high refresh rates). Off = classic layered window.</summary>
    public bool FastRendering { get; set; } = true;
    /// <summary>Extra look-ahead for map drawings while the camera moves, in ms.</summary>
    public double PredictionMs { get; set; }

    // Replay timeline
    public bool TimelineEnabled { get; set; } = true;
    /// <summary>How long a replay note stays up, in replay seconds. 0 = until the end of the replay.</summary>
    public double NoteDurationSec { get; set; } = 20;
    public bool PauseWhileDrawing { get; set; } = true;

    // Drawings
    public bool RestoreDrawings { get; set; } = true;
    public double LaserSeconds { get; set; } = 1.5;

    // Minimap: x, y, width, height as fractions of the League window.
    public double[]? MinimapRect { get; set; }
    public bool MirrorToMinimap { get; set; } = true;
    public bool MinimapInput { get; set; } = true;
    public double MinimapWorldSize { get; set; } = 14870;

    // Ease of use
    /// <summary>Holding the draw-mode key opens the tool wheel; tapping still toggles drawing.</summary>
    public bool WheelOnHold { get; set; } = true;
    public bool AutoCollapseToolbar { get; set; } = true;
    public bool SmoothStrokes { get; set; } = true;
    public bool PenPressure { get; set; } = true;
    /// <summary>Last colour and size used with each tool, so e.g. the laser stays red.</summary>
    public Dictionary<string, ToolStyle> ToolStyles { get; set; } = new();
    public string LastShape { get; set; } = nameof(ToolKind.Arrow);

    /// <summary>New range circles follow the champion the replay camera is locked onto.</summary>
    public bool RangeFollow { get; set; }

    // Spotlight: dims everything except a circle around the cursor.
    public double SpotlightRadius { get; set; } = 220;
    public double SpotlightDim { get; set; } = 0.6;

    /// <summary>League's HUD settings when the minimap was last found; a change triggers a fresh detection.</summary>
    public string? MinimapCfgSignature { get; set; }
    public bool AutoDetectMinimap { get; set; } = true;
    public bool StartMenuShortcutDone { get; set; }

    // Rift Board
    /// <summary>Optional Riot developer/personal API key: adds item purchases, wards, skill order and damage.</summary>
    public string? RiotApiKey { get; set; }
    public bool TrackMinimap { get; set; } = true;
    public bool BoardCleanMap { get; set; }
    public double[]? BoardWindow { get; set; }

    // Notes timeline strip: centre x and top y as fractions of the League window.
    public bool ShowNotesTimeline { get; set; } = true;
    public double[] TimelineStripPos { get; set; } = { 0.5, 0.09 };

    // Screenshots
    public string ScreenshotFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RiftPen");
    public bool ScreenshotToClipboard { get; set; } = true;

    public static ObservableCollection<RangePreset> DefaultRanges() => new()
    {
        new() { Id = "vision", Name = "Champion vision", Radius = 1350, Color = "#FFD60A" },
        new() { Id = "xp", Name = "Experience range", Radius = 1400, Color = "#AF52DE" },
        new() { Id = "ward", Name = "Ward vision", Radius = 900, Color = "#34C759" },
        new() { Id = "tower", Name = "Tower range", Radius = 750, Color = "#FF3B30" },
        new() { Id = "adc", Name = "Ranged auto", Radius = 550, Color = "#32D7FF" },
        new() { Id = "flash", Name = "Flash", Radius = 400, Color = "#FFFFFF" },
        new() { Id = "smite", Name = "Smite", Radius = 500, Color = "#FF9500" },
        new() { Id = "ignite", Name = "Ignite", Radius = 600, Color = "#FF5E3A" },
        new() { Id = "exhaust", Name = "Exhaust", Radius = 650, Color = "#C7A0FF" },
    };

    /// <summary>Fills in default shortcuts for new actions, and upgrades old "Anywhere" flags to scopes.</summary>
    public void EnsureHotkeys()
    {
        foreach (var a in HotkeyCatalog.BuiltIn)
        {
            if (!Hotkeys.TryGetValue(a.Id, out var b)) { Hotkeys[a.Id] = a.Default.Clone(); continue; }
            if (b.Scope == null)
                b.Scope = b.SameCombo(a.Default) ? a.Default.EffectiveScope : (b.Anywhere ? HotkeyScope.Everywhere : HotkeyScope.Drawing);
        }
        foreach (var p in RangePresets)
            if (!Hotkeys.ContainsKey("range:" + p.Id)) Hotkeys["range:" + p.Id] = new HotkeyBinding { Scope = HotkeyScope.Drawing };
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RiftPen");
    static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch { s = new AppSettings(); /* corrupt file: fall back to defaults */ }
        s.EnsureHotkeys();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are a convenience; never crash over them */ }
    }

    public static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
    }
}
