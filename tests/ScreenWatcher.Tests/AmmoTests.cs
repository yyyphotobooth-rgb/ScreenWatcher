using System.Drawing;
using ScreenWatcher.Alerts;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using Xunit.Abstractions;
using static ScreenWatcher.Tests.HudTestData;

namespace ScreenWatcher.Tests;

/// <summary>
/// Stage 1, ammo counters. Labelled set: samples/labelled/ammo (260 crops saved by the collect
/// mode at HUD scale 1.0; every value checked by eye). A reading must be the labelled value or
/// "unreadable"; a wrong value is never accepted.
/// </summary>
public class AmmoTests(ITestOutputHelper output)
{
    static AmmoDetector NewDetector(HudReadings? readings = null) => new(TestData.Config, Glyphs, readings ?? new HudReadings());

    sealed class Tally
    {
        public int Fields, Read, Wrong;
        public readonly int[] DigitTotal = new int[10], DigitRead = new int[10];
        public int RedFields, RedRead;

        public void Add(string label, DigitReading r)
        {
            if (label == "blank")
            {
                Assert.False(r.Ok, "a blank counter must be unreadable");
                return;
            }
            Fields++;
            bool red = label == "00000";
            if (red) RedFields++;
            foreach (char c in label) DigitTotal[c - '0']++;
            if (!r.Ok) return;
            if (r.Value != int.Parse(label)) { Wrong++; return; }
            Read++;
            if (red) RedRead++;
            foreach (char c in label) DigitRead[c - '0']++;
        }

        public string Summary(string name) =>
            $"{name}: {Read}/{Fields} counters read ({100.0 * Read / Fields:F1} %), {Wrong} wrong; red 00000: {RedRead}/{RedFields}; per digit " +
            string.Join(" ", Enumerable.Range(0, 10).Select(d => $"{d}:{DigitRead[d]}/{DigitTotal[d]}"));
    }

    Tally ReadLabelled(double up)
    {
        var det = NewDetector();
        var tally = new Tally();
        foreach (var row in Labels("ammo"))
        {
            var frame = RegionFrame(Path.Combine(LabelledDir("ammo"), row[0]), "ammo", up);
            var (gun, missile) = det.ReadRaw(frame, new HudScale(up, up));
            tally.Add(row[1], gun);
            tally.Add(row[2], missile);
            if (row[2] == "00000") Assert.True(!missile.Ok || missile.Red, row[0] + ": empty counter should be read from the red digits");
        }
        output.WriteLine(tally.Summary($"scale {up}"));
        return tally;
    }

