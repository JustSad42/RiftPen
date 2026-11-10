using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RiftPen;

/// <summary>
/// Finds the minimap automatically: with the replay paused, it grabs the screen with the minimap shown,
/// hides the minimap through the Replay API (interfaceMinimap), grabs it again, and measures where the
/// pixels changed. Re-runs on its own when League's HUD settings (minimap size/side, HUD scale, resolution) change.
/// </summary>
public static class MinimapDetect
{
    public static bool Running { get; private set; }

    /// <summary>League's HUD settings that affect where the minimap is, or null if game.cfg can't be read.</summary>
    public static string? HudSignature(string cfgPath)
    {
        try
        {
            var wanted = new[] { "MinimapScale", "FlipMiniMap", "GlobalScale", "Width", "Height", "WindowMode" };
            var values = File.ReadLines(cfgPath)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2 && wanted.Contains(p[0].Trim()))
                .GroupBy(p => p[0].Trim())
                .Select(g => $"{g.Key}={g.First()[1].Trim()}")
                .OrderBy(s => s);
            return string.Join(";", values);
        }
        catch { return null; }
    }

    static bool MinimapOnLeft(string cfgPath)
    {
        try { return File.ReadLines(cfgPath).Any(l => l.Replace(" ", "").Equals("FlipMiniMap=1", StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    /// <summary>Returns the minimap as fractions of the League window (x, y, w, h), or null with a reason.</summary>
    internal static async Task<(double[]? Rect, string Message)> DetectAsync(Native.RECT league, Action<bool> hideOverlayExtras)
    {
        var S = AppState.I;
        if (Running) return (null, "Already looking for the minimap.");
        if (!S.Replay.Connected || S.Replay.Playback == null) return (null, "Open a replay first.");
        if (league.Width < 200 || league.Height < 200) return (null, "Couldn't find the League window.");

        Running = true;
        bool wasPaused = S.Replay.Playback.Paused;
        try
        {
            if (!wasPaused) S.Replay.SetPaused(true);
            hideOverlayExtras(true);
            await Task.Delay(400); // let the paused frame and our hidden extras settle

            // Only the bottom 60% of the window can hold the minimap.
            var area = new Native.RECT { Left = league.Left, Right = league.Right, Top = league.Top + (int)(league.Height * 0.4), Bottom = league.Bottom };
            var shown = Pixels(Screenshot.Capture(area));
            if (!await S.Replay.SetRenderAsync(new { interfaceMinimap = false }))
                return (null, "The replay didn't let RiftPen hide the minimap. Use \"Set minimap area…\" instead.");
            await Task.Delay(350);
            var hidden = Pixels(Screenshot.Capture(area));
            await S.Replay.SetRenderAsync(new { interfaceMinimap = true });

            var box = ChangedSquare(shown, hidden, area.Width, area.Height, MinimapOnLeft(S.Settings.GameCfgPath));
            if (box is not var (x, y, side))
                return (null, "Couldn't spot the minimap. Make sure it's visible in the replay, or use \"Set minimap area…\".");

            double top = area.Top - league.Top + y;
            return (new[] { x / (double)league.Width, top / league.Height, side / (double)league.Width, side / (double)league.Height },
                $"Minimap found ({side} px).");
        }
        catch (Exception ex)
        {
            AppSettings.LogError(ex);
            return (null, "Minimap detection failed: " + ex.Message);
        }
        finally
        {
            hideOverlayExtras(false);
            if (!wasPaused) S.Replay.SetPaused(false);
            Running = false;
        }
    }

    internal static byte[] Pixels(BitmapSource src)
    {
        var bgra = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Bgr32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var buf = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(buf, stride, 0);
        return buf;
    }

    /// <summary>
    /// The square region that changed when the minimap was hidden. Rows/columns that are mostly unchanged
    /// (decorations, the odd moving pixel) are trimmed, then the square is anchored to the screen-edge corner.
    /// </summary>
    internal static (int X, int Y, int Side)? ChangedSquare(byte[] a, byte[] b, int w, int h, bool onLeft)
    {
        var colCount = new int[w];
        var rowCount = new int[h];
        int total = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                int d = Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
                if (d <= 28) continue;
                colCount[x]++;
                rowCount[y]++;
                total++;
            }
        }
        if (total < 2000) return null;

        static (int Start, int End)? LongestRun(int[] counts, double threshold)
        {
            (int, int)? best = null;
            int start = -1;
            for (int i = 0; i <= counts.Length; i++)
            {
                bool on = i < counts.Length && counts[i] >= threshold;
                if (on && start < 0) start = i;
                if (!on && start >= 0)
                {
                    if (best is not var (bs, be) || i - 1 - start > be - bs) best = (start, i - 1);
                    start = -1;
                }
            }
            return best;
        }

        var cols = LongestRun(colCount, colCount.Max() * 0.35);
        var rows = LongestRun(rowCount, rowCount.Max() * 0.35);
        if (cols is not var (x0, x1) || rows is not var (y0, y1)) return null;
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        int side = Math.Min(bw, bh);
        if (side < h * 0.08 || side > h * 1.0) return null; // too small/large to be the minimap
        if (Math.Max(bw, bh) > side * 1.6) return null;      // not square-ish: probably not the minimap

        int sx = onLeft ? x0 : x1 + 1 - side; // anchor to the corner it sits in
        int sy = y1 + 1 - side;
        return (sx, sy, side);
    }
}
