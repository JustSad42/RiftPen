using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace RiftPen;

/// <summary>
/// "Hide RiftPen from stream capture": keeps RiftPen's own controls (toolbar, notes bar, notes list, settings and
/// their menus) out of OBS, Discord, Snipping Tool and other screen capture, while you still see them. Your
/// drawings and the Rift Board stay in the capture. Uses Windows' display affinity (Windows 10 2004 or newer).
/// </summary>
internal static class CaptureGuard
{
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    static readonly List<WeakReference<Window>> guarded = new();
    static bool installed;

    static bool On => AppState.I.Settings.HideUiFromCapture;

    /// <summary>Keeps <paramref name="w"/> (and the popups inside it) hidden from capture while the setting is on.</summary>
    public static void Guard(Window w)
    {
        Install();
        guarded.Add(new WeakReference<Window>(w));
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply(w);
        else w.SourceInitialized += (_, _) => Apply(w);
        w.Loaded += (_, _) => HookPopups(w);
    }

    /// <summary>Re-applies the setting to every guarded window (after it's switched on or off).</summary>
    public static void Refresh()
    {
        guarded.RemoveAll(r => !r.TryGetTarget(out _));
        foreach (var r in guarded)
            if (r.TryGetTarget(out var w)) Apply(w);
    }

    static void Apply(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) SetWindowDisplayAffinity(h, On ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    static void ApplyTo(Visual? v)
    {
        if (v != null && PresentationSource.FromVisual(v) is HwndSource src) SetWindowDisplayAffinity(src.Handle, On ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    static bool IsGuarded(DependencyObject? d)
    {
        var w = d == null ? null : Window.GetWindow(d);
        return w != null && guarded.Any(r => r.TryGetTarget(out var g) && g == w);
    }

    /// <summary>Menus and tooltips open in windows of their own; give them the same treatment when they open.</summary>
    static void Install()
    {
        if (installed) return;
        installed = true;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is ContextMenu m && IsGuarded(m.PlacementTarget)) ApplyTo(m);
        }));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is ToolTip t && IsGuarded(t.PlacementTarget)) ApplyTo(t);
        }));
    }

    /// <summary>The toolbar's own popups (colours, shapes, boards…).</summary>
    static void HookPopups(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Popup p) p.Opened += (_, _) => ApplyTo(p.Child);
            HookPopups(child);
        }
    }
}
