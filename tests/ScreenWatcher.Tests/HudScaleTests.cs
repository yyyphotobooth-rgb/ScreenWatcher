using ScreenWatcher.Detection;
using ScreenWatcher.Imaging;
using Xunit.Abstractions;

namespace ScreenWatcher.Tests;

/// <summary>Gauge ring locator (HUD scale) and death detection at measured scale on samples/from-chat.</summary>
public class HudScaleTests(ITestOutputHelper output)
{
    readonly HudScaleLocator _locator = new(TestData.Config.HudScale);

    // Client rectangles inside the desktop screenshots (measured; see samples/README.md):
    // file, client x, y, w, h, expected scale.
    public static TheoryData<string, int, int, int, int, double> Measured => new()
    {
        { "live_printwindow.png", 0, 0, 1600, 1024, 1.0 },
        { "{27ADEBFE-44BC-48BA-913A-ADCC93EBA911}.png", 1, 37, 1024, 768, 1.0 },
        { "from-chat/alive_1920x1080.png", 234, 220, 1920, 1080, 1.0 },
        { "from-chat/alive_small_a.png", 164, 187, 1152, 864, 1.0 },
        { "from-chat/alive_small_b.png", 318, 341, 1280, 800, 1.0 },
        { "from-chat/alive_small_c.png", 134, 155, 1600, 1024, 1.0 },
        { "from-chat/dead_1024x768_detected.png", 9, 37, 1024, 768, 1.0 },
        { "from-chat/alive_2560x1440.png", 8, 31, 2552, 1409, 1.31 },
        { "from-chat/dead_1920x1440_missed.png", 34, 31, 1920, 1421, 1.31 },
    };

    [Theory, MemberData(nameof(Measured))]
    public void Ring_scale_is_measured(string file, int x, int y, int w, int h, double expected)
    {
        var frame = TestData.DesktopClientFrame(file, x, y, w, h);
        var m = _locator.Measure(frame);
        output.WriteLine($"{file}: found={m.Found} scale={m.Scale.X:F4}x{m.Scale.Y:F4} score={m.Score:F1} p/m={m.PeakToMean:F2} ring={m.RingWidthPx:F1}x{m.RingHeightPx:F1}");
        Assert.True(m.Found);
        Assert.InRange(m.Scale.X, expected - 0.03, expected + 0.03);
        Assert.InRange(m.Scale.Y, expected - 0.03, expected + 0.03);
    }

    // Frames with no gauge at the top-left: desktop-only screenshot, and game scenes cut so the
    // HUD is not at the client origin.
    public static TheoryData<string, int, int, int, int> NoGauge => new()
    {
        { "{1BB4016B-B041-4A52-9FE4-810346C3AC59}.png", 0, 0, 1600, 1024 },
        { "{1BB4016B-B041-4A52-9FE4-810346C3AC59}.png", 700, 300, 1280, 800 },
        { "from-chat/alive_1920x1080.png", 634, 520, 1280, 720 },
        { "from-chat/dead_1920x1440_missed.png", 434, 331, 1024, 768 },
        { "from-chat/alive_2560x1440.png", 208, 431, 1600, 900 },
        { "from-chat/alive_small_c.png", 234, 255, 1024, 768 },
        { "live3_printwindow.png", 300, 100, 1024, 768 },
        { "from-chat/missile_empty.png", 500, 300, 1024, 700 },
    };

    [Theory, MemberData(nameof(NoGauge))]
    public void Ring_not_found_without_gauge(string file, int x, int y, int w, int h)
    {
        var frame = TestData.DesktopClientFrame(file, x, y, w, h);
        var m = _locator.Measure(frame);
        output.WriteLine($"{file}@{x},{y}: found={m.Found} scale={m.Scale.X:F4}x{m.Scale.Y:F4} score={m.Score:F1} p/m={m.PeakToMean:F2}");
        Assert.False(m.Found);
    }

    // Death at measured scale. file, client x, y, w, h, dead?
    public static TheoryData<string, int, int, int, int, bool> DeathSamples => new()
    {
        { "from-chat/dead_1920x1440_missed.png", 34, 31, 1920, 1421, true },
        { "from-chat/dead_1024x768_detected.png", 9, 37, 1024, 768, true },
        { "from-chat/alive_1920x1080.png", 234, 220, 1920, 1080, false },
        { "from-chat/alive_2560x1440.png", 8, 31, 2552, 1409, false },
        { "from-chat/alive_small_a.png", 164, 187, 1152, 864, false },
        { "from-chat/alive_small_b.png", 318, 341, 1280, 800, false },
        { "from-chat/alive_small_c.png", 134, 155, 1600, 1024, false },
    };

