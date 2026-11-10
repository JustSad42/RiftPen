using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Threading;

namespace RiftPen;

/// <summary>A key or mouse-button combo. Vk 4/5/6 = middle mouse / mouse 4 / mouse 5.</summary>
public sealed class HotkeyBinding
{
    public int Vk { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    /// <summary>When the shortcut works. Null only in settings saved by older versions.</summary>
    public HotkeyScope? Scope { get; set; }

    /// <summary>Older settings' flag; read for upgrading, kept in sync with <see cref="Scope"/>.</summary>
    public bool Anywhere { get; set; }

    [JsonIgnore] public bool IsSet => Vk != 0;
    [JsonIgnore] public HotkeyScope EffectiveScope => Scope ?? (Anywhere ? HotkeyScope.Everywhere : HotkeyScope.Drawing);

    public void SetScope(HotkeyScope s)
    {
        Scope = s;
        Anywhere = s != HotkeyScope.Drawing;
    }

    public HotkeyBinding Clone() => (HotkeyBinding)MemberwiseClone();

    public bool SameCombo(HotkeyBinding o) => Vk == o.Vk && Ctrl == o.Ctrl && Alt == o.Alt && Shift == o.Shift && Win == o.Win;

    public string Display
    {
        get
        {
            if (!IsSet) return "Not set";
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Win) parts.Add("Win");
            parts.Add(KeyName(Vk));
            return string.Join("+", parts);
        }
    }

    public override string ToString() => Display;

    public static string KeyName(int vk)
    {
        switch (vk)
        {
            case 4: return "Middle mouse";
            case 5: return "Mouse 4";
            case 6: return "Mouse 5";
        }
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key switch
        {
            Key.Oem3 => "~ `",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPlus => "=",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Oem2 => "/",
            Key.Oem5 => "\\",
            Key.Oem1 => ";",
            Key.Oem7 => "'",
            Key.Escape => "Esc",
            Key.Delete => "Del",
            Key.Back => "Backspace",
            Key.Return => "Enter",
            Key.Capital => "Caps Lock",
            Key.Left => "←",
            Key.Right => "→",
            Key.Up => "↑",
            Key.Down => "↓",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(key - Key.NumPad0),
            _ => key.ToString(),
        };
    }

    public static HotkeyBinding Of(Key key, HotkeyScope scope = HotkeyScope.Drawing, bool ctrl = false, bool alt = false, bool shift = false)
    {
        var b = new HotkeyBinding { Vk = KeyInterop.VirtualKeyFromKey(key), Ctrl = ctrl, Alt = alt, Shift = shift };
        b.SetScope(scope);
        return b;
    }

    public static HotkeyBinding None(HotkeyScope scope = HotkeyScope.Drawing)
    {
        var b = new HotkeyBinding();
        b.SetScope(scope);
        return b;
    }
}

public sealed record HotkeyAction(string Id, string Name, string Group, HotkeyBinding Default);

public static class HotkeyCatalog
{
    static readonly string[] ColorNames = { "red", "orange", "yellow", "green", "cyan", "blue", "purple", "pink", "white", "black" };

    public static readonly IReadOnlyList<HotkeyAction> BuiltIn = Build();

