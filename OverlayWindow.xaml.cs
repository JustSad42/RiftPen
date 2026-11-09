using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

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
    int frame;
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
        CompositionTarget.Rendering += OnFrame;
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
        frame++;
        if (frame % 15 == 0)
        {
            RefreshLeague();
            scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
        if (frame % 180 == 0)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        var cam = S.Replay.Predict(S.Settings.PredictionMs);
        bool visible = cam != null && leagueFound &&
                       (!S.Settings.HideWorldWhenLeagueNotFocused || ForegroundIsLeagueOrUs());

        // League's 3D view fills its client area; convert from physical pixels to our DIPs.
        Rect viewport = leagueFound
            ? new Rect((leaguePx.Left - monitor.Left) / scale, (leaguePx.Top - monitor.Top) / scale, leaguePx.Width / scale, leaguePx.Height / scale)
            : new Rect(0, 0, ActualWidth, ActualHeight);

        Surface.Frame(cam, viewport, visible);
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
