using System.Drawing;
using ScreenWatcher.Alerts;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using Xunit.Abstractions;
using static ScreenWatcher.Tests.HudTestData;

namespace ScreenWatcher.Tests;

/// <summary>
/// Stage 2, fuel arc. Labelled set: samples/labelled/fuel (gauge crops whose "xxx/300" text was
/// read by eye; the app itself never reads that text) plus the full-frame samples. The arc shows
/// fuel in whole segments, so a reading is a segment count and the % is the middle of its range.
/// </summary>
public class FuelTests(ITestOutputHelper output)
{
    static readonly FuelArcReader Reader = new(TestData.Config.Fuel);

    static FuelDetector NewDetector(HudReadings readings, int? threshold = null)
    {
        var options = new HudOptions();
        if (threshold is { } t) options.SetThreshold(AlertIds.FuelLow, t);
        return new FuelDetector(TestData.Config, readings, options);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.305)]
    [InlineData(1.318)]
    [InlineData(1.5)]
    public void Labelled_gauge_crops_read_the_labelled_segments_and_the_true_percent_is_in_range(double up)
    {
        int n = 0;
        double worst = 0;
        foreach (var row in Labels("fuel"))
        {
            var frame = RegionFrame(Path.Combine(LabelledDir("fuel"), row[0]), "gauge", up);
            var r = Reader.Read(frame, new HudScale(up, up));
            Assert.True(r.Ok, row[0] + " unreadable");
            Assert.Equal(int.Parse(row[3]), r.Lit);

            // The mapping (never fed the text) must bracket the true fuel % read by eye.
            double truePct = 100.0 * int.Parse(row[1]) / int.Parse(row[2]);
            var (lo, hi) = Reader.PercentRange(r.Lit);
            Assert.InRange(truePct, lo, hi);
            worst = Math.Max(worst, Math.Abs(Reader.Percent(r.Lit) - truePct));
            n++;
        }
        output.WriteLine($"scale {up}: {n}/{n} crops read; largest error of the % estimate {worst:F1} points");
        Assert.Equal(38, n);
        Assert.True(worst <= 3.5);
    }

    public static IEnumerable<object[]> FullFrames() =>
    [
        // sample, fuel text in the image (of 300), lit segments
        [Alive2560, 290, 13],        // real HUD scale ~1.30
        [Dead1920x1440, 292, 13],    // real HUD scale ~1.32
        [Dead1024, 293, 13],
        [Alive1920, 290, 13],
        [SmallA, 292, 13],
        [SmallB, 291, 13],
        [SmallC, 291, 13],
        [MissileEmpty, 289, 13],
        [GunEmpty, 213, 12],
        [Kill298, 204, 11],
        [Kill299, 202, 11],
        [Live1, 292, 13],
        [Live2, 292, 13],
        [Live3, 292, 13],
    ];

    [Theory]
    [MemberData(nameof(FullFrames))]
    public void Full_frame_samples_read_the_expected_segments(FullSample sample, int fuel, int segments)
    {
        var r = Reader.Read(sample.Load(), sample.Scale);
        output.WriteLine($"{sample}: {r}, text {fuel}/300");
        Assert.True(r.Ok);
        Assert.Equal(segments, r.Lit);
        var (lo, hi) = Reader.PercentRange(r.Lit);
        Assert.InRange(100.0 * fuel / 300, lo, hi);
    }

    [Fact]
    public void Owner_crop_179_of_300_reads_ten_segments()
    {
        // The crop starts one pixel right of the client edge (ring-aligned), so it goes at x = -1.
        var frame = TestData.CropOnBlackClient("from-chat/crop_fuel_179of300.png", -1, 0);
        var r = Reader.Read(frame, HudScale.One);
        Assert.True(r.Ok);
        Assert.Equal(10, r.Lit);
        Assert.InRange(100.0 * 179 / 300, Reader.PercentRange(10).Low, Reader.PercentRange(10).High);
    }

    [Fact]
    public void No_gauge_in_the_frame_is_unreadable()
    {
        // Full desktop without the game, the centre dialog region, and a plain orange/brown fill
        // (scenery colours close to the arc's own).
        Assert.False(Reader.Read(TestData.ClientFrame("{1BB4016B-B041-4A52-9FE4-810346C3AC59}.png", 0, 0, 1024, 768), HudScale.One).Ok);
        Assert.False(Reader.Read(TestData.ClientFrame("{1BB4016B-B041-4A52-9FE4-810346C3AC59}.png", 700, 300, 1024, 768), HudScale.One).Ok);
        using var bmp = new Bitmap(1024, 768);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(230, 107, 55)); // lava, measured in a dead sample
        var lava = ScreenWatcher.Imaging.Frame.FromBitmap(bmp, new Rectangle(0, 0, 1024, 768));
        Assert.False(Reader.Read(lava, HudScale.One).Ok);
    }

    [Fact]
    public void Segment_ranges_cover_0_to_100_without_gaps()
    {
        double prev = 0;
        for (int n = 0; n <= 13; n++)
        {
            var (lo, hi) = Reader.PercentRange(n);
            Assert.Equal(prev, lo, 6);
            Assert.True(hi > lo);
            Assert.InRange(Reader.Percent(n), (int)Math.Floor(lo), (int)Math.Ceiling(hi));
            prev = hi;
        }
        Assert.Equal(100, prev);
        Assert.Equal(3, Reader.Percent(0));
        Assert.Equal(9, Reader.Percent(1));
        Assert.Equal(15, Reader.Percent(2));
    }

    [Fact]
    public void Alert_fires_once_at_the_threshold_and_rearms_only_clearly_above_it()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings); // default threshold 10 %
        Assert.Equal(3, TestData.Config.Fuel.StablePolls);

        foreach (int lit in new[] { 4, 4, 4, 3, 3, 3, 2, 2, 2 }) Assert.Null(d.Update(lit));
        Assert.Equal(new RowReading("~15%", false), readings.Get(AlertIds.FuelLow));

        Assert.Null(d.Update(1));
        Assert.Null(d.Update(1));
        Assert.Equal("เชื้อเพลิงใกล้หมด เหลือ ~9%", d.Update(1));     // third stable poll at ~9 % <= 10 %
        Assert.Equal(new RowReading("~9%", true), readings.Get(AlertIds.FuelLow));
        for (int i = 0; i < 5; i++) Assert.Null(d.Update(1));
        foreach (int lit in new[] { 0, 0, 0, 0 }) Assert.Null(d.Update(lit)); // lower still: no second alert
        Assert.Equal(new RowReading("~3%", true), readings.Get(AlertIds.FuelLow));

        // ~15 % is above 10 % but not by the 6-point margin: still not re-armed.
        foreach (int lit in new[] { 2, 2, 2, 1, 1, 1 }) Assert.Null(d.Update(lit));
        // Refuelled (~21 % >= 16 %): re-armed, fires again on the way down.
        foreach (int lit in new[] { 3, 3, 3 }) Assert.Null(d.Update(lit));
        Assert.False(readings.Get(AlertIds.FuelLow).Reached);
        Assert.Null(d.Update(1));
        Assert.Null(d.Update(1));
        Assert.NotNull(d.Update(1));
    }

    [Fact]
    public void Threshold_comes_from_the_row_and_unstable_or_unreadable_polls_never_alert()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings, threshold: 30);
        Assert.Null(d.Update(5));
        Assert.Null(d.Update(5));
        Assert.Null(d.Update(5));                       // ~32 % > 30 %
        Assert.Null(d.Update(4));
        Assert.Null(d.Update(5));                       // flicker: not 3 in a row
        Assert.Null(d.Update(4));
        Assert.Null(d.Update(4));
        Assert.Equal("เชื้อเพลิงใกล้หมด เหลือ ~26%", d.Update(4));

        var d2 = NewDetector(readings);
        foreach (int? lit in new int?[] { 0, 0, null, 0, 0, null, null, null }) Assert.Null(d2.Update(lit));
        Assert.Equal(RowReading.Unknown, readings.Get(AlertIds.FuelLow).Text);
        d2.Reset();
        Assert.Equal(RowReading.None, readings.Get(AlertIds.FuelLow).Text);
    }

    [Fact]
    public void Full_arc_shows_as_more_than_and_the_alert_carries_the_gauge_crop()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings, threshold: 100);
        var frame = Dead1024.Load();
        var events = new List<WatchEvent>();
        var t = new DateTime(2026, 11, 18, 20, 0, 0);
        for (int i = 0; i < 3; i++) d.Process(frame, HudScale.One, t.AddSeconds(i), events);
        Assert.Equal(">75%", readings.Get(AlertIds.FuelLow).Text);
        var e = Assert.Single(events);                  // threshold 100 %: everything is "low"
        Assert.Equal(AlertIds.FuelLow, e.AlertId);
        Assert.Equal(new Rectangle(0, 24, 192, 164), e.Crop);
        using var img = new Bitmap(new MemoryStream(new AlertComposer(TestData.Config).Compose(e, frame, HudScale.One).Png!));
        Assert.Equal(new Size(192, 164), img.Size);
    }
}
