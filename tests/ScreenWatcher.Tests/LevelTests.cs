using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Diagnostics;
using ScreenWatcher.Imaging;
using Xunit.Abstractions;
using static ScreenWatcher.Tests.HudTestData;

namespace ScreenWatcher.Tests;

/// <summary>
/// Row "เลเวลอัพ": the level from the line "Lv.88 50.61%" at the bottom centre. Lossless frames show
/// levels 85 (2026-11-18) and 88 (trial PNGs); the trial's JPEG evidence shows 87 -> 88 between
/// 03:27 and 06:20 (the level-up that opened the quest panel).
/// </summary>
public class LevelTests(ITestOutputHelper output)
{
    static readonly WatcherConfig C = TestData.Config;
    static readonly LevelReader Reader = new(C.Level);
    static readonly DateTime T0 = new(2026, 11, 19, 4, 20, 0);
    static string Field(string f) => Path.Combine(TestData.SamplesDir, "field-2026-11-19", f);

    static Frame Load(string path)
    {
        using var bmp = new Bitmap(path);
        return Frame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
    }

    public static IEnumerable<object[]> Lossless() =>
    [
        [new FullSample("field-2026-11-19/inventory_full_message.png", 0, 0, 1024, 768), 88],
        [new FullSample("field-2026-11-19/full/095352_339_full_event_unreadable.png", 0, 0, 1024, 768), 88],
        [new FullSample("field-2026-11-19/full/095643_337_full_event_unreadable.png", 0, 0, 1024, 768), 88],
        [Live1, 85], [Live2, 85], [Live3, 85], [Kill298, 85], [Kill299, 85], [GunEmpty, 85],
        [Alive1920, 85], [SmallA, 85], [SmallB, 85], [SmallC, 85], [MissileEmpty, 85], [Dead1024, 85],
    ];

    [Theory]
    [MemberData(nameof(Lossless))]
    public void Lossless_frames_read_the_level(FullSample sample, int level) =>
        Assert.Equal(level, Reader.Read(sample.Load(), HudScale.One));

    [Fact]
    public void Jpeg_trial_frames_read_the_right_level_or_nothing_never_a_wrong_one()
    {
        // Level by time (read by eye on the frames): 87 until 03:27, 88 from 06:20.
        var files = Directory.GetFiles(Field("full"), "*.jpg").Concat(Directory.GetFiles(Field(""), "*full*.jpg"));
        int read = 0;
        foreach (var f in files)
        {
            string name = Path.GetFileName(f);
            int expected = name.StartsWith("false_sp") || string.CompareOrdinal(name, "04") < 0 ? 87 : 88;
            int? got = Reader.Read(Load(f), HudScale.One);
            output.WriteLine($"{name}: {got?.ToString() ?? "?"}");
            if (got is not null) { Assert.Equal(expected, got); read++; }
        }
        Assert.True(read >= 8, $"only {read} JPEG frames read");
    }

    [Fact]
    public void Owner_client_1920x1200_at_hud_scale_1_11_reads_the_level_in_Tahoma_11()
    {
        // Live capture 2026-11-19 12:58 (the owner's current window): "Lv.88 86.69%", Tahoma 11 px.
        var frame = Load(Path.Combine(TestData.SamplesDir, "live-2026-11-19", "full_1920x1200.png"));
        Assert.Equal(88, Reader.Read(frame, new HudScale(1.1125, 1.115)));
    }

    [Fact]
    public void Level_digits_at_1_0_are_Tahoma_10_and_at_1920x1200_Tahoma_11()
    {
        static string Cropped(string[] rows)
        {
            int x0 = Enumerable.Range(0, rows[0].Length).First(x => rows.Any(r => r[x] == '#'));
            int x1 = Enumerable.Range(0, rows[0].Length).Last(x => rows.Any(r => r[x] == '#'));
            return string.Join("/", rows.Select(r => r[x0..(x1 + 1)]));
        }
        foreach (var (d, rows) in C.Level.Digits)
            Assert.Equal(Cropped(rows), CountFont.Glyph("Tahoma", 10, d[0]));
        foreach (var (d, rows) in C.Items.CountGlyphs)   // the item counts' Tahoma 11 digits = the 1920x1200 level digits
            Assert.Equal(string.Join("/", rows), CountFont.Glyph("Tahoma", 11, d[0]));
    }