    static List<HotkeyAction> Build()
    {
        const HotkeyScope L = HotkeyScope.League, E = HotkeyScope.Everywhere;
        var list = new List<HotkeyAction>
        {
            new("toggleDraw", "Draw mode on/off", "General", HotkeyBinding.Of(Key.Oem3, L)),
            new("holdDraw", "Hold to draw (release to stop)", "General", HotkeyBinding.None(L)),
            new("wheel", "Tool wheel (hold, release to pick)", "General", HotkeyBinding.None(L)),
            new("cheatsheet", "Show shortcuts (hold)", "General", HotkeyBinding.Of(Key.F1)),
            new("stopDraw", "Stop drawing", "General", HotkeyBinding.Of(Key.Escape)),
            new("cycleSpace", "Switch AUTO / MAP / SCREEN", "General", HotkeyBinding.Of(Key.M)),
            new("undo", "Undo", "General", HotkeyBinding.Of(Key.Z, ctrl: true)),
            new("redo", "Redo", "General", HotkeyBinding.Of(Key.Y, ctrl: true)),
            new("clear", "Clear what's on screen", "General", HotkeyBinding.Of(Key.Delete)),
            new("deleteSelected", "Delete selected drawing", "General", HotkeyBinding.Of(Key.Back)),
            new("hide", "Hide / show drawings", "General", HotkeyBinding.Of(Key.H, E, ctrl: true, alt: true)),
            new("toggleToolbar", "Show / hide toolbar", "General", HotkeyBinding.Of(Key.T, E, ctrl: true, alt: true)),
            new("screenshot", "Screenshot with drawings", "General", HotkeyBinding.Of(Key.F8, L)),
            new("spotlight", "Spotlight around cursor on/off", "General", HotkeyBinding.Of(Key.S, L, ctrl: true, alt: true)),
            new("ping", "Ping at cursor", "General", HotkeyBinding.None(L)),
            new("grid", "Calibration test grid", "General", HotkeyBinding.Of(Key.G)),

            new("tool:Select", "Select / move", "Tools", HotkeyBinding.Of(Key.S)),
            new("tool:Pen", "Pen", "Tools", HotkeyBinding.Of(Key.P)),
            new("tool:Highlighter", "Highlighter", "Tools", HotkeyBinding.Of(Key.H)),
            new("tool:Laser", "Laser pointer (fades)", "Tools", HotkeyBinding.Of(Key.Q)),
            new("tool:Ping", "Ping (click for a ripple)", "Tools", HotkeyBinding.Of(Key.W)),
            new("tool:Eraser", "Eraser", "Tools", HotkeyBinding.Of(Key.E)),
            new("tool:Line", "Line", "Tools", HotkeyBinding.Of(Key.L)),
            new("tool:Arrow", "Arrow", "Tools", HotkeyBinding.Of(Key.A)),
            new("tool:Rectangle", "Rectangle", "Tools", HotkeyBinding.Of(Key.R)),
            new("tool:Ellipse", "Circle", "Tools", HotkeyBinding.Of(Key.O)),
            new("tool:Text", "Text", "Tools", HotkeyBinding.Of(Key.T)),
            new("tool:Marker", "Marker", "Tools", HotkeyBinding.Of(Key.K)),
            new("tool:Range", "Range circle (last used)", "Tools", HotkeyBinding.Of(Key.V)),
            new("rangeFollow", "Range circles follow locked champion on/off", "Tools", HotkeyBinding.Of(Key.F)),
            new("sizeDown", "Smaller", "Tools", HotkeyBinding.Of(Key.OemOpenBrackets)),
            new("sizeUp", "Bigger", "Tools", HotkeyBinding.Of(Key.OemCloseBrackets)),

            new("replayPause", "Play / pause replay", "Replay", HotkeyBinding.Of(Key.Space)),
            new("replayBack", "Back 5 seconds", "Replay", HotkeyBinding.Of(Key.Left)),
            new("replayForward", "Forward 5 seconds", "Replay", HotkeyBinding.Of(Key.Right)),
            new("replaySlower", "Slower", "Replay", HotkeyBinding.Of(Key.Down)),
            new("replayFaster", "Faster", "Replay", HotkeyBinding.Of(Key.Up)),
            new("notePrev", "Jump to previous note", "Replay", HotkeyBinding.Of(Key.Left, shift: true)),
            new("noteNext", "Jump to next note", "Replay", HotkeyBinding.Of(Key.Right, shift: true)),
            new("timeline", "Timeline notes on/off", "Replay", HotkeyBinding.None()),
            new("notes", "Notes list for this replay", "Replay", HotkeyBinding.Of(Key.N)),
        };
        for (int i = 0; i < 10; i++)
            list.Add(new($"color:{i}", $"Color {(i + 1) % 10} ({ColorNames[i]})", "Colors", HotkeyBinding.Of(i == 9 ? Key.D0 : Key.D1 + i)));
        return list;
    }

    public static IEnumerable<HotkeyAction> All(AppSettings s) =>
        BuiltIn.Concat(s.RangePresets.Select(p => new HotkeyAction("range:" + p.Id, $"{p.Name} ({p.Radius:0})", "Range circles", HotkeyBinding.None())));
}

/// <summary>
/// Low-level keyboard + mouse hook on its own thread, so shortcuts work while League is focused
/// (League blocks normal Windows hotkeys). Matching presses are kept from reaching other apps.
/// The hook thread never waits on the UI, so it can't add input lag.
/// </summary>
public sealed class InputHook : IDisposable
{
    public sealed record Entry(string Id, HotkeyBinding Binding);

    readonly Dispatcher ui;
    readonly Native.LowLevelProc kbProc, mouseProc; // keep delegates alive
    readonly uint ourPid = (uint)Environment.ProcessId;
    Thread? thread;
    uint threadId;
    IntPtr kbHook, mouseHook;

    volatile Entry[] entries = Array.Empty<Entry>();
    volatile Action<HotkeyBinding>? capture;
    volatile bool drawMode, editingText;
    long leagueWindow;

    // Only touched on the hook thread.
    readonly HashSet<int> pressed = new(), swallowed = new();
    readonly Dictionary<int, string> held = new();

    public event Action<string, bool>? ActionTriggered; // raised on the UI thread
    public bool Installed { get; private set; }

    public InputHook(Dispatcher ui)
    {
        this.ui = ui;
        kbProc = KeyboardProc;
        mouseProc = MouseProc;
    }

    public bool DrawMode { set => drawMode = value; }
    public bool EditingText { set => editingText = value; }
    public IntPtr LeagueWindow { set => Interlocked.Exchange(ref leagueWindow, value.ToInt64()); }

