using System.Drawing;
using ScreenWatcher.Alerts;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;
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

    static readonly DateTime T0 = new(2026, 11, 20, 13, 0, 0);
    static readonly KillObservation Tip = KillObservation.CoveredByText;

    /// <summary>Feeds the polls one second apart (continuing the detector's own clock); returns the
    /// indexes where the alert fired.</summary>
    static List<int> Feed(KillDetector d, params KillObservation[] polls)
    {
        var fired = new List<int>();
        for (int i = 0; i < polls.Length; i++) if (d.Update(polls[i], Clock.Next(d))) fired.Add(i);
        return fired;
    }

    static KillObservation[] Times(KillObservation o, int n) => Enumerable.Repeat(o, n).ToArray();
    static KillObservation[] Seq(params object[] parts) =>
        parts.SelectMany(p => p is KillObservation o ? new[] { o } : (KillObservation[])p).ToArray();

    /// <summary>Per-detector poll clock (one second per poll).</summary>
    static class Clock
    {
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<KillDetector, StrongBox<int>> Ticks = new();
        public static DateTime Next(KillDetector d) => T0.AddSeconds(Ticks.GetOrCreateValue(d).Value++);
    }

    sealed class StrongBox<T> { public T Value = default!; }

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
            var frame = RegionFrame(Path.Combine(LabelledDir("kill"), row[0]), "kill", up);
            var r = d.ReadRaw(frame, new HudScale(up, up));
            if (row[1] == "gone")
            {
                // A true "gone" must stay gone (no tooltip text) with the frame-level check too.
                Assert.Equal(KillObservation.Gone, d.Observe(frame, new HudScale(up, up)));
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
        var d = NewDetector();
        var frame = sample.Load();
        var r = d.ReadRaw(frame, sample.Scale);
        output.WriteLine($"{sample}: {r}");
        Assert.Equal(value is { } v ? KillObservation.Of(v) : KillObservation.Gone, KillDetector.Observe(r));
        Assert.Equal(value is { } v2 ? KillObservation.Of(v2) : KillObservation.Gone, d.Observe(frame, sample.Scale));
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
    public void Reading_300_fires_once_and_the_row_then_shows_done_not_unreadable()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings);
        Assert.Equal([5], Feed(d, N(297), N(297), N(298), N(299), N(299), N(300), N(300), N(300)));
        Assert.Equal(new RowReading("300", true), readings.Get(AlertIds.Kill300));
        // The panel then goes away for good: no second alert, "ครบแล้ว" after 10 s.
        Assert.Empty(Feed(d, Times(Gone, 40)));
        Assert.Equal(KillDetector.DoneText, readings.Get(AlertIds.Kill300).Text);
        Assert.Empty(Feed(d, Times(Bad, 20)));     // unreadable scenery where the panel was: still done
        Assert.Equal(KillDetector.DoneText, readings.Get(AlertIds.Kill300).Text);
    }

    [Fact]
    public void Panel_absent_10_s_after_295_or_more_fires_and_below_that_shows_no_panel()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings);
        // 300 was never seen (it shows only briefly): 299, then no panel. Absent from poll 3: fires at 13.
        Assert.Equal([13], Feed(d, Seq(N(298), N(299), N(299), Times(Gone, 15))));
        Assert.Equal(KillDetector.DoneText, readings.Get(AlertIds.Kill300).Text);

        var lowReadings = new HudReadings();
        var low = NewDetector(lowReadings);
        Assert.Empty(Feed(low, Seq(N(293), N(294), N(294), Times(Gone, 30))));
        Assert.Equal(KillDetector.NoPanelText, lowReadings.Get(AlertIds.Kill300).Text);

        var at295 = NewDetector();
        Assert.Equal([12], Feed(at295, Seq(N(295), N(295), Times(Bad, 12))));   // unreadable scenery counts as absent
    }

    [Fact]
    public void No_card_used_shows_no_panel_and_never_fires()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings);
        Assert.Empty(Feed(d, Seq(Times(Gone, 5), Times(Bad, 5), Times(Gone, 30))));
        Assert.Equal(KillDetector.NoPanelText, readings.Get(AlertIds.Kill300).Text);
    }

    [Fact]
    public void Short_absences_do_not_fire()
    {
        var d = NewDetector();
        Assert.Empty(Feed(d, Seq(N(298), N(298), Times(Gone, 9), N(298), Times(Bad, 5), N(298), N(299), Times(Gone, 8), N(299))));
        // Still armed: a real disappearance afterwards fires.
        Assert.Equal([11], Feed(d, Seq(N(299), Times(Gone, 12))));
    }

    [Fact]
    public void Tooltip_text_restarts_the_wait_and_alone_makes_the_row_unreadable()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings);
        // 297, tooltip for 5 s, panel back: nothing, the number again.
        Assert.Empty(Feed(d, Seq(N(297), N(297), Times(Tip, 5))));
        Assert.Equal(RowReading.Unknown, readings.Get(AlertIds.Kill300).Text);
        Assert.Empty(Feed(d, N(297)));
        Assert.Equal("297", readings.Get(AlertIds.Kill300).Text);
        // 8 s absent, tooltip 3 s, 8 s absent: the wait restarted, no alert yet; 2 s more and it fires.
        Assert.Empty(Feed(d, Seq(Times(Gone, 8), Times(Tip, 3), Times(Gone, 8))));
        Assert.Equal("297", readings.Get(AlertIds.Kill300).Text);   // pending: the last number, not "?"
        Assert.Equal([2], Feed(d, Times(Gone, 3)));
        // Once done, a tooltip over the empty spot changes nothing.
        Assert.Empty(Feed(d, Times(Tip, 15)));
        Assert.Equal(KillDetector.DoneText, readings.Get(AlertIds.Kill300).Text);
    }

    [Fact]
    public void A_single_misread_jump_is_not_trusted()
    {
        var d = NewDetector();
        // 120 -> "300" once -> 121: the 300 does not continue the count, so it is ignored;
        // and a lone high value followed by "gone" does not count as "last value >= 295".
        Assert.Empty(Feed(d, Seq(N(120), N(120), N(300), N(121), N(121), N(299), Times(Gone, 15))));
    }

    [Fact]
    public void Rearms_when_the_panel_is_back_with_a_low_value()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings);
        Assert.Equal([12], Feed(d, Seq(N(299), N(299), Times(Gone, 11))));
        // Back at 296..299 (not "low"): not re-armed, nothing fires.
        Assert.Empty(Feed(d, Seq(N(296), N(296), N(297), Times(Gone, 15))));
        Assert.Equal(KillDetector.DoneText, readings.Get(AlertIds.Kill300).Text);
        // New card from 0: re-armed, the next 300 fires again.
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
        Assert.Equal([17], Feed(d, Seq(N(280), N(283), N(286), N(290), N(293), N(296), N(299), Times(Gone, 12))));
        var r2 = new HudReadings();
        var d2 = NewDetector(r2);
        Feed(d2, N(41), N(41));
        Assert.Equal(new RowReading("41", false), r2.Get(AlertIds.Kill300));
        Feed(d2, Bad, Bad, Bad);
        Assert.Equal("41", r2.Get(AlertIds.Kill300).Text);          // unreadable digits: no "?"
        Feed(d2, Tip);
        Assert.Equal(RowReading.Unknown, r2.Get(AlertIds.Kill300).Text);
        d2.Reset();
        Assert.Equal(RowReading.None, r2.Get(AlertIds.Kill300).Text);
    }

    [Fact]
    public void Real_299_frames_then_frames_without_the_panel_fire_after_10_s_with_the_Thai_text_and_panel_crop()
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
        for (int i = 3; i < 13; i++) d.Process(noPanel, HudScale.One, t.AddSeconds(i), events);
        Assert.Empty(events);
        d.Process(noPanel, HudScale.One, t.AddSeconds(13), events);
        var e = Assert.Single(events);
        Assert.Equal("ภารกิจสำเร็จ! ยิงครบ 300 ตัว (เลขล่าสุดที่เห็น 299)", e.Message);
        Assert.Equal(AlertIds.Kill300, e.AlertId);
        Assert.Equal(new Rectangle(1024 - 180, 326, 180, 40), e.Crop);
        var m = new AlertComposer(TestData.Config).Compose(e, noPanel, HudScale.One);
        using var img = new Bitmap(new MemoryStream(m.Png!));
        Assert.Equal(new Size(180, 40), img.Size);
    }

    [Theory]
    [InlineData("174618_682_kill_heartbeat.png")]   // bright sky
    [InlineData("174624_682_kill_change.png")]      // dark foliage (76 % near-black)
    [InlineData("174627_690_kill_change.png")]      // sea
    public void True_gone_crops_after_298_still_fire_after_10_s(string gone)
    {
        var d = NewDetector();
        var events = new List<WatchEvent>();
        var t = new DateTime(2026, 11, 18, 20, 0, 0);
        var f298 = Kill298.Load();
        var noPanel = RegionFrame(Path.Combine(LabelledDir("kill"), gone), "kill");
        d.Process(f298, HudScale.One, t, events);
        d.Process(f298, HudScale.One, t.AddSeconds(1), events);
        for (int i = 2; i < 12; i++) d.Process(noPanel, HudScale.One, t.AddSeconds(i), events);
        Assert.Empty(events);
        d.Process(noPanel, HudScale.One, t.AddSeconds(12), events);
        Assert.Equal("ภารกิจสำเร็จ! ยิงครบ 300 ตัว (เลขล่าสุดที่เห็น 298)", Assert.Single(events).Message);
    }

    /// <summary>BGR of a crop (alpha ignored), from a frame or a decoded PNG.</summary>
    internal static byte[] Bgr(Frame f, Rectangle r)
    {
        var px = new byte[r.Width * r.Height * 4];
        f.CopyRect(r, px);
        return Bgr(px, r.Width * r.Height);
    }

    internal static byte[] Bgr(byte[] bgra, int pixels)
    {
        var o = new byte[pixels * 3];
        for (int i = 0; i < pixels; i++) { o[i * 3] = bgra[i * 4]; o[i * 3 + 1] = bgra[i * 4 + 1]; o[i * 3 + 2] = bgra[i * 4 + 2]; }
        return o;
    }

    internal static byte[] PngBgr(byte[] png)
    {
        using var bmp = new Bitmap(new MemoryStream(png));
        return Bgr(Frame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)), new Rectangle(0, 0, bmp.Width, bmp.Height));
    }

    /// <summary>Pixels in the kill digits' orange key colour (collect region "kill").</summary>
    internal static int OrangeDigitPixels(byte[] bgr)
    {
        int n = 0;
        for (int i = 0; i < bgr.Length; i += 3)
            if (bgr[i + 2] >= 200 && bgr[i + 1] is >= 60 and <= 92 && bgr[i] is >= 5 and <= 25) n++;
        return n;
    }

    [Fact]
    public void Panel_disappearing_after_299_attaches_the_299_crop_not_the_empty_region()
    {
        var d = NewDetector();
        var events = new List<WatchEvent>();
        var t = new DateTime(2026, 11, 18, 20, 0, 0);
        var f298 = Kill298.Load();
        var f299 = Kill299.Load();
        var noPanel = Dead1024.Load();
        d.Process(f298, HudScale.One, t, events);
        d.Process(f299, HudScale.One, t.AddSeconds(1), events);
        for (int i = 2; i <= 12; i++) d.Process(noPanel, HudScale.One, t.AddSeconds(i), events);
        var e = Assert.Single(events);

        var region = new Rectangle(1024 - 180, 326, 180, 40);
        byte[] crop299 = Bgr(f299, region), cropNow = Bgr(noPanel, region);
        Assert.NotNull(e.Image);
        Assert.Equal(299, e.Image!.Value);
        Assert.Equal((180, 40), (e.Image.Width, e.Image.Height));
        Assert.Equal(crop299, Bgr(e.Image.Pixels, 180 * 40));
        Assert.Equal("ภารกิจสำเร็จ! ยิงครบ 300 ตัว (เลขล่าสุดที่เห็น 299)", e.Message);

        // The PNG that goes to Discord is that 299 crop, pixel for pixel, with the orange digits on it.
        var m = new AlertComposer(TestData.Config).Compose(e, noPanel, HudScale.One);
        var sent = PngBgr(m.Png!);
        Assert.Equal(crop299, sent);
        Assert.NotEqual(cropNow, sent);
        Assert.True(OrangeDigitPixels(sent) > 50, $"digits on the sent crop: {OrangeDigitPixels(sent)}");
        Assert.True(OrangeDigitPixels(cropNow) < 5, $"the region at alert time has no panel: {OrangeDigitPixels(cropNow)}");
    }
}
