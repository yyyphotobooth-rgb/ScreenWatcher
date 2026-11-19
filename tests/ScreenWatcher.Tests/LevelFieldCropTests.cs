using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.ExpTest;
using ScreenWatcher.Imaging;
using Xunit.Abstractions;
using static ScreenWatcher.Tests.HudTestData;

namespace ScreenWatcher.Tests;

/// <summary>
/// /exp refused on 2026-11-19 18:31:10 and 18:31:29 while "Lv.89 48.25%" was on screen (1024x768,
/// HUD scale 1.0). Lossless level crops saved by the owner's collect mode that day
/// (samples/field-2026-11-19/level-crops, put back on a black 1024x768 client). Causes:
/// - a "4" before 0/2/4/6/8 loses its crossbar's last pixel (the next glyph's outline), so every
///   percentage 40.00..48.99 was unreadable and a test could not start;
/// - a stray light pixel of the exp bar's top edge in the blank column between two glyphs, and
///   that edge drawn as a whole light-grey line, broke the level line.
/// </summary>
public class LevelFieldCropTests(ITestOutputHelper output)
{
    static readonly WatcherConfig C = TestData.Config;
    static readonly DateTime T0 = new(2026, 11, 19, 18, 31, 0);
    static string Dir => Path.Combine(TestData.SamplesDir, "field-2026-11-19", "level-crops");
    static Frame Crop(string name) => RegionFrame(Path.Combine(Dir, name), "level");

    /// <summary>The refusal window 18:28..18:35, every value read by eye on 4x zoomed crops.</summary>
    public static IEnumerable<object[]> RefusalWindow() =>
        new (string File, int Pct)[]
        {
            ("182857_733_level_change.png", 4740), ("182859_734_level_change.png", 4744), ("182904_719_level_change.png", 4746),
            ("182915_727_level_change.png", 4753), ("183056_729_level_change.png", 4805), ("183106_731_level_change.png", 4812),
            ("183110_731_level_change.png", 4816), ("183125_730_level_change.png", 4822), ("183131_733_level_change.png", 4825),
            ("183147_720_level_change.png", 4832), ("183153_725_level_change.png", 4835), ("183205_732_level_change.png", 4841),
            ("183210_729_level_change.png", 4845), ("183215_722_level_change.png", 4848), ("183221_725_level_change.png", 4852),
            ("183227_729_level_change.png", 4855), ("183242_722_level_change.png", 4861), ("183250_729_level_change.png", 4865),
            ("183306_719_level_change.png", 4871), ("183310_729_level_change.png", 4874), ("183318_721_level_change.png", 4878),
            ("183324_720_level_change.png", 4881), ("183336_723_level_change.png", 4884), ("183342_732_level_change.png", 4888),
            ("183351_732_level_change.png", 4891), ("183357_726_level_change.png", 4895), ("183405_729_level_change.png", 4901),
            ("183411_732_level_change.png", 4905), ("183540_719_level_change.png", 4944), ("183543_731_level_change.png", 4947),
            ("183548_725_level_change.png", 4950),
        }.Select(x => new object[] { x.File, x.Pct });

    [Theory]
    [MemberData(nameof(RefusalWindow))]
    public void Refusal_window_crops_read_level_and_percentage_exactly(string file, int pct) =>
        Assert.Equal(new LevelLine(89, pct), new LevelReader(C.Level).ReadLine(Crop(file), HudScale.One));

    [Fact]
    public void Whole_day_never_a_wrong_number_readings_only_rise_and_almost_all_read()
    {
        // All 231 level crops of 2026-11-19 (Lv.89, the level-up to 90 at 23:23:49 as the journal's
        // level-up alert shows; exp only rising): a wrong digit would show as a drop. The 11 left
        // unreadable have scenery stacked on a glyph or no line.
        var reader = new LevelReader(C.Level);
        int read = 0, prev = -1;
        string prevName = "";
        foreach (var f in Directory.GetFiles(Dir, "*.png").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(f);
            var line = reader.ReadLine(Crop(name), HudScale.One);
            output.WriteLine($"{name}: {line?.ToString() ?? "null"}");
            if (line is { } l) Assert.Equal(string.CompareOrdinal(name, "232349") < 0 ? 89 : 90, l.Level);
            if (line is not { PctHundredths: { } p } ll) continue;
            read++;
            int v = ll.Level * 10000 + p;
            Assert.True(v >= prev, $"{name} {ll} after {prevName}");
            (prev, prevName) = (v, name);
        }
        Assert.Equal(231, Directory.GetFiles(Dir, "*.png").Length);
        Assert.True(read >= 220, $"only {read} of 231 read");
    }