    [Theory, MemberData(nameof(DeathSamples))]
    public void Death_detection_at_measured_scale(string file, int x, int y, int w, int h, bool dead)
    {
        var frame = TestData.DesktopClientFrame(file, x, y, w, h);
        var tracker = new HudScaleTracker(TestData.Config.HudScale);
        var scale = tracker.Update(frame);
        Assert.True(tracker.Current, "ring should be found");

        var o = new DeathFrameAnalyzer(TestData.Config.Death).Analyze(frame, scale);
        output.WriteLine($"{file} {frame.Width}x{frame.Height} {tracker.Describe()} => {o}");
        LogPatches(frame, scale);

        var detector = new DeathDetector(TestData.Config.Death);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        int events = 0;
        for (int s = 0; s <= 3; s++)
            if (detector.Process(frame, scale, t0.AddSeconds(s)) is not null) events++;

        if (dead)
        {
            Assert.True(o.GaugeDead, "gauge should be dark");
            Assert.True(o.DialogPresent, "death dialog should be present");
            Assert.Equal(1, events);
        }
        else
        {
            Assert.False(o.GaugeDead);
            Assert.False(o.DialogPresent);
            Assert.True(o.HpFilled);
            Assert.Equal(0, events);
        }
    }

    [Fact]
    public void Dead_1920x1440_is_missed_at_fixed_scale()
    {
        // Documents the reported bug: the old fixed coordinates do not see this death.
        var frame = TestData.DesktopClientFrame("from-chat/dead_1920x1440_missed.png", 34, 31, 1920, 1421);
        var o = new DeathFrameAnalyzer(TestData.Config.Death).Analyze(frame, HudScale.One);
        output.WriteLine(o.ToString());
        Assert.False(o.GaugeDead && o.DialogPresent);
    }

    [Fact]
    public void Tracker_keeps_last_good_scale_when_ring_lost_and_remeasures_on_resize()
    {
        var cfg = TestData.Config.HudScale;
        var big = TestData.DesktopClientFrame("from-chat/dead_1920x1440_missed.png", 34, 31, 1920, 1421);
        var noRingSameSize = TestData.DesktopClientFrame("from-chat/dead_1920x1440_missed.png", 434, 31, 1920, 1421);
        var noRingOtherSize = TestData.DesktopClientFrame("from-chat/alive_1920x1080.png", 634, 520, 1280, 720);
        var small = TestData.DesktopClientFrame("from-chat/alive_small_a.png", 164, 187, 1152, 864);

        var t = new HudScaleTracker(cfg);
        Assert.False(t.EverFound);
        var s1 = t.Update(big);
        Assert.True(t.Current);
        Assert.InRange(s1.X, 1.28, 1.34);

        // Same size: no re-measure, scale kept (even if the ring is hidden now).
        Assert.Equal(s1, t.Update(noRingSameSize));
        Assert.True(t.Current);

        // Size change with no ring visible: last good scale kept, flagged in the status text.
        Assert.Equal(s1, t.Update(noRingOtherSize));
        Assert.False(t.Current);
        Assert.Contains("last good", t.Describe());

        // Size change with the ring: re-measured.
        var s2 = t.Update(small);
        Assert.True(t.Current);
        Assert.InRange(s2.X, 0.97, 1.03);
        Assert.Equal($"scale {s2}", t.Describe());
    }

    void LogPatches(Frame frame, HudScale scale)
    {
        var g = TestData.Config.Death.Gauge;
        var d = TestData.Config.Death.Dialog;
        int pw = scale.SizeX(g.PatchSize), ph = scale.SizeY(g.PatchSize);
        foreach (var p in g.LeftPatches) output.WriteLine($"  L({scale.MapX(p[0])},{scale.MapY(p[1])}) {frame.MeanAround(scale.MapX(p[0]), scale.MapY(p[1]), pw, ph)}");
        foreach (var p in g.RightPatches) output.WriteLine($"  R({scale.MapX(p[0])},{scale.MapY(p[1])}) {frame.MeanAround(scale.MapX(p[0]), scale.MapY(p[1]), pw, ph)}");
        foreach (var p in d.PatchOffsetsFromCenter)
        {
            int x = frame.Width / 2 + (int)Math.Round(p[0] * scale.X), y = frame.Height / 2 + (int)Math.Round(p[1] * scale.Y);
            output.WriteLine($"  D({x},{y}) {frame.MeanAround(x, y, scale.SizeX(d.PatchWidth), scale.SizeY(d.PatchHeight))}");
        }
    }
}
