using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RiftPen;

/// <summary>
/// Summoner's Rift map art for the Rift Board: the game's own minimap terrain (from CommunityDragon, which mirrors the
/// current patch's files), including the elemental rift versions the map turns into after the second drake. The
/// 512-pixel original is redrawn at 2048 pixels with smooth, sharp wall edges so it stays crisp when zoomed in, and the
/// walls get the dark colour League's minimap uses. Results are cached on disk.
/// </summary>
public static class MapArt
{
    const string Cdragon = "https://raw.communitydragon.org/latest/game/assets/maps/info/map11/2dlevelminimap_{0}_baron1.png";
    const int Out = 2048;
    /// <summary>Bump when the processing changes, so cached images are redrawn.</summary>
    const int Revision = 2;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    static string Folder => Path.Combine(AppSettings.Folder, "maps");

    /// <summary>Wall colour of League's minimap.</summary>
    static readonly (byte R, byte G, byte B) Wall = (17, 20, 19);

    /// <summary>The rift's look at <paramref name="t"/>: "base", or the element it turned into.</summary>
    public static string VariantAt(GameTimeline g, double t)
    {
        var drakes = g.Events.Where(e => e.Type == "ELITE_MONSTER_KILL" && e.MonsterType == "DRAGON" && e.MonsterSubType != "ELDER_DRAGON")
                             .OrderBy(e => e.Time).ToList();
        // The map changes when the second drake dies, into the element of the third (the soul element).
        if (drakes.Count < 3 || t < drakes[1].Time) return "base";
        return drakes[2].MonsterSubType switch
        {
            "FIRE_DRAGON" => "infernal",
            "WATER_DRAGON" => "ocean",
            "EARTH_DRAGON" => "mountain",
            "AIR_DRAGON" => "cloud",
            "HEXTECH_DRAGON" => "hextech",
            _ => "base",
        };
    }

    /// <summary>Path of the processed image for a variant, downloading and redrawing it the first time.</summary>
    public static async Task<string?> GetAsync(string variant)
    {
        Directory.CreateDirectory(Folder);
        var done = Path.Combine(Folder, $"map11_{variant}_r{Revision}.png");
        if (File.Exists(done)) return done;

        byte[]? source = null;
        try { source = await Http.GetByteArrayAsync(string.Format(Cdragon, variant)); }
        catch (Exception ex) { AppSettings.LogError(ex); }
        if (source == null && variant == "base")
        {
            // Offline: Data Dragon's copy of the same terrain.
            var dd = await DataDragon.MapImageAsync();
            if (dd != null) source = await File.ReadAllBytesAsync(dd);
        }
        if (source == null) return null;

        var png = await Task.Run(() => Redraw(source));
        var tmp = done + ".tmp";
        await File.WriteAllBytesAsync(tmp, png);
        File.Move(tmp, done, overwrite: true);
        return done;
    }

    /// <summary>
    /// Upscales the minimap 4× treating walls as a shape (smooth Catmull-Rom edges, anti-aliased) and the ground as
    /// colour (blended only from ground pixels, so walls don't bleed into it).
    /// </summary>
    public static byte[] Redraw(byte[] source)
    {
        var dec = BitmapDecoder.Create(new MemoryStream(source), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var src = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);

        // Wall mask: transparent, white (CommunityDragon) or black (Data Dragon) pixels.
        var wall = new float[w * h];
        for (int i = 0; i < w * h; i++)
        {
            byte b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2], a = px[i * 4 + 3];
            bool isWall = a < 128 || (r > 232 && g > 232 && b > 232) || (r < 24 && g < 24 && b < 24);
            wall[i] = isWall ? 1 : 0;
        }

        // Soften the pixel staircase of the original before upscaling, so diagonal and curved walls come out smooth.
        var shape = (float[])wall.Clone();
        for (int pass = 0; pass < 2; pass++) shape = Blur(shape, w, h);

        int ow = Out, oh = Out * h / w;
        var dst = new byte[ow * oh * 4];
        double sx = (double)w / ow, sy = (double)h / oh;
        Parallel.For(0, oh, y =>
        {
            double fy = (y + 0.5) * sy - 0.5;
            int y0 = (int)Math.Floor(fy);
            double ty = fy - y0;
            for (int x = 0; x < ow; x++)
            {
                double fx = (x + 0.5) * sx - 0.5;
                int x0 = (int)Math.Floor(fx);
                double tx = fx - x0;

                // Wall coverage: bicubic, then a narrow ramp for a crisp anti-aliased edge.
                double m = 0;
                for (int j = -1; j <= 2; j++)
                {
                    double wy = CatmullRom(j - ty);
                    int yy = Math.Clamp(y0 + j, 0, h - 1);
                    for (int i = -1; i <= 2; i++)
                        m += wy * CatmullRom(i - tx) * shape[yy * w + Math.Clamp(x0 + i, 0, w - 1)];
                }
                double cover = Math.Clamp((m - 0.5) / 0.09 + 0.5, 0, 1);

                // Ground colour: bilinear over ground pixels only (widened to 4×4 near walls).
                double cb = 0, cg = 0, cr = 0, cw = 0;
                for (int pass = 0; pass < 2 && cw == 0; pass++)
                {
                    int lo = pass == 0 ? 0 : -1, hi = pass == 0 ? 1 : 2;
                    for (int j = lo; j <= hi; j++)
                        for (int i = lo; i <= hi; i++)
                        {
                            int xx = Math.Clamp(x0 + i, 0, w - 1), yy = Math.Clamp(y0 + j, 0, h - 1), k = yy * w + xx;
                            if (wall[k] > 0) continue;
                            double wt = Math.Max(0.05, 1 - Math.Abs(i - tx)) * Math.Max(0.05, 1 - Math.Abs(j - ty));
                            cb += px[k * 4] * wt; cg += px[k * 4 + 1] * wt; cr += px[k * 4 + 2] * wt; cw += wt;
                        }
                }
                if (cw == 0) { cb = Wall.B; cg = Wall.G; cr = Wall.R; cw = 1; }
                cb /= cw; cg /= cw; cr /= cw;

                int o = (y * ow + x) * 4;
                dst[o] = (byte)Math.Round(cb + (Wall.B - cb) * cover);
                dst[o + 1] = (byte)Math.Round(cg + (Wall.G - cg) * cover);
                dst[o + 2] = (byte)Math.Round(cr + (Wall.R - cr) * cover);
                dst[o + 3] = 255;
            }
        });

        var bmp = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Bgra32, null, dst, ow * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A [1 2 1] blur in both directions.</summary>
    static float[] Blur(float[] a, int w, int h)
    {
        var tmp = new float[a.Length];
        var res = new float[a.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                tmp[y * w + x] = (a[y * w + Math.Max(0, x - 1)] + 2 * a[y * w + x] + a[y * w + Math.Min(w - 1, x + 1)]) / 4;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                res[y * w + x] = (tmp[Math.Max(0, y - 1) * w + x] + 2 * tmp[y * w + x] + tmp[Math.Min(h - 1, y + 1) * w + x]) / 4;
        return res;
    }

    static double CatmullRom(double x)
    {
        x = Math.Abs(x);
        if (x < 1) return 1.5 * x * x * x - 2.5 * x * x + 1;
        if (x < 2) return -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2;
        return 0;
    }
}
