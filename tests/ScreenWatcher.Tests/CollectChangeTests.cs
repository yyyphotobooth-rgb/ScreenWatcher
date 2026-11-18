using System.Drawing;
using ScreenWatcher.Collection;
using ScreenWatcher.Detection;
using ScreenWatcher.Imaging;
using Xunit.Abstractions;

namespace ScreenWatcher.Tests;

/// <summary>
/// The collect-mode change test (HUD colour keys, <see cref="HudSignature"/>) on crops from
/// samples/ and on crops saved by the collect mode in a live session (samples/live-collect).
/// Every pair was checked by eye (samples/README.md, "Collect regions").
/// </summary>
public class CollectChangeTests(ITestOutputHelper output)
{
    static readonly Dictionary<string, (string File, int X, int Y, int W, int H)> Clients = new()
    {
        ["live1"] = ("live_printwindow.png", 0, 0, 1600, 1024),
        ["live2"] = ("live2_printwindow.png", 0, 0, 1600, 1024),
        ["live3"] = ("live3_printwindow.png", 0, 0, 1600, 1024),
        ["dead27"] = ("{27ADEBFE-44BC-48BA-913A-ADCC93EBA911}.png", 1, 37, 1024, 768),
        ["deadA8"] = ("{A8F5B88E-6719-4E06-945C-9B09AE522E1B}.png", 2, 40, 1024, 768),
        ["dead1024"] = ("from-chat/dead_1024x768_detected.png", 9, 37, 1024, 768),
        ["smallB"] = ("from-chat/alive_small_b.png", 318, 341, 1280, 800),
        ["smallC"] = ("from-chat/alive_small_c.png", 134, 155, 1600, 1024),
        ["missileEmpty"] = ("from-chat/missile_empty.png", 3, 34, 1600, 1024),
    };

    static readonly Dictionary<string, Frame> Cache = [];