    public void SetBindings(IEnumerable<Entry> list)
    {
        entries = list.Where(e => e.Binding.IsSet).ToArray();
        UpdateMouseNeed();
    }

    /// <summary>The next key/mouse-button combo goes to <paramref name="callback"/> (on the UI thread) instead of triggering anything.</summary>
    public void BeginCapture(Action<HotkeyBinding> callback)
    {
        capture = callback;
        UpdateMouseNeed();
    }

    public void CancelCapture()
    {
        capture = null;
        UpdateMouseNeed();
    }

    // The mouse hook sees every mouse movement system-wide, so it's only installed while a
    // shortcut uses a mouse button (or a new shortcut is being recorded).
    const int WM_APP_MOUSEHOOK = 0x8001;
    volatile bool wantMouse;

    void UpdateMouseNeed()
    {
        bool want = capture != null || entries.Any(e => e.Binding.Vk is 4 or 5 or 6);
        if (want == wantMouse) return;
        wantMouse = want;
        if (threadId != 0) Native.PostThreadMessage(threadId, WM_APP_MOUSEHOOK, IntPtr.Zero, IntPtr.Zero);
    }

    void ApplyMouseHook()
    {
        if (wantMouse && mouseHook == IntPtr.Zero)
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, Native.GetModuleHandle(null), 0);
        else if (!wantMouse && mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(mouseHook);
            mouseHook = IntPtr.Zero;
        }
    }

    public bool MouseHookInstalled => mouseHook != IntPtr.Zero;

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "RiftPen input hook", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        ready.Wait(2000);
    }

    void Run(ManualResetEventSlim ready)
    {
        threadId = Native.GetCurrentThreadId();
        var mod = Native.GetModuleHandle(null);
        kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, kbProc, mod, 0);
        ApplyMouseHook();
        Installed = kbHook != IntPtr.Zero;
        ready.Set();
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            if (msg.message == WM_APP_MOUSEHOOK) ApplyMouseHook();
        if (kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(kbHook);
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
    }

    IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
            if (down || up)
            {
                int vk = (int)Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam).vkCode;
                if (Handle(vk, down)) return (IntPtr)1;
            }
        }
        return Native.CallNextHookEx(kbHook, code, wParam, lParam);
    }

    IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            int vk = 0;
            bool down = false;
            switch (msg)
            {
                case Native.WM_MBUTTONDOWN: vk = 4; down = true; break;
                case Native.WM_MBUTTONUP: vk = 4; break;
                case Native.WM_XBUTTONDOWN:
                case Native.WM_XBUTTONUP:
                    uint data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam).mouseData >> 16;
                    vk = data == 1 ? 5 : data == 2 ? 6 : 0;
                    down = msg == Native.WM_XBUTTONDOWN;
                    break;
            }
            if (vk != 0 && Handle(vk, down)) return (IntPtr)1;
        }
        return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;

    /// <summary>League's game window, or one of RiftPen's own windows, is in front.</summary>
    bool LeagueOrUsFocused()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg.ToInt64() == Interlocked.Read(ref leagueWindow)) return true;
        Native.GetWindowThreadProcessId(fg, out var pid);
        return pid == ourPid;
    }

    /// <summary>Returns true to keep the input from reaching other apps.</summary>
    bool Handle(int vk, bool down)
    {
        if (IsModifier(vk)) return false;

        if (!down)
        {
            pressed.Remove(vk);
            if (held.Remove(vk, out var heldId)) Post(heldId, false);
            return swallowed.Remove(vk);
        }

        if (!pressed.Add(vk)) return swallowed.Contains(vk); // key auto-repeat

        var combo = new HotkeyBinding
        {
            Vk = vk,
            Ctrl = Native.IsDown(0x11),
            Alt = Native.IsDown(0x12),
            Shift = Native.IsDown(0x10),
            Win = Native.IsDown(0x5B) || Native.IsDown(0x5C),
        };

        var cap = capture;
        if (cap != null)
        {
            capture = null;
            swallowed.Add(vk);
            ui.BeginInvoke(() => cap(combo));
            return true;
        }

        bool drawing = drawMode && !editingText;
        bool? inLeague = null;
        foreach (var e in entries)
        {
            if (!e.Binding.SameCombo(combo)) continue;
            var scope = e.Binding.EffectiveScope;
            if (e.Id is "holdDraw" or "wheel" && scope == HotkeyScope.Drawing) scope = HotkeyScope.League; // must work before drawing starts
            bool active = scope switch
            {
                HotkeyScope.Everywhere => !editingText || e.Binding.Ctrl || e.Binding.Alt,
                HotkeyScope.League => drawing || (inLeague ??= LeagueOrUsFocused()) && !editingText,
                _ => drawing,
            };
            if (!active) continue;
            swallowed.Add(vk);
            held[vk] = e.Id;
            Post(e.Id, true);
            return true;
        }
        return false;
    }

    void Post(string id, bool down) => ui.BeginInvoke(() => ActionTriggered?.Invoke(id, down));

    public void Dispose()
    {
        if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
