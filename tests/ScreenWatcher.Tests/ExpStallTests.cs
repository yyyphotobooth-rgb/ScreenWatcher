using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Diagnostics;
using ScreenWatcher.Imaging;
using ScreenWatcher.UI;
using static ScreenWatcher.Tests.HudTestData;

namespace ScreenWatcher.Tests;

/// <summary>
/// Percentage read on every poll (level row "Lv.N · P%") and the row "EXP ไม่ขยับเกิน N นาที".
/// </summary>
public class ExpStallTests
{
    static readonly WatcherConfig C = TestData.Config;
    static readonly DateTime T0 = new(2026, 11, 19, 20, 0, 0);
    static string Field(string f) => Path.Combine(TestData.SamplesDir, "field-2026-11-19", f);

    static Frame Load(string path)
    {
        using var bmp = new Bitmap(path);
        return Frame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
    }

    // ------------------------------------------------------------- percentage on every poll

    [Theory]
    [MemberData(nameof(LevelTests.LosslessPct), MemberType = typeof(LevelTests))]
    public void Every_poll_reads_the_percentage_on_lossless_frames(FullSample sample, int level, string pct)
    {
        var readings = new HudReadings();
        var d = new LevelDetector(C, readings, new HudOptions());
        d.Process(sample.Load(), HudScale.One, T0, []);
        Assert.Equal(level, d.LastLevel);
        Assert.Equal(level, d.LastLine?.Level);
        Assert.Equal(pct, d.LastLine?.PctText);
        Assert.Equal($"Lv.{level} · {pct}%", readings.Get(AlertIds.LevelUp).Text);
    }

    [Fact]
    public void Every_poll_on_jpeg_frames_gives_the_right_percentage_or_none_never_a_wrong_one()
    {
        // Read by eye (same table as LevelTests); the level by time: 87 until 03:27, 88 from 06:20.
        var byEye = new Dictionary<string, string>
        {
            ["false_shield_out_full.jpg"] = "17.22", ["false_sp_out_full.jpg"] = "36.39", ["levelup_quest_popup_full.jpg"] = "48.51",
            ["unreadable_in_town_shop_full.jpg"] = "51.21", ["004703_335_kill300_full.jpg"] = "19.64", ["012821_333_kill300_full.jpg"] = "34.83",
            ["013353_581_miss_full.jpg"] = "36.52", ["021010_333_kill300_full.jpg"] = "48.04", ["024813_331_kill300_full.jpg"] = "62.66",
            ["032714_336_kill300_full.jpg"] = "76.88", ["062059_332_fuelLow_full.jpg"] = "16.38", ["083814_332_fuelLow_full.jpg"] = "38.50",
            ["095356_331_unreadable_full.jpg"] = "51.21", ["095647_327_unreadable_full.jpg"] = "51.45", ["103335_340_kill300_full.jpg"] = "58.05",
        };
        int read = 0;
        foreach (var f in Directory.GetFiles(Field(""), "*.jpg", SearchOption.AllDirectories))
        {
            var readings = new HudReadings();
            var d = new LevelDetector(C, readings, new HudOptions());
            d.Process(Load(f), HudScale.One, T0, []);
            if (d.LastLine?.PctHundredths is null) continue;
            Assert.Equal(byEye[Path.GetFileName(f)], d.LastLine.Value.PctText);
            Assert.StartsWith($"Lv.{d.LastLevel} · ", readings.Get(AlertIds.LevelUp).Text);
            read++;
        }
        Assert.True(read >= 6, $"only {read} JPEG percentages read");
    }

    [Fact]
    public void Level_row_text_with_and_without_the_percentage()
    {
        Assert.Equal("Lv.88 · 50.61%", LevelDetector.RowText(88, 5061));
        Assert.Equal("Lv.89 · 3.14%", LevelDetector.RowText(89, 314));
        Assert.Equal("Lv.89 · 0.05%", LevelDetector.RowText(89, 5));
        Assert.Equal("Lv.89 · 100.00%", LevelDetector.RowText(89, 10000));
        Assert.Equal("Lv.88", LevelDetector.RowText(88, null));

        var readings = new HudReadings();
        var d = new LevelDetector(C, readings, new HudOptions());
        var ev = new List<WatchEvent>();
        d.Update(88, T0, 1024, 768, ev, 5061);
        Assert.Equal("Lv.88 · 50.61%", readings.Get(AlertIds.LevelUp).Text);
        d.Update(88, T0.AddSeconds(1), 1024, 768, ev, null);   // level read, percentage not
        Assert.Equal("Lv.88", readings.Get(AlertIds.LevelUp).Text);
        d.Update(null, T0.AddSeconds(2), 1024, 768, ev, null);  // nothing read: still the last level, no percentage
        Assert.Equal("Lv.88", readings.Get(AlertIds.LevelUp).Text);
        Assert.Empty(ev);
    }