    [Fact]
    public void A_4_before_0_2_4_6_8_is_read_without_its_last_crossbar_pixel_but_only_with_the_one_blank_column()
    {
        // 18:31:31 "48.25%": the 4 is 4 columns wide (area columns 128..131), one blank column, then the 8.
        var frame = Crop("183131_733_level_change.png");
        var reader = new LevelReader(C.Level);
        Assert.Equal(4825, reader.ReadPercent(frame, HudScale.One, 89));
        // One more blank column before the 8: that is not the painted-over pixel, the cut 4 does not count.
        var area = reader.Area(1024, 768, HudScale.One);
        Assert.Null(reader.ReadPercent(InsertBlankColumn(frame, area, area.X + 132), HudScale.One, 89));
    }

    /// <summary>Copy of the frame with a black column inserted at <paramref name="column"/> (absolute
    /// x) inside the area; the columns after it move one right.</summary>
    static Frame InsertBlankColumn(Frame f, System.Drawing.Rectangle area, int column)
    {
        var px = f.Pixels.ToArray();
        for (int y = area.Y; y < area.Bottom; y++)
        {
            for (int x = area.Right - 1; x > column; x--)
                Array.Copy(px, (y * f.Width + x - 1) * 4, px, (y * f.Width + x) * 4, 4);
            px[(y * f.Width + column) * 4] = px[(y * f.Width + column) * 4 + 1] = px[(y * f.Width + column) * 4 + 2] = 0;
        }
        var g = new Frame();
        var h = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { g.CopyFromBgra(h.AddrOfPinnedObject(), f.Width * 4, f.Width, f.Height); }
        finally { h.Free(); }
        return g;
    }

    [Fact]
    public async Task Exp_test_starts_on_the_refusal_window_frames()
    {
        // Same path as the Discord command: LevelDetector reads the level, the runner the percentage.
        var readings = new HudReadings();
        var level = new LevelDetector(C, readings, new HudOptions());
        var runner = new ExpTestRunner(C, level.Reader);
        var t = runner.RequestStart(10, ExpTestService.SourceDiscord, T0);
        var events = new List<WatchEvent>();
        var now = T0;
        foreach (var f in new[] { "183110_731_level_change.png", "183125_730_level_change.png" })
        {
            now = now.AddSeconds(1);
            var frame = Crop(f);
            level.Process(frame, HudScale.One, now, events);
            runner.OnFrame(frame, HudScale.One, now, level.LastLevel, KillObservation.Gone, false);
        }
        Assert.True(t.IsCompleted, "no stable reading on two consecutive frames");
        var o = await t;
        Assert.Equal(ExpStartStatus.Started, o.Status);
        Assert.Equal("Lv.89 48.22%", o.Start!.Text);
        runner.Cancel();
    }

    [Fact]
    public void Exp_stall_row_counts_and_alerts_on_a_frame_stuck_at_48_25()
    {
        // The stall row reads the same line: before the fix 48.25 % was unreadable, so nothing was
        // counted and a stall in 40..48.99 % could never alert.
        var readings = new HudReadings();
        var options = new HudOptions();
        var level = new LevelDetector(C, readings, options);
        var stall = new ExpStallDetector(C, readings, options, () => level.LastLine);
        var frame = Crop("183131_733_level_change.png");
        var events = new List<WatchEvent>();
        var now = T0;
        for (int i = 0; i < stall.LimitMinutes * 60 + 10; i++)
        {
            now = now.AddSeconds(1);
            level.Process(frame, HudScale.One, now, events);
            stall.Process(frame, HudScale.One, now, events);
        }
        Assert.Equal("Lv.89 · 48.25%", readings.Get(AlertIds.LevelUp).Text);
        Assert.Single(events, e => e.AlertId == AlertIds.ExpStall);
    }
}
