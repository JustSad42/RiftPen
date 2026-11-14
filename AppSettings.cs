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
    /// <summary>Pen, highlighter and shape strokes fade away on their own (good for live commentary).</summary>
    public bool FadingInk { get; set; }
    /// <summary>How long fading ink stays before it fades, in seconds.</summary>
    public double FadeSeconds { get; set; } = 4;

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

    // Picked last time (restored at start-up)
    public string? LastTool { get; set; }
    public string? LastSpaceMode { get; set; }
    public string? LastMarker { get; set; }
    public string? LastRangeId { get; set; }

    /// <summary>New range circles follow the champion the replay camera is locked onto.</summary>
    public bool RangeFollow { get; set; }

    // Spotlight: dims everything except a circle around the cursor.
    public double SpotlightRadius { get; set; } = 220;
    public double SpotlightDim { get; set; } = 0.6;

    /// <summary>League's HUD settings when the minimap was last found; a change triggers a fresh detection.</summary>
    public string? MinimapCfgSignature { get; set; }
    public bool AutoDetectMinimap { get; set; } = true;
    public bool StartMenuShortcutDone { get; set; }
    /// <summary>Download new versions from GitHub in the background; they install when RiftPen quits.</summary>
    public bool AutoUpdate { get; set; } = true;

    // Rift Board
    /// <summary>Optional Riot developer/personal API key: adds item purchases, wards, skill order and damage.</summary>
    public string? RiotApiKey { get; set; }
    public bool TrackMinimap { get; set; } = true;
    public bool BoardCleanMap { get; set; }
    public bool BoardTrails { get; set; } = true;
    /// <summary>Board moves with the replay (off = sandbox).</summary>
    public bool BoardLinked { get; set; } = true;
    public bool BoardTimers { get; set; } = true;
    public bool BoardWards { get; set; } = true;
    public bool BoardJungle { get; set; }
    /// <summary>Off, Path or Heatmap (see BoardPathMode).</summary>
    public string BoardPathMode { get; set; } = "Off";
    /// <summary>How far back paths/heatmaps look, in seconds; 0 = whole game so far.</summary>
    public double BoardPathWindow { get; set; } = 120;
    public string BoardMatchupLane { get; set; } = "TOP";
    public string BoardMatchupMetric { get; set; } = "Gold";
    /// <summary>Open the board on a screen League isn't on, so it never covers the minimap.</summary>
    public bool BoardAvoidLeagueScreen { get; set; } = true;
    public double BoardZoom { get; set; } = 1;
    /// <summary>World point (X, Z) at the centre of the board view.</summary>
    public double[]? BoardCenter { get; set; }
    public double[]? BoardWindow { get; set; }

    // Notes timeline strip: centre x and top y as fractions of the League window.
    public bool ShowNotesTimeline { get; set; } = true;
    public double[] TimelineStripPos { get; set; } = { 0.5, 0.09 };

    /// <summary>Keep RiftPen's toolbar, notes bar, menus and settings out of OBS/Discord capture (drawings still show).</summary>
    public bool HideUiFromCapture { get; set; }

    /// <summary>Ward and control ward markers on the map show the area they'd reveal (900 units).</summary>
    public bool WardVision { get; set; } = true;

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

    string? lastWritten;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var json = JsonSerializer.Serialize(this, Json);
            if (json == lastWritten && File.Exists(FilePath)) return; // nothing changed since the last write
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true); // never leaves a half-written file if RiftPen is killed mid-save
            lastWritten = json;
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