    [Fact]
    public void No_level_line_no_reading()
    {
        Assert.Null(Reader.Read(TestData.ClientFrame("{1BB4016B-B041-4A52-9FE4-810346C3AC59}.png", 0, 0, 1024, 768), HudScale.One));
        Assert.Null(Reader.Read(Frame.FromBitmap(new Bitmap(1024, 768), new Rectangle(0, 0, 1024, 768)), HudScale.One));
        // The line moved out of the client (cut off at the bottom): nothing read.
        using var bmp = new Bitmap(Field("inventory_full_message.png"));
        Assert.Null(Reader.Read(Frame.FromBitmap(bmp, new Rectangle(0, 0, 1024, 760)), HudScale.One));
    }

    /// <summary>SYNTHETIC: the 1024x768 sample stretched (nearest neighbour, as a bitmap font is
    /// drawn larger) to a ~1.3 client. No real ~1.3 frame shows the line whole.</summary>
    [Theory]
    [InlineData(1.25)]
    [InlineData(1.305)]
    public void Stretched_line_is_read_at_about_1_3(double up)
    {
        using var src = new Bitmap(Field("inventory_full_message.png"));
        int w = (int)Math.Round(1024 * up), h = (int)Math.Round(768 * up);
        using var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(0, 0, w, h));
        }
        Assert.Equal(88, Reader.Read(Frame.FromBitmap(dst, new Rectangle(0, 0, w, h)), new HudScale(up, up)));
    }

    // ------------------------------------------------------------- row logic

    sealed class Rig
    {
        public readonly HudReadings Readings = new();
        public readonly HudOptions Options = new();
        public readonly LevelDetector D;
        public readonly List<WatchEvent> Events = [];
        public readonly List<string> Ignored = [];
        int _t;
        public Rig()
        {
            D = new LevelDetector(C, Readings, Options);
            D.Ignored += (a, b, why) => Ignored.Add($"{a}->{b} {why}");
        }
        public void Feed(int? level, int polls = 1)
        {
            for (int i = 0; i < polls; i++) D.Update(level, T0.AddSeconds(_t++), 1024, 768, Events);
        }
        public string Now => Readings.Get(AlertIds.LevelUp).Text;
    }

    [Fact]
    public void First_stable_reading_is_the_baseline_and_a_higher_one_alerts_once_with_the_full_frame()
    {
        var r = new Rig();
        Assert.Equal(RowReading.None, r.Now);
        r.Feed(87, 5);
        Assert.Empty(r.Events);
        Assert.Equal("Lv.87", r.Now);
        r.Feed(88, 2);                                  // two polls: not stable yet
        Assert.Empty(r.Events);
        r.Feed(88);
        var e = Assert.Single(r.Events);
        Assert.Equal("เลื่อนเลเวล! Lv.87 → Lv.88", e.Message);
        Assert.Equal(AlertIds.LevelUp, e.AlertId);
        Assert.Equal(new Rectangle(0, 0, 1024, 768), e.Crop);
        Assert.Equal(T0.AddSeconds(7), r.D.LastAlertAt);
        r.Feed(88, 20);
        Assert.Single(r.Events);
        r.Feed(90, 3);                                  // +2: still a level-up
        Assert.Equal("เลื่อนเลเวล! Lv.88 → Lv.90", r.Events[^1].Message);
    }

    [Fact]
    public void Lower_readings_and_big_jumps_are_ignored_and_journalled_once()
    {
        var r = new Rig();
        r.Feed(88, 3);
        r.Feed(86, 10);                                 // lower
        r.Feed(88, 3);
        r.Feed(95, 10);                                 // +7
        Assert.Empty(r.Events);
        Assert.Equal(["88->86 lower", "88->95 jump"], r.Ignored);
        r.Feed(89, 3);                                  // the real next level still counts
        Assert.Equal("เลื่อนเลเวล! Lv.88 → Lv.89", Assert.Single(r.Events).Message);
    }

    [Fact]
    public void Unreadable_shows_a_question_mark_and_a_new_game_session_takes_a_new_baseline()
    {
        var r = new Rig();
        r.Feed(null, 5);
        Assert.Equal(RowReading.None, r.Now);           // never seen yet
        r.Feed(87, 3);
        r.Feed(null, 2);
        Assert.Equal("Lv.87", r.Now);
        r.Feed(null);
        Assert.Equal(RowReading.Unknown, r.Now);

        r.D.Reset();                                    // pause: baseline kept
        r.Feed(88, 3);
        Assert.Single(r.Events);

        r.D.EndSession();                               // game lost and found again
        r.Feed(89, 3);
        Assert.Single(r.Events);                        // new baseline, no alert
    }

    [Fact]
    public void Switched_off_row_sends_nothing_and_does_not_quiet_the_quest_panel()
    {
        var r = new Rig();
        r.Options.SetEnabled(AlertIds.LevelUp, false);
        r.Feed(87, 3);
        r.Feed(88, 3);
        Assert.Single(r.Events);                        // raised; the alert service drops it (row off)
        Assert.Null(r.D.LastAlertAt);
    }

    [Fact]
    public void Quest_panel_message_is_not_sent_within_60_s_of_a_level_up_alert()
    {
        var readings = new HudReadings();
        var options = new HudOptions();
        DateTime? levelUp = null;
        var q = new QuestPanelDetector(C, readings, options, () => 10, quietUntil: () => levelUp?.AddSeconds(C.Level.QuestQuietSeconds));
        var events = new List<WatchEvent>();
        levelUp = T0;
        for (int s = 1; s < 40; s++) q.Seen(T0.AddSeconds(s), 1024, 768, events);
        Assert.Empty(events);                           // the level-up alert told the owner already
        for (int s = 40; s < 43; s++) q.NotSeen();
        for (int s = 100; s < 120; s++) q.Seen(T0.AddSeconds(s), 1024, 768, events);
        Assert.Equal(C.QuestPanel.Message, Assert.Single(events).Message);   // a later appearance
    }

    [Fact]
    public void Level_text_counts_as_a_value_for_statistics_and_the_unreadable_row()
    {
        Assert.True(RowStats.TryValue("Lv.88", out int v));
        Assert.Equal(88, v);
        var readings = new HudReadings();
        var u = new UnreadableDetector(C, readings, new HudOptions(), () => 0);
        foreach (var r in C.Unreadable.Rows) readings.Set(r[0], "20");
        readings.Set(AlertIds.InventoryFull, "ปกติ");
        readings.Set(AlertIds.LevelUp, "Lv.88");
        var events = new List<WatchEvent>();
        for (int s = 0; s < 5; s++) u.Process(T0.AddSeconds(s), 1024, 768, events);
        readings.Set(AlertIds.LevelUp, RowReading.Unknown);
        for (int s = 5; s < 30; s++) u.Process(T0.AddSeconds(s), 1024, 768, events);
        Assert.Equal(AlertIds.LevelUp, Assert.Single(Assert.Single(events).Unreadable!).Id);
    }

    [Fact]
    public void Its_alert_collects_the_level_region_which_is_not_in_the_ring()
    {
        Assert.DoesNotContain(C.Collect.Regions, r => r.Name == "level");
        var rules = new AutoCollectRules(C, C.Collect.AllRegions.Select(r => r.Name).ToArray());
        int level = rules.IndexOf("level");
        Assert.True(level >= 0);
        rules.NoteAlert(new WatchEvent(WatchEventKind.Hud, "x", T0, AlertIds.LevelUp, new Rectangle(0, 0, 1024, 768)));
        rules.Evaluate(new AutoInputs(T0, HudScale.One, true, _ => RowReading.None, _ => 0));
        Assert.True(rules.IsActive(level));
        var rc = new CollectRegion(C.Collect.AllRegions.First(r => r.Name == "level"));
        Assert.Equal(new Rectangle(382, 752, 260, 16), rc.Locate(1024, 768, HudScale.One));
    }
}
