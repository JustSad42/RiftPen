using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RiftPen;

/// <summary>Transparent, always-on-top window covering one monitor. Click-through unless draw mode is on.</summary>
public partial class OverlayWindow : Window
{
    readonly AppState S = AppState.I;
    readonly uint ourPid = (uint)Environment.ProcessId;

    IntPtr hwnd;
    Native.RECT monitor;
    int monitorIndex;
    double scale = 1;

    IntPtr league;
    Native.RECT leaguePx;
    bool leagueFound;
    bool lastDrawMode;

    public OverlayWindow()
    {
        InitializeComponent();
        // Fast mode: a normal GPU-rendered window that Windows composites with per-pixel transparency.
        // Classic mode (AllowsTransparency) copies the whole frame through the CPU, which stutters at 4K/high refresh.
        AllowsTransparency = !S.Settings.FastRendering;
        Surface.EditLayer = EditLayer;

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            InstallStyleGuard();
            if (!AllowsTransparency) MakeGpuTransparent();
            Native.SetExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT, 0);
            RefreshLeague();
            monitorIndex = PickInitialMonitor();
            MoveToMonitor(monitorIndex);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => MoveToMonitor(monitorIndex));

        S.Changed += OnStateChanged;
        Surface.CursorProvider = CursorDip;
        StartLoop();
        ContentRendered += (_, _) =>
        {
            if (!AllowsTransparency) Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA);
        };
    }

    bool clickThrough = true;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct STYLESTRUCT { public uint Old, New; }

    delegate IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam);
    WndProc? subclassProc; // keep alive
    IntPtr wpfProc;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr CallWindowProc(IntPtr prev, IntPtr h, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// WPF strips WS_EX_LAYERED from windows without AllowsTransparency, and without LAYERED,
    /// WS_EX_TRANSPARENT doesn't let clicks through. This Win32 subclass runs after WPF's handler and puts back the bits we need.
    /// </summary>
    void InstallStyleGuard()
    {
        subclassProc = GuardStyles;
        wpfProc = Native.SetWindowLongPtr(hwnd, -4 /* GWLP_WNDPROC */, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(subclassProc));
    }

    IntPtr GuardStyles(IntPtr h, int msg, IntPtr wParam, IntPtr lParam)
    {
        var result = CallWindowProc(wpfProc, h, msg, wParam, lParam);
        const int WM_STYLECHANGING = 0x7C;
        if (msg == WM_STYLECHANGING && wParam.ToInt64() == Native.GWL_EXSTYLE)
        {
            var ss = System.Runtime.InteropServices.Marshal.PtrToStructure<STYLESTRUCT>(lParam);
            uint want = ss.New | (uint)Native.WS_EX_TOOLWINDOW;
            if (!AllowsTransparency) want |= (uint)Native.WS_EX_LAYERED;
            want = clickThrough ? want | (uint)Native.WS_EX_TRANSPARENT : want & ~(uint)Native.WS_EX_TRANSPARENT;
            if (want != ss.New)
            {
                ss.New = want;
                System.Runtime.InteropServices.Marshal.StructureToPtr(ss, lParam, false);
            }
        }
        return result;
    }

    void SetClickThrough(bool on)
    {
        clickThrough = on;
        if (on) Native.SetExStyle(hwnd, Native.WS_EX_TRANSPARENT, 0);
        else Native.SetExStyle(hwnd, 0, Native.WS_EX_TRANSPARENT);
    }

    void MakeGpuTransparent()
    {
        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int noRound = 1; // DWMWCP_DONOTROUND
        Native.DwmSetWindowAttribute(hwnd, 33, ref noRound, sizeof(int));
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        Native.DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int));
        // Layered + fully opaque alpha keeps it GPU-rendered but allows click-through.
        Native.SetExStyle(hwnd, Native.WS_EX_LAYERED, 0);
        Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA);
    }

    public string MonitorDescription => $"Monitor {monitorIndex + 1} ({monitor.Width}×{monitor.Height})";

    public void NextMonitor()
    {
        var count = Native.GetMonitors().Count;
        monitorIndex = count == 0 ? 0 : (monitorIndex + 1) % count;
        MoveToMonitor(monitorIndex);
    }

    void MoveToMonitor(int index)
    {
        var mons = Native.GetMonitors();
        if (mons.Count == 0 || hwnd == IntPtr.Zero) return;
        monitorIndex = Math.Clamp(index, 0, mons.Count - 1);
        monitor = mons[monitorIndex];
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    int PickInitialMonitor()
    {
        if (!leagueFound) return 0;
        var mons = Native.GetMonitors();
        int cx = (leaguePx.Left + leaguePx.Right) / 2, cy = (leaguePx.Top + leaguePx.Bottom) / 2;
        int i = mons.FindIndex(m => m.Contains(cx, cy));
        return i < 0 ? 0 : i;
    }

    void RefreshLeague()
    {
        league = Native.FindLeagueWindow();
        leagueFound = Native.TryGetClientScreenRect(league, out leaguePx);
        if (S.Hook != null) S.Hook.LeagueWindow = league;
    }

    /// <summary>Saves what you see (game + drawings) as a PNG, without the toolbar or status badge.</summary>
    public async void TakeScreenshot(Window? toolbar)
    {
        var area = leagueFound ? leaguePx : monitor;
        double oldOpacity = toolbar?.Opacity ?? 1;
        try
        {
            if (toolbar != null) toolbar.Opacity = 0;
            Surface.SuppressBadge = true;
            Surface.InvalidateVisual();
            await Task.Delay(120); // let the hidden toolbar and badge leave the screen
            var image = Screenshot.Capture(area);
            var path = Screenshot.Save(image, S.Settings.ScreenshotFolder);
            if (S.Settings.ScreenshotToClipboard)
            {
                try { Clipboard.SetImage(image); } catch { /* clipboard busy */ }
            }
            S.Say($"Screenshot saved: {System.IO.Path.GetFileName(path)}{(S.Settings.ScreenshotToClipboard ? " (also copied)" : "")}");
        }
        catch (Exception ex)
        {
            AppSettings.LogError(ex);
            S.Say("Screenshot failed: " + ex.Message);
        }
        finally
        {
            if (toolbar != null) toolbar.Opacity = oldOpacity;
            Surface.SuppressBadge = false;
            Surface.InvalidateVisual();
        }
    }

    bool ForegroundIsLeagueOrUs()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == league) return true;
        Native.GetWindowThreadProcessId(fg, out var pid);
        return pid == ourPid;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (hwnd == IntPtr.Zero) return;
        Tick();
    }

    // ---- adaptive update loop ----
    // Hot: every display frame (camera moving, laser fading, wheel open…).
    // Warm: 30×/s while a replay is open but nothing moves. Cold: 4×/s with no replay.
    // This keeps RiftPen near 0% CPU when idle instead of redrawing 165 times a second.

    DispatcherTimer? slowTimer;
    bool hot;
    long lastBusyTicks, lastLeagueTicks, lastTopmostTicks;

    void StartLoop()
    {
        slowTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        slowTimer.Tick += (_, _) => { if (!hot) Tick(); };
        slowTimer.Start();
        // Respond at once when something starts (drawing, wheel, laser) instead of waiting for the next slow tick.
        S.Changed += () =>
        {
            if (!Surface.Busy) return;
            lastBusyTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            SetHot(true);
        };
    }

    void SetHot(bool on)
    {
        if (on == hot) return;
        hot = on;
        if (on) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>Whether the overlay is redrawing every frame right now (for the self-check / settings readout).</summary>
    public bool IsHot => hot;

    void Tick()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double Ms(long since) => (now - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        if (Ms(lastLeagueTicks) >= 250)
        {
            lastLeagueTicks = now;
            RefreshLeague();
            scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            MaybeAutoDetectMinimap(now);
        }
        if (Ms(lastTopmostTicks) >= 3000)
        {
            lastTopmostTicks = now;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        if (slowTimer != null)
        {
            var want = TimeSpan.FromMilliseconds(S.Replay.Connected ? 33 : 250);
            if (slowTimer.Interval != want) slowTimer.Interval = want;
        }

        var cam = S.Replay.Predict(S.Settings.PredictionMs);
        bool visible = cam != null && leagueFound &&
                       (!S.Settings.HideWorldWhenLeagueNotFocused || ForegroundIsLeagueOrUs());

        // League's 3D view fills its client area; convert from physical pixels to our DIPs.
        Rect viewport = leagueFound
            ? new Rect((leaguePx.Left - monitor.Left) / scale, (leaguePx.Top - monitor.Top) / scale, leaguePx.Width / scale, leaguePx.Height / scale)
            : new Rect(0, 0, ActualWidth, ActualHeight);

        bool changed = Surface.Frame(cam, viewport, visible);
        if (changed || Surface.Busy || (S.Replay.Active && visible)) lastBusyTicks = now;
        SetHot(Ms(lastBusyTicks) < 1500);
        Timeline?.Update(leagueFound, leaguePx, visible || (leagueFound && ForegroundIsLeagueOrUs()));
    }

    // ---- automatic minimap detection ----

    long replaySinceTicks;
    bool autoDetectTried;

    void MaybeAutoDetectMinimap(long now)
    {
        if (!S.Replay.Connected) { replaySinceTicks = 0; autoDetectTried = false; return; }
        if (replaySinceTicks == 0) replaySinceTicks = now;
        if (autoDetectTried || !S.Settings.AutoDetectMinimap || !leagueFound || S.DrawMode || MinimapDetect.Running) return;
        if ((now - replaySinceTicks) / (double)System.Diagnostics.Stopwatch.Frequency < 3) return; // let the replay load
        autoDetectTried = true;
        var sig = MinimapDetect.HudSignature(S.Settings.GameCfgPath);
        bool needed = S.Settings.MinimapRect == null ||
                      (sig != null && S.Settings.MinimapCfgSignature != null && sig != S.Settings.MinimapCfgSignature);
        if (needed) DetectMinimap(automatic: true);
    }

    /// <summary>Finds the minimap by hiding it for a moment through the Replay API and seeing what changed.</summary>
    public async void DetectMinimap(bool automatic)
    {
        if (!leagueFound) { S.Say("Couldn't find the League window."); return; }
        S.Say(automatic ? "Finding your minimap (one moment)…" : "Looking for the minimap…");
        bool spotlightWas = S.Spotlight;
        var (rect, message) = await MinimapDetect.DetectAsync(leaguePx, hide =>
        {
            S.Spotlight = !hide && spotlightWas; // the spotlight follows the mouse and would confuse the comparison
            Surface.InvalidateVisual();
        });
        if (rect != null)
        {
            S.Settings.MinimapRect = rect;
            S.Settings.MinimapCfgSignature = MinimapDetect.HudSignature(S.Settings.GameCfgPath);
            S.Settings.Save();
            S.NotifyChanged();
        }
        S.Say(message);
    }

    /// <summary>The notes timeline strip, positioned over the League window.</summary>
    public TimelineWindow? Timeline { get; set; }

    /// <summary>Mouse position in overlay coordinates (works while click-through).</summary>
    public Point CursorDip()
    {
        Native.GetCursorPos(out var p);
        return new Point((p.X - monitor.Left) / scale, (p.Y - monitor.Top) / scale);
    }

    void OnStateChanged()
    {
        if (S.DrawMode == lastDrawMode) return;
        lastDrawMode = S.DrawMode;
        if (hwnd == IntPtr.Zero) return;

        if (S.DrawMode)
        {
            SetClickThrough(false);
            Activate();
            Surface.Focus();
            Keyboard.Focus(Surface);
        }
        else
        {
            Surface.CommitText();
            SetClickThrough(true);
            if (leagueFound) Native.SetForegroundWindow(league);
        }
    }
}