    static Frame Load(string key)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var f)) return f;
            var c = Clients[key];
            return Cache[key] = TestData.DesktopClientFrame(c.File, c.X, c.Y, c.W, c.H);
        }
    }

    static CollectRegion Region(string name) => new(TestData.Config.Collect.Regions.Single(r => r.Name == name));

    static int Threshold(string region) =>
        HudSignature.Threshold(TestData.Config.Collect.Change, HudScale.One, Region(region).MinChangedPixels);

    /// <summary>Region of a sample client. These samples are all at HUD scale ~1.0 (measured
    /// 0.99..1.00), so the region is located at exactly 1.0 to compare the same pixels.</summary>
    static HudSignature SampleSignature(string key, string region)
    {
        var frame = Load(key);
        var r = Region(region);
        var s = new HudSignature();
        s.Compute(frame, r.Locate(frame.Width, frame.Height, HudScale.One), r.Keys);
        return s;
    }

    /// <summary>A crop saved by the collect mode (the whole image is the region).</summary>
    static HudSignature CropSignature(string path, string region)
    {
        using var bmp = new Bitmap(Path.Combine(TestData.SamplesDir, "live-collect", path));
        var frame = Frame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var s = new HudSignature();
        s.Compute(frame, new Rectangle(0, 0, bmp.Width, bmp.Height), Region(region).Keys);
        return s;
    }

    int Count(string region, HudSignature saved, HudSignature now, string label)
    {
        int n = HudSignature.CountChanged(now, saved, TestData.Config.Collect.Change.MaxShift);
        output.WriteLine($"{region}: {label}: {n} changed px (threshold {Threshold(region)})");
        return n;
    }

    // ---- samples/: same HUD content over different scenery ----

    [Theory]
    [InlineData("ammo", "smallB", "smallC")]   // 07356 / 00302; clouds differ
    [InlineData("ammo", "dead27", "deadA8")]   // 00517 / 00906; lava moved
    [InlineData("kill", "dead27", "deadA8")]   // 133; lava and a dark streak behind the panel
    [InlineData("items", "dead27", "deadA8")]  // 291 570 48 13 5
    [InlineData("dialog", "dead27", "deadA8")] // same death dialog, explosion behind changed
    public void Same_hud_over_different_scene_is_no_change(string region, string a, string b)
    {
        int n = Count(region, SampleSignature(a, region), SampleSignature(b, region), $"{a} -> {b}");
        Assert.InRange(n, 0, Threshold(region) - 1);
    }

    // ---- samples/: different HUD content ----

    [Theory]
    [InlineData("ammo", "live2", "live3")]         // machine gun 09008 -> 09003 (one digit)
    [InlineData("ammo", "live1", "live2")]         // 09125/01188 -> 09008/01138
    [InlineData("ammo", "smallC", "missileEmpty")] // missile counter turns red 00000
    [InlineData("kill", "live1", "live2")]         // 232 -> 235
    [InlineData("kill", "live2", "live3")]         // 235 -> 236 (one stroke)
    [InlineData("kill", "live1", "dead1024")]      // panel present -> absent
    [InlineData("items", "smallB", "smallC")]      // count 47 -> 46
    [InlineData("gauge", "smallB", "smallC")]      // 198 -> 206, 2269 -> 2344
    [InlineData("gauge", "live2", "live3")]        // right half drained 2503 -> 1744
    [InlineData("dialog", "live1", "dead27")]      // death dialog absent -> present
    public void Different_hud_content_is_a_change(string region, string a, string b)
    {
        int n = Count(region, SampleSignature(a, region), SampleSignature(b, region), $"{a} -> {b}");
        Assert.True(n >= Threshold(region), $"{n} changed px");
    }

    // ---- live-collect: kill counter, 64 crops saved over ~3 min of combat ----

    /// <summary>The number shown in each crop of samples/live-collect/kill, in file-name order
    /// (read by eye). Backgrounds: sky, rock, grass, sea, lava (index 35), dark.</summary>
    static readonly int[] KillValues =
    [
        44, 44, 44, 44, 45, 45, 46, 46, 46, 46, 47, 47, 47, 48, 48, 48, 48, 49, 49, 49, 50, 50, 51, 51,
        52, 52, 52, 53, 54, 54, 55, 55, 56, 56, 56, 57, 58, 58, 58, 58, 58, 59, 59, 59, 59, 59, 59, 59,
        60, 60, 60, 61, 61, 61, 62, 62, 62, 62, 62, 62, 62, 63, 64, 64,
    ];

    [Fact]
    public void Live_kill_counter_changes_exactly_when_the_number_changes()
    {
        var files = Directory.GetFiles(Path.Combine(TestData.SamplesDir, "live-collect", "kill"), "*.png")
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(KillValues.Length, files.Length);
        var sigs = files.Select(f => CropSignature(Path.Combine("kill", f!), "kill")).ToArray();
        int threshold = Threshold("kill"), maxSame = 0, minDiff = int.MaxValue, pairs = 0;
        for (int i = 0; i < sigs.Length; i++)
            for (int j = i + 1; j < sigs.Length; j++, pairs++)
            {
                int d = HudSignature.CountChanged(sigs[j], sigs[i], TestData.Config.Collect.Change.MaxShift);
                if (KillValues[i] == KillValues[j]) maxSame = Math.Max(maxSame, d);
                else minDiff = Math.Min(minDiff, d);
            }
        output.WriteLine($"{pairs} pairs: same number max {maxSame} px, different number min {minDiff} px, threshold {threshold}");
        Assert.True(maxSame < threshold, $"same number differs by {maxSame} px");
        Assert.True(minDiff >= threshold, $"different numbers differ by only {minDiff} px");
    }

    // ---- live-collect: item bar and dialog ----

    [Theory]
    [InlineData("items", "172018_649_items_change.png", "172020_656_items_change.png", false)] // 229/334 both; scenery differs
    [InlineData("items", "172018_649_items_change.png", "172022_661_items_change.png", false)] // 229/334 both
    [InlineData("items", "172005_657_items_change.png", "172008_652_items_change.png", true)]  // 230/336 -> 229/335
    [InlineData("items", "172015_655_items_change.png", "172018_649_items_change.png", true)]  // 335 -> 334
    [InlineData("dialog", "172033_657_dialog_change.png", "172036_655_dialog_change.png", false)] // no dialog; ships, labels, explosion
    [InlineData("dialog", "172241_657_dialog_change.png", "172243_660_dialog_change.png", true)]  // death dialog appears over lava
    public void Live_crops(string region, string a, string b, bool changed)
    {
        int n = Count(region, CropSignature(Path.Combine(region, a), region), CropSignature(Path.Combine(region, b), region), $"{a} -> {b}");
        if (changed) Assert.True(n >= Threshold(region), $"{n} changed px");
        else Assert.InRange(n, 0, Threshold(region) - 1);
    }

    [Fact]
    public void Identical_crop_is_no_change_and_size_mismatch_is_a_change()
    {
        var cfg = TestData.Config.Collect.Change;
        var a = SampleSignature("live1", "ammo");
        Assert.Equal(0, HudSignature.CountChanged(a, SampleSignature("live1", "ammo"), cfg.MaxShift));
        Assert.False(HudSignature.Changed(a, SampleSignature("live1", "ammo"), cfg, HudScale.One));
        Assert.True(HudSignature.Changed(a, SampleSignature("live1", "gauge"), cfg, HudScale.One));
        Assert.True(HudSignature.Changed(a, new HudSignature(), cfg, HudScale.One)); // nothing saved yet
    }
}