    [Fact]
    public void Level_row_with_percentage_still_counts_as_a_value_for_the_unreadable_rule_and_statistics()
    {
        Assert.True(RowStats.TryValue("Lv.88 · 50.61%", out int v));
        Assert.Equal(88, v);
        Assert.True(RowStats.TryValue("Lv.88", out v));
        Assert.Equal(88, v);

        // The unreadable rule: a level row that showed "Lv.88 · 50.61%" and then "?" past the limit fires.
        var readings = new HudReadings();
        var options = new HudOptions();
        options.SetThreshold(AlertIds.Unreadable, 2);
        var u = new UnreadableDetector(C, readings, options, () => 0);
        var ev = new List<WatchEvent>();
        readings.Set(AlertIds.LevelUp, "Lv.88 · 50.61%");
        u.Process(T0, 1024, 768, ev);
        readings.Set(AlertIds.LevelUp, RowReading.Unknown);
        for (int s = 1; s <= 10; s++) u.Process(T0.AddSeconds(s), 1024, 768, ev);
        Assert.Contains(ev, e => e.AlertId == AlertIds.Unreadable && e.Unreadable!.Any(r => r.Id == AlertIds.LevelUp));
        Assert.DoesNotContain(C.Unreadable.Rows, r => r[0] == AlertIds.ExpStall);
    }

    // ------------------------------------------------------------- stall row

    sealed class Rig
    {
        public readonly HudReadings Readings = new();
        public readonly HudOptions Options = new();
        public readonly ExpStallDetector D;
        public readonly List<WatchEvent> Events = [];
        public DateTime Now = T0;
        public Rig(int? minutes = null)
        {
            D = new ExpStallDetector(C, Readings, Options, () => null);
            if (minutes is { } m) Options.SetThreshold(AlertIds.ExpStall, m);
        }
        /// <summary>One poll per second for <paramref name="seconds"/> seconds.</summary>
        public void Polls(int seconds, int level, int? pct)
        {
            for (int i = 0; i < seconds; i++)
            {
                Now = Now.AddSeconds(1);
                D.Update(new LevelLine(level, pct), Now, 1024, 768, Events);
            }
        }
        public void Unreadable(int seconds)
        {
            for (int i = 0; i < seconds; i++) { Now = Now.AddSeconds(1); D.Update(null, Now, 1024, 768, Events); }
        }
        public string Text => Readings.Get(AlertIds.ExpStall).Text;
        public bool Armed { get { var d = new Dictionary<string, bool>(); D.CopyArmed(d); return d[AlertIds.ExpStall]; } }
    }

    [Fact]
    public void Default_is_five_minutes_and_the_alert_fires_once_with_the_full_frame()
    {
        var r = new Rig();
        Assert.Equal(5, r.D.LimitMinutes);
        Assert.Equal(RowReading.None, r.Text);
        r.Polls(300, 88, 5061);   // baseline confirmed at poll 2: ~298 s counted
        Assert.Empty(r.Events);
        Assert.Equal("นิ่ง 4 นาที", r.Text);
        r.Polls(3, 88, 5061);
        var e = Assert.Single(r.Events);
        Assert.Equal("EXP ไม่ขยับมา 5 นาที ยานอาจติดอยู่", e.Message);
        Assert.Equal(AlertIds.ExpStall, e.AlertId);
        Assert.Equal(WatchEventKind.Hud, e.Kind);
        Assert.Equal(new Rectangle(0, 0, 1024, 768), e.Crop);
        Assert.False(r.Armed);
        Assert.True(r.Readings.Get(AlertIds.ExpStall).Reached);
        r.Polls(600, 88, 5061);
        Assert.Single(r.Events);   // once
        Assert.Equal("นิ่ง 15 นาที", r.Text);
    }

    [Fact]
    public void Fires_after_the_set_minutes_not_before()
    {
        var r = new Rig(minutes: 1);
        r.Polls(2, 88, 5061);      // baseline
        Assert.Equal("ปกติ", r.Text);
        r.Polls(60, 88, 5061);     // 60 s counted: not over yet
        Assert.Empty(r.Events);
        Assert.Equal("นิ่ง 1 นาที", r.Text);
        r.Polls(1, 88, 5061);
        Assert.Equal("EXP ไม่ขยับมา 1 นาที ยานอาจติดอยู่", Assert.Single(r.Events).Message);
    }

    [Fact]
    public void Unreadable_time_neither_counts_nor_resets_the_clock()
    {
        var r = new Rig(minutes: 1);
        r.Polls(31, 88, 5061);     // baseline at poll 2, then 29 s counted
        r.Polls(120, 88, null);    // level read, percentage not
        r.Unreadable(120);         // nothing read
        Assert.Empty(r.Events);
        Assert.Equal(RowReading.Unknown, r.Text);
        Assert.InRange(r.D.CountedSeconds, 28.5, 29.5);
        r.Polls(30, 88, 5061);     // first readable poll counts nothing (gap was unreadable): +29 s = 58 s
        Assert.Empty(r.Events);
        r.Polls(3, 88, 5061);
        Assert.Single(r.Events);
    }

