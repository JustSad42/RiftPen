using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace RiftPen;

/// <summary>Shared app state: current tool/colour, the drawings, undo/redo, replay timeline and shortcuts.</summary>
public sealed class AppState
{
    public static AppState I { get; } = new();

    public static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x3B, 0x30), // red
        Color.FromRgb(0xFF, 0x95, 0x00), // orange
        Color.FromRgb(0xFF, 0xD6, 0x0A), // yellow
        Color.FromRgb(0x34, 0xC7, 0x59), // green
        Color.FromRgb(0x32, 0xD7, 0xFF), // cyan
        Color.FromRgb(0x2F, 0x6B, 0xFF), // blue
        Color.FromRgb(0xAF, 0x52, 0xDE), // purple
        Color.FromRgb(0xFF, 0x4F, 0xA3), // pink
        Color.FromRgb(0xFF, 0xFF, 0xFF), // white
        Color.FromRgb(0x10, 0x10, 0x10), // black
    };

    static readonly double[] Speeds = { 0.25, 0.5, 1, 2, 4, 8 };

    public AppSettings Settings { get; } = AppSettings.Load();
    public ReplayApiClient Replay { get; } = new();
    public List<DrawItem> Items { get; } = new();
    public List<string> HotkeyFailures { get; set; } = new();
    public InputHook? Hook { get; set; }

    /// <summary>Only the real app saves drawings to disk (not the self-test).</summary>
    public bool AutosaveEnabled { get; set; }

    public event Action? Changed;
    public event Action? ItemsChanged;
    /// <summary>Short on-screen message.</summary>
    public event Action<string>? Message;
    /// <summary>Things the windows handle: "screenshot", "toggleToolbar", "minimapPicked".</summary>
    public event Action<string>? UiRequest;

    sealed class UndoStep
    {
        public required Action Undo;
        public required Action Redo;
        public string? MergeKey;
        public DateTime At = DateTime.Now;
    }

    readonly Stack<UndoStep> undo = new();
    readonly Stack<UndoStep> redo = new();
    DispatcherTimer? saveTimer;

    ToolKind tool = ToolKind.Pen;
    Color color;
    double thickness;
    SpaceMode spaceMode = SpaceMode.Auto;
    MarkerKind marker = MarkerKind.Ward;
    bool drawMode, hidden, holdingDraw, pausedByUs;
    int rangeIndex;
    DrawItem? selected;

    AppState()
    {
        try { color = (Color)ColorConverter.ConvertFromString(Settings.Color); }
        catch { color = Palette[0]; }
        thickness = Math.Clamp(Settings.Thickness, 1, 30);
    }

    // ---------------------------------------------------------------- tool state

    public ToolKind Tool
    {
        get => tool;
        set
        {
            if (tool == value) return;
            // Each tool keeps its own colour and size (the laser stays red, the highlighter yellow…).
            if (RemembersStyle(tool)) Settings.ToolStyles[tool.ToString()] = new ToolStyle { Color = color.ToString(), Thickness = thickness };
            tool = value;
            if (RemembersStyle(value) &&
                (Settings.ToolStyles.TryGetValue(value.ToString(), out var style) || DefaultStyles.TryGetValue(value, out style)))
            {
                try { color = (Color)ColorConverter.ConvertFromString(style.Color); } catch { }
                thickness = Math.Clamp(style.Thickness, 1, 30);
                Settings.Color = color.ToString();
                Settings.Thickness = thickness;
            }
            if (IsShape(value)) Settings.LastShape = value.ToString();
            if (value != ToolKind.Select) selected = null;
            Raise();
        }
    }

    static readonly Dictionary<ToolKind, ToolStyle> DefaultStyles = new()
    {
        [ToolKind.Highlighter] = new ToolStyle { Color = "#FFFFD60A", Thickness = 6 },
        [ToolKind.Laser] = new ToolStyle { Color = "#FFFF3B30", Thickness = 5 },
    };

    // Select recolours what you pick; range circles use their preset's colour.
    static bool RemembersStyle(ToolKind k) => k is not (ToolKind.Select or ToolKind.Range);

    public static bool IsShape(ToolKind k) => k is ToolKind.Line or ToolKind.Arrow or ToolKind.Rectangle or ToolKind.Ellipse;

    public ToolKind LastShape => Enum.TryParse<ToolKind>(Settings.LastShape, out var k) && IsShape(k) ? k : ToolKind.Arrow;

    // ---------------------------------------------------------------- tool wheel + cheat sheet

    bool spotlight;

    /// <summary>Dims the screen except around the cursor (works while drawing or not).</summary>
    public bool Spotlight { get => spotlight; set { if (spotlight != value) { spotlight = value; Raise(); } } }

    public bool WheelOpen { get; private set; }
    /// <summary>True when the wheel opens, false when it closes (the canvas applies the choice).</summary>
    public event Action<bool>? WheelChanged;
    public bool CheatSheet { get; private set; }

    DispatcherTimer? holdTimer;
    bool drawKeyDown;

    void OpenWheel()
    {
        if (WheelOpen) return;
        WheelOpen = true;
        WheelChanged?.Invoke(true);
        Raise();
    }

    void CloseWheel()
    {
        if (!WheelOpen) return;
        WheelOpen = false;
        WheelChanged?.Invoke(false);
        Raise();
    }

    public MarkerKind Marker { get => marker; set { if (marker != value) { marker = value; Raise(); } } }
    public SpaceMode SpaceMode { get => spaceMode; set { if (spaceMode != value) { spaceMode = value; Raise(); } } }
    public bool Hidden { get => hidden; set { if (hidden != value) { hidden = value; Raise(); } } }
    public int RangeIndex { get => rangeIndex; set { if (rangeIndex != value) { rangeIndex = value; Raise(); } } }

    /// <summary>The drawing picked with the select tool.</summary>
    public DrawItem? Selected { get => selected; set { if (selected != value) { selected = value; Raise(); } } }

    public bool PickingMinimap { get; private set; }

    public bool DrawMode
    {
        get => drawMode;
        set
        {
            if (drawMode == value) return;
            drawMode = value;
            if (Hook != null) Hook.DrawMode = value;

            if (value)
            {
                Replay.Boost();
                // Freeze the replay while drawing so the note lands on the right moment.
                if (Settings.PauseWhileDrawing && Replay.Playback is { Paused: false })
                {
                    Replay.SetPaused(true);
                    pausedByUs = true;
                }
            }
            else
            {
                if (pausedByUs) Replay.SetPaused(false);
                pausedByUs = false;
                holdingDraw = false;
                if (PickingMinimap)
                {
                    PickingMinimap = false;
                    UiRequest?.Invoke("minimapPicked");
                }
            }
            Raise();
        }
    }

    public Color Color
    {
        get => color;
        set
        {
            if (Selected is { } sel && Tool == ToolKind.Select && Items.Contains(sel) && sel.Color != value)
            {
                var before = sel.Clone();
                sel.Color = value;
                PushEdit(sel, before, null);
            }
            if (color != value) { color = value; Settings.Color = value.ToString(); Raise(); }
        }
    }

    public double Thickness
    {
        get => thickness;
        set
        {
            value = Math.Clamp(Math.Round(value), 1, 30);
            if (Selected is { } sel && Tool == ToolKind.Select && Items.Contains(sel) && sel.Thickness != value)
            {
                var before = sel.Clone();
                sel.Thickness = value;
                PushEdit(sel, before, "size:" + RuntimeHelpers.GetHashCode(sel));
            }
            if (thickness != value) { thickness = value; Settings.Thickness = value; Raise(); }
        }
    }

    public RangePreset? CurrentRange
    {
        get
        {
            var list = Settings.RangePresets;
            return list.Count == 0 ? null : list[Math.Clamp(rangeIndex, 0, list.Count - 1)];
        }
    }

    public void SelectTool(ToolKind kind)
    {
        Tool = kind;
        DrawMode = true; // picking a tool starts drawing, like Epic Pen
    }

    public void CycleSpaceMode() => SpaceMode = (SpaceMode)(((int)SpaceMode + 1) % 3);
    public void ToggleGrid() { Settings.ShowGrid = !Settings.ShowGrid; Raise(); }
    public void NotifyChanged() => Raise();
    public void Say(string text) => Message?.Invoke(text);
    public void RequestUi(string what) => UiRequest?.Invoke(what);
    void Raise() => Changed?.Invoke();

    // ---------------------------------------------------------------- replay timeline

    ReplayApiClient.PlaybackState? keyFor;
    string? cachedKey;

    /// <summary>Identifies the open replay (by its exact length). Null when no replay is open.</summary>
    public string? ReplayKey
    {
        get
        {
            var pb = Replay.Playback;
            if (pb == null || pb.Length < 1) return null;
            if (!ReferenceEquals(pb, keyFor)) { keyFor = pb; cachedKey = pb.Length.ToString("0.0", CultureInfo.InvariantCulture); }
            return cachedKey;
        }
    }

    public double? ReplayTime => Replay.CurrentTime;

    /// <summary>Ties a new drawing to the current replay moment (when timeline notes are on).</summary>
    public void Stamp(DrawItem it) => Stamp(it, ReplayTime);

    /// <summary>Ties a drawing to replay moment <paramref name="t"/> (the Rift Board can be at a different moment than the replay).</summary>
    public void Stamp(DrawItem it, double? t)
    {
        if (!Settings.TimelineEnabled || it.Kind == ToolKind.Laser) return;
        var key = ReplayKey;
        if (key == null || t == null) return;
        it.ReplayKey = key;
        it.TimeStart = t;
        it.TimeEnd = Settings.NoteDurationSec > 0 ? t + Settings.NoteDurationSec : null;
    }

    /// <summary>Whether a drawing belongs on screen right now, and how faded it is (0–1).</summary>
    public bool Visible(DrawItem it, out double alpha) => Visible(it, ReplayTime, out alpha);

    /// <summary>Same, at replay moment <paramref name="time"/>.</summary>
    public bool Visible(DrawItem it, double? time, out double alpha)
    {
        alpha = 1;
        if (it.ReplayKey == null) return true;
        if (it.ReplayKey != ReplayKey) return false;
        if (it.TimeStart is not double start) return true;
        if (time is not double now) return false;
        if (now < start - 0.3) return false;
        if (it.TimeEnd is double end && now > end) return false;
        double fadeIn = Math.Clamp((now - (start - 0.3)) / 0.3, 0, 1);
        double fadeOut = it.TimeEnd is double e2 ? Math.Clamp((e2 - now) / 1.0, 0, 1) : 1;
        alpha = Math.Min(fadeIn, fadeOut);
        return alpha > 0.01;
    }

    public static string FormatTime(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    void JumpToNote(int direction)
    {
        var key = ReplayKey;
        if (key == null || ReplayTime is not double now) { Say("Open a replay to jump between notes."); return; }
        var times = Items.Where(i => i.ReplayKey == key && i.TimeStart != null).Select(i => i.TimeStart!.Value).Distinct().OrderBy(x => x).ToList();
        double? target = direction > 0
            ? times.Where(x => x > now + 0.75).Cast<double?>().FirstOrDefault()
            : times.Where(x => x < now - 0.75).Cast<double?>().LastOrDefault();
        if (target is not double t) { Say(direction > 0 ? "No later notes in this replay." : "No earlier notes in this replay."); return; }
        Replay.Seek(t + 0.05);
        Say($"Note at {FormatTime(t)}");
    }

    void StepSpeed(int direction)
    {
        if (Replay.Playback is not { } pb) return;
        int i = Array.FindIndex(Speeds, s => s >= pb.Speed - 0.01);
        if (i < 0) i = Speeds.Length - 1;
        i = Math.Clamp(i + direction, 0, Speeds.Length - 1);
        Replay.SetSpeed(Speeds[i]);
        Say($"Replay speed {Speeds[i]:0.##}×");
    }

    // ---------------------------------------------------------------- minimap

    public void StartMinimapPick()
    {
        PickingMinimap = true;
        if (DrawMode) Raise(); else DrawMode = true;
    }

    public void FinishMinimapPick(double[]? rect)
    {
        if (rect != null)
        {
            Settings.MinimapRect = rect;
            // Remember the HUD layout, so only a real HUD change makes auto-detection replace a hand-set area.
            Settings.MinimapCfgSignature = MinimapDetect.HudSignature(Settings.GameCfgPath);
            Settings.Save();
            Say("Minimap area saved.");
        }
        DrawMode = false; // also clears PickingMinimap and re-opens Settings
    }

    // ---------------------------------------------------------------- shortcuts

    /// <summary>Pushes the current shortcuts to the input hook.</summary>
    public void ApplyBindings()
    {
        Settings.EnsureHotkeys();
        Hook?.SetBindings(HotkeyCatalog.All(Settings).Select(a =>
            new InputHook.Entry(a.Id, Settings.Hotkeys.TryGetValue(a.Id, out var b) ? b : a.Default)));
    }

    public HotkeyBinding? BindingFor(string id) => Settings.Hotkeys.TryGetValue(id, out var b) && b.IsSet ? b : null;

    /// <summary>Runs a shortcut. <paramref name="down"/> is false when a held key is released.</summary>
    public void RunAction(string id, bool down)
    {
        if (id == "holdDraw")
        {
            if (down && !DrawMode) { DrawMode = true; holdingDraw = true; }
            else if (!down && holdingDraw) DrawMode = false;
            return;
        }
        if (id == "toggleDraw" && Settings.WheelOnHold)
        {
            // Tap = draw mode on/off. Hold = tool wheel, pick on release.
            if (down)
            {
                drawKeyDown = true;
                if (holdTimer == null)
                {
                    holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                    holdTimer.Tick += (_, _) => { holdTimer!.Stop(); if (drawKeyDown) OpenWheel(); };
                }
                holdTimer.Stop();
                holdTimer.Start();
            }
            else
            {
                drawKeyDown = false;
                holdTimer?.Stop();
                if (WheelOpen) CloseWheel();
                else DrawMode = !DrawMode;
            }
            return;
        }
        if (id == "wheel")
        {
            if (down) OpenWheel(); else CloseWheel();
            return;
        }
        if (id == "cheatsheet")
        {
            CheatSheet = down;
            Raise();
            return;
        }
        if (!down) return;

        var pb = Replay.Playback;
        switch (id)
        {
            case "toggleDraw": DrawMode = !DrawMode; return;
            case "stopDraw": DrawMode = false; return;
            case "cycleSpace": CycleSpaceMode(); return;
            case "undo": Undo(); return;
            case "redo": Redo(); return;
            case "clear": Clear(); return;
            case "deleteSelected":
                if (Selected is { } sel && Items.Contains(sel)) RemoveItems(new[] { sel });
                return;
            case "hide": Hidden = !Hidden; return;
            case "grid": ToggleGrid(); return;
            case "sizeUp": Thickness += 1; return;
            case "sizeDown": Thickness -= 1; return;
            case "spotlight":
                Spotlight = !Spotlight;
                return;
            case "rangeFollow":
                Settings.RangeFollow = !Settings.RangeFollow;
                Say(Settings.RangeFollow
                    ? "Range circles now follow the champion the camera is locked onto."
                    : "Range circles now stay where you place them.");
                Raise();
                return;
            case "screenshot":
            case "toggleToolbar":
            case "notes":
            case "board":
            case "ping":
                UiRequest?.Invoke(id);
                return;
            case "notesBar":
                Settings.ShowNotesTimeline = !Settings.ShowNotesTimeline;
                Settings.Save();
                Say(Settings.ShowNotesTimeline
                    ? "Replay notes bar shown."
                    : $"Replay notes bar hidden. Bring it back with {BindingFor("notesBar")?.Display ?? "the toolbar's timeline button (right-click)"}.");
                Raise();
                return;
            case "timeline":
                Settings.TimelineEnabled = !Settings.TimelineEnabled;
                Say(Settings.TimelineEnabled ? "Timeline notes on: new drawings follow the replay." : "Timeline notes off: new drawings stay up.");
                Raise();
                return;
            case "replayPause":
                if (pb == null) { Say("No replay open."); return; }
                Replay.SetPaused(!pb.Paused);
                pausedByUs = false; // the user is in charge now
                return;
            case "replayBack": if (ReplayTime is double tb) Replay.Seek(tb - 5); return;
            case "replayForward": if (ReplayTime is double tf) Replay.Seek(tf + 5); return;
            case "replaySlower": StepSpeed(-1); return;
            case "replayFaster": StepSpeed(+1); return;
            case "notePrev": JumpToNote(-1); return;
            case "noteNext": JumpToNote(+1); return;
        }
        if (id.StartsWith("tool:") && Enum.TryParse<ToolKind>(id[5..], out var kind)) { SelectTool(kind); return; }
        if (id.StartsWith("color:") && int.TryParse(id[6..], out var ci) && ci is >= 0 and < 10) { Color = Palette[ci]; return; }
        if (id.StartsWith("range:"))
        {
            int idx = Settings.RangePresets.ToList().FindIndex(p => p.Id == id[6..]);
            if (idx >= 0) { RangeIndex = idx; SelectTool(ToolKind.Range); }
        }
    }

    // ---------------------------------------------------------------- drawings + undo

    public void AddItem(DrawItem item)
    {
        Items.Add(item);
        Push(new UndoStep { Undo = () => Items.Remove(item), Redo = () => Items.Add(item) });
    }

    public void AddItems(List<DrawItem> items)
    {
        if (items.Count == 0) return;
        Items.AddRange(items);
        Push(new UndoStep
        {
            Undo = () => { foreach (var i in items) Items.Remove(i); },
            Redo = () => Items.AddRange(items),
        });
    }

    /// <summary>Records items already removed (in removal order, with the index each had) as one undo step.</summary>
    public void PushRemoval(List<(DrawItem Item, int Index)> removed)
    {
        Push(new UndoStep
        {
            Undo = () => { for (int i = removed.Count - 1; i >= 0; i--) Items.Insert(Math.Min(removed[i].Index, Items.Count), removed[i].Item); },
            Redo = () => { foreach (var r in removed) Items.Remove(r.Item); },
        });
    }

    public void RemoveItems(IEnumerable<DrawItem> which)
    {
        var set = which.ToHashSet();
        RemoveWhere(set.Contains);
    }

    /// <summary>Clears what's showing right now. Replay notes at other moments stay.</summary>
    public void Clear() => RemoveWhere(it => Visible(it, out _) && (it.Space == Space.Screen || Replay.Connected));

    /// <summary>Deletes saved replay notes: for one replay, or all of them when <paramref name="key"/> is null.</summary>
    public int DeleteReplayNotes(string? key)
    {
        int before = Items.Count;
        RemoveWhere(it => it.ReplayKey != null && (key == null || it.ReplayKey == key));
        return before - Items.Count;
    }

    void RemoveWhere(Func<DrawItem, bool> predicate)
    {
        var removed = new List<(DrawItem, int)>();
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!predicate(Items[i])) continue;
            removed.Add((Items[i], i));
            Items.RemoveAt(i);
        }
        if (removed.Count > 0) PushRemoval(removed);
    }

    /// <summary>
    /// Records a change to an existing drawing (move, recolour, resize). Rapid changes with the same
    /// <paramref name="mergeKey"/> (like dragging the size slider) become one undo step.
    /// </summary>
    public void PushEdit(DrawItem item, DrawItem before, string? mergeKey)
    {
        var after = item.Clone();
        if (mergeKey != null && undo.TryPeek(out var top) && top.MergeKey == mergeKey && (DateTime.Now - top.At).TotalSeconds < 1.5)
        {
            top.Redo = () => item.CopyFrom(after);
            top.At = DateTime.Now;
            redo.Clear();
            ItemsChanged?.Invoke();
            ScheduleSave();
            return;
        }
        Push(new UndoStep { Undo = () => item.CopyFrom(before), Redo = () => item.CopyFrom(after), MergeKey = mergeKey });
    }

    /// <summary>Mouse-wheel resize of the selected drawing.</summary>
    public void ScaleSelected(double factor)
    {
        if (Selected is not { } it || !Items.Contains(it)) return;
        var before = it.Clone();
        switch (it.Kind)
        {
            case ToolKind.Range:
                it.Radius = Math.Clamp(it.Radius * factor, 10, 20000);
                break;
            case ToolKind.Text:
            case ToolKind.Marker:
                it.Thickness = Math.Clamp(it.Thickness * factor, 1, 60);
                break;
            default:
                if (it.Points.Count == 0) break;
                var c = it.Kind == ToolKind.Ellipse && it.Space == Space.World
                    ? it.Points[0] // ground circles grow around their centre
                    : new Point(it.Points.Average(p => p.X), it.Points.Average(p => p.Y));
                for (int i = 0; i < it.Points.Count; i++) it.Points[i] = c + (it.Points[i] - c) * factor;
                break;
        }
        PushEdit(it, before, "scale:" + RuntimeHelpers.GetHashCode(it));
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        var s = undo.Pop();
        s.Undo();
        redo.Push(s);
        AfterItemsChanged();
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        var s = redo.Pop();
        s.Redo();
        undo.Push(s);
        AfterItemsChanged();
    }

    void Push(UndoStep step)
    {
        undo.Push(step);
        redo.Clear();
        AfterItemsChanged();
    }

    void AfterItemsChanged()
    {
        itemsVersion++;
        if (selected != null && !Items.Contains(selected)) selected = null;
        ItemsChanged?.Invoke();
        ScheduleSave();
    }

    // ---------------------------------------------------------------- fast lookup of what can show now

    int itemsVersion, contextVersion = -1;
    string? contextKey;
    List<DrawItem> contextItems = new();
    bool contextHasTimed;

    /// <summary>Call after changing <see cref="Items"/> directly (e.g. the eraser mid-drag).</summary>
    public void Touch() => itemsVersion++;

    /// <summary>Drawings that belong to the current context: always-on ones plus this replay's notes.</summary>
    public IReadOnlyList<DrawItem> ContextItems
    {
        get
        {
            var key = ReplayKey;
            if (contextVersion != itemsVersion || contextKey != key)
            {
                contextItems = Items.Where(i => i.ReplayKey == null || i.ReplayKey == key).ToList();
                contextHasTimed = contextItems.Any(i => i.ReplayKey != null);
                contextVersion = itemsVersion;
                contextKey = key;
            }
            return contextItems;
        }
    }

    /// <summary>The open replay has notes that fade in and out with its clock.</summary>
    public bool ContextHasTimedNotes { get { _ = ContextItems; return contextHasTimed; } }

    /// <summary>Saves soon without an undo step (e.g. renaming a note).</summary>
    public void SaveSoon() => ScheduleSave();

    // ---------------------------------------------------------------- autosave

    public void LoadAutosave()
    {
        if (!Settings.RestoreDrawings || !File.Exists(Persistence.AutosavePath)) return;
        try { Items.AddRange(Persistence.Load(Persistence.AutosavePath)); itemsVersion++; }
        catch (Exception ex) { AppSettings.LogError(ex); }
    }

    void ScheduleSave()
    {
        if (!AutosaveEnabled) return;
        if (saveTimer == null)
        {
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveNow(); };
        }
        saveTimer.Stop();
        saveTimer.Start();
    }

    public void SaveNow()
    {
        if (!AutosaveEnabled) return;
        try { Persistence.Save(Persistence.AutosavePath, Items); }
        catch (Exception ex) { AppSettings.LogError(ex); }
    }
}
