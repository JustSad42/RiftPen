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

    /// <summary>A small pen-on-circle icon drawn at runtime.</summary>
    static System.Drawing.Icon MakeIcon()
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
