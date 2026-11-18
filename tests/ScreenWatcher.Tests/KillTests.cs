using System.Drawing;
using ScreenWatcher.Alerts;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using Xunit.Abstractions;
using static ScreenWatcher.Tests.HudTestData;

namespace ScreenWatcher.Tests;

/// <summary>
/// Stage 3, kill counter. Labelled sets: samples/labelled/kill (195 crops from the collect mode,
/// values checked by eye) and samples/live-collect/kill (64 crops labelled in an earlier task).
/// </summary>
public class KillTests(ITestOutputHelper output)
{
    static KillDetector NewDetector(HudReadings? readings = null) => new(TestData.Config, Glyphs, readings ?? new HudReadings());

    static KillObservation N(int v) => KillObservation.Of(v);
    static readonly KillObservation Gone = KillObservation.Gone, Bad = KillObservation.Unreadable;

    /// <summary>Feeds the polls; returns the indexes where the alert fired.</summary>
    static List<int> Feed(KillDetector d, params KillObservation[] polls)
    {
        var fired = new List<int>();
        for (int i = 0; i < polls.Length; i++) if (d.Update(polls[i])) fired.Add(i);
        return fired;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.305)]
    [InlineData(1.318)]
    [InlineData(1.5)]
    public void Labelled_crops_are_read_without_a_single_wrong_value(double up)
    {
        var d = NewDetector();
        int numbers = 0, read = 0, wrong = 0, gone = 0;
        int[] digitTotal = new int[10], digitRead = new int[10];
        foreach (var row in Labels("kill"))
        {
            var r = d.ReadRaw(RegionFrame(Path.Combine(LabelledDir("kill"), row[0]), "kill", up), new HudScale(up, up));
            if (row[1] == "gone")
            {
                Assert.Equal(KillObservation.Gone, KillDetector.Observe(r));
                gone++;
                continue;
            }
            numbers++;
            foreach (char c in row[1]) digitTotal[c - '0']++;
            if (!r.Ok) continue;
            if (r.Digits == 0 || r.Value != int.Parse(row[1])) { wrong++; continue; }
            Assert.Equal(row[1].Length, r.Digits);
            read++;
            foreach (char c in row[1]) digitRead[c - '0']++;
        }
        output.WriteLine($"scale {up}: {read}/{numbers} numbers read ({100.0 * read / numbers:F1} %), {wrong} wrong, {gone} 'gone' crops; per digit " +
            string.Join(" ", Enumerable.Range(0, 10).Select(i => $"{i}:{digitRead[i]}/{digitTotal[i]}")));
        Assert.Equal(0, wrong);
        Assert.Equal(192, numbers);
        Assert.Equal(3, gone);
        // Measured 191/192 at every scale: the one miss is "62" over lava, refused as unreadable.
        Assert.True(read >= 191);
    }

    [Fact]
    public void Earlier_live_set_of_64_crops_reads_its_labels()
    {
        // Same labels as CollectChangeTests.KillValues (44 -> 64 in file-name order).
        int[] values =
        [
            44, 44, 44, 44, 45, 45, 46, 46, 46, 46, 47, 47, 47, 48, 48, 48, 48, 49, 49, 49, 50, 50, 51, 51,
            52, 52, 52, 53, 54, 54, 55, 55, 56, 56, 56, 57, 58, 58, 58, 58, 58, 59, 59, 59, 59, 59, 59, 59,
            60, 60, 60, 61, 61, 61, 62, 62, 62, 62, 62, 62, 62, 63, 64, 64,
        ];
        var files = Directory.GetFiles(Path.Combine(TestData.SamplesDir, "live-collect", "kill"), "*.png").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(values.Length, files.Length);
        var d = NewDetector();
        int read = 0;
        for (int i = 0; i < files.Length; i++)
        {
            var r = d.ReadRaw(RegionFrame(files[i], "kill"), HudScale.One);
            if (!r.Ok) continue;
            Assert.Equal(values[i], r.Value);
            read++;
        }
        Assert.True(read >= 63, $"{read}/64");
    }

    public static IEnumerable<object?[]> FullFrames() =>
    [
        [Kill298, 298],
        [Kill299, 299],
        [GunEmpty, 274],
        [MissileEmpty, 200],
        [Live1, 232],
        [Live2, 235],
        [Live3, 236],
        // No kill panel in these (map grid closed, or dead):
        [Dead1024, null],
        [Alive1920, null],
        [SmallA, null],
        [SmallB, null],
        [SmallC, null],
        [Alive2560, null],        // real HUD scale ~1.30
        [Dead1920x1440, null],    // real HUD scale ~1.32
    ];

    [Theory]
    [MemberData(nameof(FullFrames))]
    public void Full_frame_samples_read_the_number_or_no_panel(FullSample sample, int? value)
    {
        var r = NewDetector().ReadRaw(sample.Load(), sample.Scale);
        output.WriteLine($"{sample}: {r}");
        Assert.Equal(value is { } v ? KillObservation.Of(v) : KillObservation.Gone, KillDetector.Observe(r));
    }

    [Fact]
    public void Owner_crop_278_is_read()
    {
        // crop_killcounter.png is a 170x64 cut of the panel. Its place on a 1024x768 client was
        // found once by trying offsets: the digits line up with the crop at (856, 306).
        var r = NewDetector().ReadRaw(TestData.CropOnBlackClient("from-chat/crop_killcounter.png", 856, 306), HudScale.One);
        Assert.True(r.Ok);
        Assert.Equal((278, 3), (r.Value, r.Digits));
    }

    [Fact]
    public void Reading_300_fires_once()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings);
        Assert.Equal([5], Feed(d, N(297), N(297), N(298), N(299), N(299), N(300), N(300), N(300)));
        Assert.Equal(new RowReading("300", true), readings.Get(AlertIds.Kill300));
        // The panel then goes away: no second alert.
        Assert.Empty(Feed(d, Gone, Gone, Gone, Gone));
        Assert.Equal(RowReading.None, readings.Get(AlertIds.Kill300).Text);
    }

    [Fact]
    public void Panel_gone_at_295_or_more_fires_and_below_that_does_not()
    {
        var d = NewDetector();
        // 300 was never seen (it shows only briefly): 299, then no panel for two polls.
        Assert.Equal([4], Feed(d, N(298), N(299), N(299), Gone, Gone, Gone, Gone));

        var low = NewDetector();
        Assert.Empty(Feed(low, N(293), N(294), N(294), Gone, Gone, Gone, Gone));  // landed at 294
        var at295 = NewDetector();
        Assert.Equal([3], Feed(at295, N(295), N(295), Gone, Gone));
    }

    [Fact]
    public void One_poll_without_the_panel_or_unreadable_polls_do_not_fire()
    {
        var d = NewDetector();
        Assert.Empty(Feed(d, N(298), N(298), Gone, N(298), Bad, Gone, Bad, Gone, N(299), Bad, Bad, Bad, Bad));
        // Still armed: a real disappearance afterwards fires.
        Assert.Equal([2], Feed(d, N(299), Gone, Gone));
    }

    [Fact]
    public void A_single_misread_jump_is_not_trusted()
    {
        var d = NewDetector();
        // 120 -> "300" once -> 121: the 300 does not continue the count, so it is ignored;
        // and a lone high value followed by "gone" does not count as "last value >= 295".
        Assert.Empty(Feed(d, N(120), N(120), N(300), N(121), N(121), N(299), Gone, Gone, Gone));
    }

    [Fact]
    public void Rearms_when_the_panel_is_back_with_a_low_value()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings);
        Assert.Equal([3], Feed(d, N(299), N(299), Gone, Gone));
        // Back at 296..299 (not "low"): not re-armed, nothing fires.
        Assert.Empty(Feed(d, N(296), N(296), N(297), Gone, Gone, Gone));
        // New round from 0: re-armed, the next 300 fires again.
        Assert.Empty(Feed(d, N(0), N(0), N(1), N(5), N(17)));
        Assert.Equal("17", readings.Get(AlertIds.Kill300).Text);
        Assert.Equal([3], Feed(d, N(297), N(299), N(299), N(300)));
    }

    [Fact]
    public void Fast_counting_is_followed_and_the_row_shows_the_live_number()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings);
        // Changing every poll (never two equal readings) up to 299, then gone.
        Assert.Equal([8], Feed(d, N(280), N(283), N(286), N(290), N(293), N(296), N(299), Gone, Gone));
        var d2 = NewDetector(readings);
        d2.Update(N(41));
        Assert.Equal(new RowReading("41", false), readings.Get(AlertIds.Kill300));
        d2.Update(Bad); d2.Update(Bad);
        Assert.Equal("41", readings.Get(AlertIds.Kill300).Text);
        d2.Update(Bad);
        Assert.Equal(RowReading.Unknown, readings.Get(AlertIds.Kill300).Text);
        d2.Reset();
        Assert.Equal(RowReading.None, readings.Get(AlertIds.Kill300).Text);
    }

    [Fact]
    public void Real_299_frames_then_a_frame_without_the_panel_fire_with_the_Thai_text_and_panel_crop()
    {
        var d = NewDetector();
        var events = new List<WatchEvent>();
        var t = new DateTime(2026, 11, 18, 20, 0, 0);
        var f298 = Kill298.Load();
        var f299 = Kill299.Load();
        var noPanel = Dead1024.Load();
        d.Process(f298, HudScale.One, t, events);
        d.Process(f299, HudScale.One, t.AddSeconds(1), events);
        d.Process(f299, HudScale.One, t.AddSeconds(2), events);
        d.Process(noPanel, HudScale.One, t.AddSeconds(3), events);
        Assert.Empty(events);
        d.Process(noPanel, HudScale.One, t.AddSeconds(4), events);
        var e = Assert.Single(events);
        Assert.Equal("ยิงมอนครบ 300 ตัวแล้ว", e.Message);
        Assert.Equal(AlertIds.Kill300, e.AlertId);
        Assert.Equal(new Rectangle(1024 - 180, 326, 180, 40), e.Crop);
        var m = new AlertComposer(TestData.Config).Compose(e, noPanel, HudScale.One);
        using var img = new Bitmap(new MemoryStream(m.Png!));
        Assert.Equal(new Size(180, 40), img.Size);
    }
}
