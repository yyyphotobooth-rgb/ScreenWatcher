using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Tests;

static class TestData
{
    public static readonly WatcherConfig Config =
        WatcherConfig.Load(Path.Combine(AppContext.BaseDirectory, WatcherConfig.FileName));

    public static string SamplesDir { get; } = FindSamples();

    static string FindSamples()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ScreenWatcher.slnx")))
                return Path.Combine(d.FullName, "samples");
        throw new DirectoryNotFoundException("samples/ not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Load a sample and cut out the game's client area (offsets measured; see samples/README.md).</summary>
    public static Frame ClientFrame(string file, int x, int y, int w, int h)
    {
        using var bmp = new Bitmap(Path.Combine(SamplesDir, file));
        return Frame.FromBitmap(bmp, new Rectangle(x, y, w, h));
    }

    /// <summary>A gauge-only crop placed at its client offset on a black 1024x768 client.</summary>
    public static Frame CropOnBlackClient(string file, int clientX, int clientY)
    {
        using var crop = new Bitmap(Path.Combine(SamplesDir, file));
        using var client = new Bitmap(1024, 768);
        using (var g = Graphics.FromImage(client))
        {
            g.Clear(Color.Black);
            g.DrawImageUnscaled(crop, clientX, clientY);
        }
        return Frame.FromBitmap(client, new Rectangle(0, 0, 1024, 768));
    }
}
