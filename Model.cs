using System.Windows;
using System.Windows.Media;

namespace RiftPen;

// New members go at the end: saved drawings store these as numbers.
public enum ToolKind { Pen, Highlighter, Eraser, Line, Arrow, Rectangle, Ellipse, Text, Marker, Range, Laser, Select, Ping }

/// <summary>Where an item's points live: fixed screen position, or a spot on the League map's ground.</summary>
public enum Space { Screen, World }

/// <summary>Where new drawings go. Auto = on the map when a replay is open, otherwise on screen.</summary>
public enum SpaceMode { Auto, Map, Screen }

public enum MarkerKind { Ward, ControlWard, Danger, Missing, OnMyWay, Target, Objective, BlueDot, RedDot }

/// <summary>When a shortcut is active.</summary>
public enum HotkeyScope { Drawing, League, Everywhere }

public sealed class DrawItem
{
    public ToolKind Kind { get; init; }
    public Space Space { get; init; }
    public Color Color { get; set; }
    public double Thickness { get; set; }
    public MarkerKind Marker { get; init; }
    public string? Text { get; init; }
    /// <summary>Range circles: radius in game units.</summary>
    public double Radius { get; set; }

    /// <summary>
    /// Screen items: overlay coordinates (DIPs).
    /// World items: League map ground coordinates (X = world X, Y = world Z).
    /// </summary>
    public List<Point> Points { get; } = new();

    /// <summary>Replay notes: which replay (by length) and when, in replay seconds. Null = always shown.</summary>
    public string? ReplayKey { get; set; }
    public double? TimeStart { get; set; }
    public double? TimeEnd { get; set; }

    /// <summary>Pen-tablet pressure (0–1) per point, or null for mouse strokes.</summary>
    public List<float>? Pressure { get; set; }

    /// <summary>Range circles that move with whichever champion the replay camera is locked onto.</summary>
    public bool Follow { get; set; }

    /// <summary>Optional title shown in the replay notes list.</summary>
    public string? Note { get; set; }

    public DrawItem Clone()
    {
        var c = new DrawItem
        {
            Kind = Kind, Space = Space, Color = Color, Thickness = Thickness, Marker = Marker, Text = Text, Radius = Radius,
            ReplayKey = ReplayKey, TimeStart = TimeStart, TimeEnd = TimeEnd, Note = Note, Follow = Follow,
            Pressure = Pressure == null ? null : new List<float>(Pressure),
        };
        c.Points.AddRange(Points);
        return c;
    }

    /// <summary>Copies the editable parts (position, colour, size) back from a snapshot.</summary>
    public void CopyFrom(DrawItem o)
    {
        Color = o.Color;
        Thickness = o.Thickness;
        Radius = o.Radius;
        Points.Clear();
        Points.AddRange(o.Points);
        Pressure = o.Pressure == null ? null : new List<float>(o.Pressure);
    }
}