    [Fact]
    public void Paused_or_minimised_time_does_not_count_and_does_not_reset()
    {
        var r = new Rig(minutes: 1);
        r.Polls(41, 88, 5061);     // 39 s
        r.D.Reset();               // paused (no polls while paused)
        Assert.Equal(RowReading.None, r.Text);
        r.Now = r.Now.AddMinutes(30);
        r.Polls(1, 88, 5061);      // resumes: the 30 min are not counted
        Assert.InRange(r.D.CountedSeconds, 38.5, 39.5);
        Assert.Empty(r.Events);
        r.Polls(21, 88, 5061);     // 60 s: not over yet
        Assert.Empty(r.Events);
        r.Polls(1, 88, 5061);
        Assert.Single(r.Events);
    }

    [Fact]
    public void A_long_gap_between_polls_is_not_counted()
    {
        var r = new Rig(minutes: 1);
        r.Polls(11, 88, 5061);     // 9 s
        r.Now = r.Now.AddSeconds(100);
        r.Polls(1, 88, 5061);      // 101 s gap > MaxPollGapSeconds: not counted
        Assert.InRange(r.D.CountedSeconds, 8.5, 9.5);
    }

    [Fact]
    public void Any_stable_change_rearms_and_restarts_the_clock()
    {
        var r = new Rig(minutes: 1);
        r.Polls(63, 88, 5061);
        Assert.Single(r.Events);
        Assert.False(r.Armed);
        r.Polls(1, 88, 5062);      // one poll only: not stable, nothing changes
        Assert.False(r.Armed);
        r.Polls(2, 88, 5062);      // stable change of the percentage
        Assert.True(r.Armed);
        Assert.Equal("ปกติ", r.Text);
        Assert.InRange(r.D.CountedSeconds, 0, 1.5);
        r.Polls(62, 88, 5062);
        Assert.Equal(2, r.Events.Count);
        r.Polls(2, 89, 5062);      // a level change alone also counts as a change
        Assert.True(r.Armed);
        Assert.Equal("ปกติ", r.Text);
    }

    [Fact]
    public void App_start_and_game_found_again_start_a_fresh_clock()
    {
        // App start: a new detector; the first stable reading is the baseline, nothing fires at once.
        var r = new Rig(minutes: 1);
        r.Polls(2, 88, 5061);
        Assert.Empty(r.Events);
        Assert.InRange(r.D.CountedSeconds, 0, 0.5);

        r.Polls(50, 88, 5061);
        r.D.EndSession();          // game window lost
        Assert.Equal(RowReading.None, r.Text);
        Assert.Equal(0, r.D.CountedSeconds);
        r.Now = r.Now.AddMinutes(10);
        r.Polls(40, 88, 5061);     // same reading again: a fresh clock, not 50 + 40
        Assert.Empty(r.Events);
        r.Polls(25, 88, 5061);
        Assert.Single(r.Events);
    }

    [Fact]
    public void Limit_comes_from_the_row_setting_within_1_to_120()
    {
        var r = new Rig(minutes: 3);
        Assert.Equal(3, r.D.LimitMinutes);
        r.Options.SetThreshold(AlertIds.ExpStall, 0);
        Assert.Equal(1, r.D.LimitMinutes);
        r.Options.SetThreshold(AlertIds.ExpStall, 500);
        Assert.Equal(120, r.D.LimitMinutes);
    }

    [Fact]
    public void Row_setting_is_persisted_and_defaults_to_on_and_5_minutes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sw-stall-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var spec = AlertRowCatalog.Get(AlertIds.ExpStall);
            Assert.Equal(("EXP ไม่ขยับเกิน", "นาที", 5, 1, 120), (spec.Label, spec.Unit, spec.DefaultThreshold, spec.MinThreshold, spec.MaxThreshold));
            var fresh = AppSettings.Load(path);
            Assert.True(fresh.IsAlertEnabled(AlertIds.ExpStall));
            Assert.Equal(5, AlertRowCatalog.Threshold(fresh, spec));

            fresh.Thresholds[AlertIds.ExpStall] = 12;
            fresh.SetAlertEnabled(AlertIds.ExpStall, false);
            fresh.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal(12, AlertRowCatalog.Threshold(loaded, spec));
            Assert.False(loaded.IsAlertEnabled(AlertIds.ExpStall));

            loaded.Thresholds[AlertIds.ExpStall] = 121;   // out of range: the default
            Assert.Equal(5, AlertRowCatalog.Threshold(loaded, spec));

            // Row order: right after the level-up row.
            var ids = AlertRowCatalog.All.Select(s => s.Id).ToList();
            Assert.Equal(ids.IndexOf(AlertIds.LevelUp) + 1, ids.IndexOf(AlertIds.ExpStall));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