    [Fact]
    public void Labelled_set_at_scale_1_is_read_without_a_single_wrong_value()
    {
        var t = ReadLabelled(1);
        Assert.Equal(0, t.Wrong);
        Assert.Equal(518, t.Fields); // 260 crops x 2 counters, minus the one crop where both are blank
        // Measured 514/518; the four misses are counters over bright cloud, refused as unreadable.
        Assert.True(t.Read >= 514, t.Summary("scale 1"));
        Assert.Equal(t.RedFields, t.RedRead);
        Assert.True(t.RedFields >= 9);
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.305)]
    [InlineData(1.318)]
    [InlineData(1.5)]
    public void Labelled_set_stretched_like_a_large_window_is_read_without_a_wrong_value(double up)
    {
        var t = ReadLabelled(up);
        Assert.Equal(0, t.Wrong);
        Assert.True(t.Read >= t.Fields * 0.97, t.Summary($"scale {up}"));
        Assert.Equal(t.RedFields, t.RedRead);
    }

    public static IEnumerable<object?[]> FullFrames() =>
    [
        [Alive2560, 7970, 620],          // real HUD scale ~1.30
        [Dead1920x1440, 8051, 724],      // real HUD scale ~1.32
        [Dead1024, 8120, 770],
        [Alive1920, 7970, 620],
        [SmallA, 8098, 668],
        [SmallB, 7356, 302],
        [SmallC, 7356, 302],
        [MissileEmpty, 6456, 0],
        [GunEmpty, 0, 550],
        [Kill298, 9243, null],           // a tooltip covers the missile counter
        [Kill299, 9210, 1328],
        [Live1, 9125, 1188],
        [Live2, 9008, 1138],
        [Live3, 9003, 1138],
    ];

    [Theory]
    [MemberData(nameof(FullFrames))]
    public void Full_frame_samples_read_the_value_seen_in_the_image(FullSample sample, int? gun, int? missile)
    {
        var (g, m) = NewDetector().ReadRaw(sample.Load(), sample.Scale);
        output.WriteLine($"{sample}: gun {g}; missile {m}");
        Assert.Equal(gun, g.Ok ? g.Value : (int?)null);
        Assert.Equal(missile, m.Ok ? m.Value : (int?)null);
        if (g.Ok) Assert.Equal(gun == 0, g.Red);
        if (m.Ok) Assert.Equal(missile == 0, m.Red);
    }

    [Fact]
    public void Empty_fires_once_after_two_stable_polls_and_rearms_above_zero()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings);
        Assert.Equal(2, TestData.Config.Ammo.StablePolls);

        Assert.False(d.Update(1, 12));
        Assert.False(d.Update(1, 12));
        Assert.Equal(new RowReading("12", false), readings.Get(AlertIds.MissileEmpty));

        Assert.False(d.Update(1, 0));               // first 0: shown live, but not stable yet
        Assert.Equal(new RowReading("0", false), readings.Get(AlertIds.MissileEmpty));
        Assert.True(d.Update(1, 0));                // second 0: fires
        Assert.Equal(new RowReading("0", true), readings.Get(AlertIds.MissileEmpty));
        for (int i = 0; i < 10; i++) Assert.False(d.Update(1, 0)); // fires once

        Assert.False(d.Update(1, 300));             // reloaded
        Assert.True(readings.Get(AlertIds.MissileEmpty).Reached);  // one reading does not clear it
        Assert.False(d.Update(1, 300));             // stable above 0: re-armed
        Assert.Equal(new RowReading("300", false), readings.Get(AlertIds.MissileEmpty));
        Assert.False(d.Update(1, 0));
        Assert.True(d.Update(1, 0));                // fires again

        // The gun counter is independent.
        Assert.Equal(RowReading.None, readings.Get(AlertIds.GunAmmoEmpty).Text);
        Assert.False(d.Update(0, 0));
        Assert.True(d.Update(0, 0));
    }

    [Fact]
    public void A_single_zero_between_other_readings_does_not_fire()
    {
        var d = NewDetector();
        foreach (int? v in new int?[] { 40, 40, 0, 38, 0, null, 0, 36, 36 }) Assert.False(d.Update(0, v));
    }

    [Fact]
    public void Unreadable_shows_a_question_mark_and_never_alerts()
    {
        var readings = new HudReadings();
        var d = NewDetector(readings);
        d.Update(0, 500);
        d.Update(0, 500);
        // Short unreadable gaps keep the last value on screen; three in a row turn it into "?".
        Assert.False(d.Update(0, null));
        Assert.False(d.Update(0, null));
        Assert.Equal("500", readings.Get(AlertIds.GunAmmoEmpty).Text);
        Assert.False(d.Update(0, null));
        Assert.Equal(RowReading.Unknown, readings.Get(AlertIds.GunAmmoEmpty).Text);
        for (int i = 0; i < 20; i++) Assert.False(d.Update(0, null));

        d.Reset();
        Assert.Equal(RowReading.None, readings.Get(AlertIds.GunAmmoEmpty).Text);
    }

    [Fact]
    public void Real_frames_fire_the_missile_alert_with_its_Thai_text_and_the_ammo_strip_crop()
    {
        var d = NewDetector();
        var events = new List<WatchEvent>();
        var frame = MissileEmpty.Load();
        var t = new DateTime(2026, 11, 18, 20, 0, 0);
        d.Process(frame, HudScale.One, t, events);
        Assert.Empty(events);
        d.Process(frame, HudScale.One, t.AddSeconds(1), events);
        var e = Assert.Single(events);
        Assert.Equal(WatchEventKind.Hud, e.Kind);
        Assert.Equal("มิสไซล์หมด", e.Message);
        Assert.Equal(AlertIds.MissileEmpty, e.AlertId);
        Assert.Equal(new Rectangle(0, 0, 256, 30), e.Crop);

        var m = new AlertComposer(TestData.Config).Compose(e, frame, HudScale.One);
        Assert.Equal("มิสไซล์หมด", m.Text);
        Assert.Equal(AlertIds.MissileEmpty, m.AlertId);
        using var img = new Bitmap(new MemoryStream(m.Png!));
        Assert.Equal(new Size(256, 30), img.Size);

        events.Clear();
        d.Process(GunEmpty.Load(), HudScale.One, t.AddSeconds(2), events);
        d.Process(GunEmpty.Load(), HudScale.One, t.AddSeconds(3), events);
        Assert.Equal("กระสุนปืนกลหมด", Assert.Single(events).Message);
    }
}
