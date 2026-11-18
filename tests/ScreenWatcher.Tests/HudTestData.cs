using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ScreenWatcher.Collection;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Tests;

/// <summary>Shared pieces for the HUD reader tests: glyph fonts, labelled crops placed on a
/// client, and the full-frame samples with their measured client offsets.</summary>
public static class HudTestData
{
    public static readonly IReadOnlyDictionary<string, GlyphSet> Glyphs =
        GlyphSet.LoadAll(Path.Combine(AppContext.BaseDirectory, GlyphSet.FileName));

    public static string LabelledDir(string set) => Path.Combine(TestData.SamplesDir, "labelled", set);

    /// <summary>Rows of samples/labelled/&lt;set&gt;/labels.csv (header skipped), split on commas.</summary>
    public static List<string[]> Labels(string set) =>
        File.ReadAllLines(Path.Combine(LabelledDir(set), "labels.csv")).Skip(1)
            .Where(l => l.Trim().Length > 0).Select(l => l.Split(',')).ToList();

    /// <summary>
    /// A collect-region crop put back where that region sits on an otherwise black client.
    /// <paramref name="up"/> = 1: pixels unchanged on a 1024x768 client. Otherwise the crop is
    /// stretched bilinearly by that factor onto a client that much larger: a SYNTHETIC stand-in
    /// for the game's own HUD stretch at large windows (real ~1.3 frames are tested separately).
    /// </summary>
    public static Frame RegionFrame(string cropPath, string region, double up = 1)
    {
        var rc = new CollectRegion(TestData.Config.Collect.Regions.First(r => r.Name == region));
        int w = (int)Math.Round(1024 * up), h = (int)Math.Round(768 * up);
        var rect = rc.Locate(w, h, new HudScale(up, up));
        using var crop = new Bitmap(cropPath);
        using var client = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(client))
        {
            g.Clear(Color.Black);
            if (up == 1)
                g.DrawImage(crop, new Rectangle(rect.X, rect.Y, crop.Width, crop.Height), new Rectangle(0, 0, crop.Width, crop.Height), GraphicsUnit.Pixel);
            else
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using var ia = new ImageAttributes();
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(crop, new Rectangle(rect.X, rect.Y, rect.Width, rect.Height), 0, 0, crop.Width, crop.Height, GraphicsUnit.Pixel, ia);
            }
        }
        return Frame.FromBitmap(client, new Rectangle(0, 0, w, h));
    }

    /// <summary>A full-frame sample: file (under samples/), client rectangle inside the image,
    /// and the HUD scale measured for it (samples/README.md).</summary>
    public sealed record FullSample(string File, int X, int Y, int W, int H, double ScaleX = 1, double ScaleY = 1)
    {
        public Frame Load() => TestData.DesktopClientFrame(File, X, Y, W, H);
        public HudScale Scale => new(ScaleX, ScaleY);
        public override string ToString() => Path.GetFileNameWithoutExtension(File);
    }

    public static readonly FullSample Alive2560 = new("from-chat/alive_2560x1440.png", 8, 31, 2552, 1409, 1.305, 1.300);
    public static readonly FullSample Dead1920x1440 = new("from-chat/dead_1920x1440_missed.png", 34, 31, 1920, 1421, 1.315, 1.318);
    public static readonly FullSample Dead1024 = new("from-chat/dead_1024x768_detected.png", 9, 37, 1024, 768);
    public static readonly FullSample Alive1920 = new("from-chat/alive_1920x1080.png", 234, 220, 1920, 1080, 0.990, 0.988);
    public static readonly FullSample SmallA = new("from-chat/alive_small_a.png", 164, 187, 1152, 864, 0.993, 0.990);
    public static readonly FullSample SmallB = new("from-chat/alive_small_b.png", 318, 341, 1280, 800, 0.993, 0.988);
    public static readonly FullSample SmallC = new("from-chat/alive_small_c.png", 134, 155, 1600, 1024, 0.990, 0.988);
    public static readonly FullSample MissileEmpty = new("from-chat/missile_empty.png", 3, 34, 1600, 1024);
    public static readonly FullSample GunEmpty = new("from-chat/gun_empty.png", 10, 41, 1024, 768);
    public static readonly FullSample Kill298 = new("from-chat/killcounter_298.png", 10, 40, 1024, 768);
    public static readonly FullSample Kill299 = new("from-chat/killcounter_299.png", 10, 41, 1024, 768);
    public static readonly FullSample Live1 = new("live_printwindow.png", 0, 0, 1600, 1024);
    public static readonly FullSample Live2 = new("live2_printwindow.png", 0, 0, 1600, 1024);
    public static readonly FullSample Live3 = new("live3_printwindow.png", 0, 0, 1600, 1024);
}
