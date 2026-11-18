using ScreenWatcher.Detection;
using ScreenWatcher.Imaging;
using Xunit.Abstractions;

namespace ScreenWatcher.Tests;

/// <summary>Detector A against the reference samples: every dead sample detects, every alive one does not.</summary>
public class DeathSampleTests(ITestOutputHelper output)
{
    readonly DeathFrameAnalyzer _analyzer = new(TestData.Config.Death);

    // Full window snips: file, client x, y, w, h.
    public static TheoryData<string, int, int, int, int> DeadFull => new()
    {
        { "{27ADEBFE-44BC-48BA-913A-ADCC93EBA911}.png", 1, 37, 1024, 768 },
        { "{A8F5B88E-6719-4E06-945C-9B09AE522E1B}.png", 2, 40, 1024, 768 },
    };

    // Live PrintWindow captures are already client-only.
    public static TheoryData<string> Alive => new()
    {
        "live_printwindow.png",
        "live2_printwindow.png",
        "live3_printwindow.png",
    };

    [Theory, MemberData(nameof(DeadFull))]
    public void Dead_full_sample_is_detected(string file, int x, int y, int w, int h)
    {
        var frame = TestData.ClientFrame(file, x, y, w, h);
        var o = Analyze(file, frame);

        Assert.True(o.GaugeDead, "gauge should be dark");
        Assert.True(o.DialogPresent, "death dialog should be present");
        Assert.False(o.HpFilled, "HP fill should be gone");
        Assert.Single(RunFor(frame, seconds: 3));
    }

    [Fact]
    public void Dead_gauge_crop_sample_shows_dark_gauge()
    {
        // 170x161 crop of the gauge only; its top-left is client (13,25) (measured by alignment).
        var frame = TestData.CropOnBlackClient("{331CF931-9432-42C2-81FA-C0071EF8CE7E}.png", 13, 25);
        var o = Analyze("gauge crop", frame);

        Assert.True(o.GaugeDead);
        Assert.False(o.HpFilled);
    }

    [Theory, MemberData(nameof(Alive))]
    public void Alive_sample_is_not_detected(string file)
    {
        var frame = TestData.ClientFrame(file, 0, 0, int.MaxValue / 2, int.MaxValue / 2);
        var o = Analyze(file, frame);

        Assert.False(o.GaugeDead);
        Assert.False(o.DialogPresent);
        Assert.True(o.HpFilled);
        Assert.Empty(RunFor(frame, seconds: 3));
    }

    DeathObservation Analyze(string name, Frame frame)
    {
        var g = TestData.Config.Death.Gauge;
        var d = TestData.Config.Death.Dialog;
        output.WriteLine($"{name} {frame.Width}x{frame.Height}");
        foreach (var p in g.LeftPatches) output.WriteLine($"  L({p[0]},{p[1]}) {frame.MeanAround(p[0], p[1], g.PatchSize, g.PatchSize)}");
        foreach (var p in g.RightPatches) output.WriteLine($"  R({p[0]},{p[1]}) {frame.MeanAround(p[0], p[1], g.PatchSize, g.PatchSize)}");
        foreach (var p in d.PatchOffsetsFromCenter)
            output.WriteLine($"  D({p[0]},{p[1]}) {frame.MeanAround(frame.Width / 2 + p[0], frame.Height / 2 + p[1], d.PatchWidth, d.PatchHeight)}");
        var o = _analyzer.Analyze(frame);
        output.WriteLine($"  => {o}");
        return o;
    }

    static List<WatchEvent> RunFor(Frame frame, int seconds)
    {
        var detector = new DeathDetector(TestData.Config.Death);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var events = new List<WatchEvent>();
        for (int s = 0; s <= seconds; s++)
            if (detector.Process(frame, t0.AddSeconds(s)) is { } e) events.Add(e);
        return events;
    }
}
