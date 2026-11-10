using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace RiftPen;

/// <summary>Notification-area icon with a small menu.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon icon;
    readonly WinForms.ToolStripMenuItem startup;

    public TrayIcon(Action toggleToolbar, Action toggleDraw, Action openSettings, Action quit)
    {
        startup = new WinForms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = Startup.IsEnabled };
        startup.Click += (_, _) => Startup.Set(startup.Checked);

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show / hide toolbar", null, (_, _) => toggleToolbar());
        menu.Items.Add("Draw mode on/off", null, (_, _) => toggleDraw());
        menu.Items.Add("Settings…", null, (_, _) => openSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(startup);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit RiftPen", null, (_, _) => quit());
        menu.Opening += (_, _) => startup.Checked = Startup.IsEnabled;

        icon = new WinForms.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "RiftPen",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) toggleToolbar(); };
    }

    /// <summary>The app's own icon (from the exe), falling back to a drawn one.</summary>
    static System.Drawing.Icon MakeIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } own) return own;
        }
        catch { }
        return DrawIcon();
    }

    static System.Drawing.Icon DrawIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var ring = new System.Drawing.Pen(System.Drawing.Color.FromArgb(58, 111, 247), 3);
            g.FillEllipse(System.Drawing.Brushes.Black, 2, 2, 28, 28);
            g.DrawEllipse(ring, 3, 3, 26, 26);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 214, 10), 4) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Triangle };
            g.DrawLine(pen, 22, 9, 11, 21);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}

/// <summary>RiftPen's entry in the Start menu (current user only).</summary>
public static class StartMenu
{
    static string LinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RiftPen.lnk");

    public static bool Exists => File.Exists(LinkPath);

    /// <summary>Creates or refreshes the shortcut so it points at this exe.</summary>
    public static void Create()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(LinkPath);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "RiftPen: draw on your screen and on the League of Legends map";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    public static void Remove()
    {
        if (File.Exists(LinkPath)) File.Delete(LinkPath);
    }

    /// <summary>A dev build in bin\ shouldn't put itself in the Start menu.</summary>
    public static bool IsDevBuild =>
        Environment.ProcessPath?.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>"Start with Windows" via the current user's Run key.</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "RiftPen";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, false);
    }
}

/// <summary>Grabs part of the screen (game + overlay, as you see it) into a PNG.</summary>
internal static class Screenshot
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    public static BitmapSource Capture(Native.RECT r)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    /// <summary>
    /// Raw BGRA pixels of a screen area, top row first. Without CAPTUREBLT, layered windows (RiftPen's own overlay)
    /// are left out, so only the game shows.
    /// </summary>
    public static byte[]? CaptureBgra(Native.RECT r, bool includeOverlays = false)
    {
        if (r.Width <= 0 || r.Height <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            if (!BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, SRCCOPY | (includeOverlays ? CAPTUREBLT : 0))) return null;
            SelectObject(mem, old);
            var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = r.Width, biHeight = -r.Height, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[r.Width * r.Height * 4];
            return GetDIBits(mem, bmp, 0, (uint)r.Height, bits, ref info, 0) == r.Height ? bits : null;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static string Save(BitmapSource image, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"RiftPen_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }
}
