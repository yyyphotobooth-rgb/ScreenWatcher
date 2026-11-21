using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;
using ScreenWatcher.UI;

namespace ScreenWatcher.Tests;

/// <summary>
/// War pillar ("เสาวอ") row: reading the HH:MM timers from samples/from-chat/warpillar_*, several
/// panels, tracking by remaining time when slots shift, one alert per pillar, tooltip cover ->
/// "อ่านค่าไม่ได้", and the row's saved settings.
/// </summary>
public sealed class WarPillarTests
{
    static readonly WatcherConfig C = TestData.Config;
    static readonly DateTime T0 = new(2026, 11, 20, 14, 0, 0);
    // Blue-line corner of the first panel in a 1.0 client (warpillar_full_0014_1407.png).
    const int CornerX = 254, CornerY = 48, Pitch = 130;

    static List<PillarPanel> Find(Frame f)
    {
        var list = new List<PillarPanel>();
        new WarPillarFinder(C.WarPillar).Find(f, HudScale.One, list);
        return list;
    }

    // ------------------------------------------------------------------ reading the samples

    [Theory]
    [InlineData("warpillar_full_0014_1407.png", 1, 1, 1024, 766, 14)]
    [InlineData("warpillar_full_0015_1406.png", 0, 0, 1023, 770, 15)]
    public void Full_frames_show_one_panel_with_its_timer(string file, int x, int y, int w, int h, int minutes)
    {
        var f = TestData.ClientFrame(Path.Combine("from-chat", file), x, y, w, h);
        var m = new HudScaleLocator(C.HudScale).Measure(f);
        Assert.True(m.Found);
        Assert.Equal(HudScale.One, HudLayout.Snap(m.Scale));
        var p = Assert.Single(Find(f));
        Assert.Equal(minutes, p.Minutes);
        Assert.False(p.Covered);
    }

    [Theory]
    [InlineData("warpillar_crop_0012.png", 175, 39, 12)]   // panel-only crop; blue corner at (79, 9)
    [InlineData("warpillar_crop_0017.png", 8, 6, 17)]      // top-left HUD crop; blue corner at (246, 42)
    public void Crops_placed_on_a_client_are_read(string file, int clientX, int clientY, int minutes)
    {
        var f = TestData.CropOnBlackClient(Path.Combine("from-chat", file), clientX, clientY);
        var p = Assert.Single(Find(f));
        Assert.Equal(minutes, p.Minutes);
        Assert.Equal(new Point(CornerX - 1, CornerY - 1), p.Box.Location);
    }

    [Fact]
    public void Older_full_frame_samples_are_read_too()
    {
        // The 2026-11-18 samples (other client sizes, HUD 1.0): three of them were taken during a
        // war and show a panel (00:55, 00:51, 00:50) at the same place; the others show none.
        var expected = new (string File, int X, int Y, int W, int H, int[] Minutes)[]
        {
            ("dead_1024x768_detected.png", 9, 37, 1024, 768, []),
            ("alive_small_a.png", 164, 187, 1152, 864, [55]),
            ("alive_small_b.png", 318, 341, 1280, 800, [51]),
            ("alive_small_c.png", 134, 155, 1600, 1024, [50]),
            ("alive_1920x1080.png", 234, 220, 1920, 1080, []),
        };
        foreach (var (file, x, y, w, h, minutes) in expected)
        {
            var list = Find(TestData.ClientFrame(Path.Combine("from-chat", file), x, y, w, h));
            Assert.Equal(minutes, list.Select(p => p.Minutes));
            Assert.All(list, p => Assert.Equal(new Point(CornerX - 1, CornerY - 1), p.Box.Location));
        }
    }

    [Fact]
    public void Every_digit_is_read_from_a_rewritten_timer()
    {
        // The samples show 0,1,2,4,5,7; the font is Tahoma 11 (pixel-identical), so every HH:MM
        // drawn the same way must read back.
        var p = new PanelFrames();
        foreach (int m in new[] { 0, 3, 6, 8, 9, 59, 60, 99, 119, 120, 345, 599 })
            Assert.Equal(m, Assert.Single(Find(p.Frame(m))).Minutes);
    }

    [Fact]
    public void Three_panels_side_by_side_are_read_left_to_right()
    {
        var f = new PanelFrames().Frame(15, 42, 70);
        var list = Find(f);
        Assert.Equal([15, 42, 70], list.Select(p => p.Minutes));
        Assert.Equal([CornerX - 1, CornerX - 1 + Pitch, CornerX - 1 + 2 * Pitch], list.Select(p => p.Box.X));
        // Each alert picture: that pillar's label box and icon (the sample's crop, moved).
        Assert.Equal(new Rectangle(CornerX - 80 + Pitch, CornerY - 4, 128, 56), list[1].Crop);
    }

    [Fact]
    public void Two_stacked_panels_are_read_top_to_bottom_with_their_hp()
    {
        // 2026-11-20 19:05 (client = the whole 1024x768 image): 00:01 with its bar about 60 % (the
        // empty right part is dark), and under it 00:51 at 100 %.
        var f = TestData.ClientFrame(Path.Combine("from-chat", "warpillar_two_stacked_1905.png"), 0, 0, 1024, 768);
        var m = new HudScaleLocator(C.HudScale).Measure(f);
        Assert.True(m.Found);
        Assert.Equal(HudScale.One, HudLayout.Snap(m.Scale));
        var list = Find(f);
        Assert.Equal([1, 51], list.Select(p => p.Minutes));
        Assert.Equal([new Point(CornerX - 1, CornerY - 1), new Point(CornerX - 1, CornerY - 1 + PanelFrames.StackPitch)],
            list.Select(p => p.Box.Location));
        Assert.InRange(list[0].Hp, 57, 63);
        Assert.Equal(100, list[1].Hp);
        Assert.All(list, p => Assert.False(p.Covered || p.HpCovered));
        Assert.Equal(new Rectangle(CornerX - 80, CornerY - 4 + PanelFrames.StackPitch, 128, 56), list[1].Crop);
    }

    [Fact]
    public void Three_stacked_panels_are_read_top_to_bottom()
    {
        var list = Find(new PanelFrames().Stacked(new PanelFrames.P(15, 80), new(42, 100), new(70, 24, PanelFrames.BarFill.Yellow)));
        Assert.Equal([15, 42, 70], list.Select(p => p.Minutes));
        Assert.Equal([80, 100, PanelFrames.ReadBack(24)], list.Select(p => p.Hp));
        Assert.Equal([CornerY - 1, CornerY - 1 + 55, CornerY - 1 + 110], list.Select(p => p.Box.Y));
        Assert.All(list, p => Assert.Equal(CornerX - 1, p.Box.X));
    }

    // ------------------------------------------------------------------ tracking and alerts

    sealed class Run
    {
        public readonly HudReadings Readings = new();
        public readonly HudOptions Options = new();
        public readonly WarPillarDetector D;
        public readonly List<WatchEvent> Events = [];
        public readonly PanelFrames Frames = new();
        public DateTime Now = T0;

        public Run(int minutes = 10)
        {
            Options.SetThreshold(AlertIds.WarPillar, minutes);
            D = new WarPillarDetector(C, Readings, Options);
        }

        /// <summary>One poll at Now + seconds; returns the alerts it raised.</summary>
        public List<WatchEvent> Poll(double seconds, params int[] timers) => Poll(seconds, Frames.Frame(timers));

        public List<WatchEvent> Poll(double seconds, Frame f)
        {
            Now = T0.AddSeconds(seconds);
            var e = new List<WatchEvent>();
            D.Process(f, HudScale.One, Now, e);
            Events.AddRange(e);
            return e;
        }

        public string Text => Readings.Get(AlertIds.WarPillar).Text;
    }

    [Fact]
    public void Row_shows_every_timer_or_none()
    {
        var r = new Run();
        r.Poll(0);
        Assert.Equal(WarPillarDetector.NoPanelText, r.Text);
        r.Poll(1, 15, 42);
        Assert.Equal("00:15 (100%) · 00:42 (100%)", r.Text);
        Assert.False(r.Readings.Get(AlertIds.WarPillar).Reached);
        r.Poll(2, 125);
        Assert.Equal("02:05 (100%)", r.Text);
        r.Poll(3);
        Assert.Equal("02:05 (100%)", r.Text);   // gone for less than missingGraceSeconds: kept
        r.Poll(2 + C.WarPillar.MissingGraceSeconds);
        Assert.Equal("ไม่มี", r.Text);
        Assert.Empty(r.Events);
    }

    [Fact]
    public void Alerts_once_per_pillar_when_it_reaches_the_minutes_with_its_own_crop()
    {
        var r = new Run(10);
        r.Poll(0, 11, 30, 45);
        r.Poll(1, 11, 30, 45);
        Assert.Empty(r.Events);
        var e = Assert.Single(r.Poll(60, 10, 29, 44));
        Assert.Equal("เสาวอเหลือ 10 นาที!", e.Message);
        Assert.Equal(AlertIds.WarPillar, e.AlertId);
        Assert.Equal(WatchEventKind.Hud, e.Kind);
        Assert.Equal(new Rectangle(CornerX - 80, CornerY - 4, 128, 56), e.Crop);   // the first slot's panel
        Assert.True(r.Readings.Get(AlertIds.WarPillar).Reached);
        Assert.Empty(r.Poll(61, 10, 29, 44));
        Assert.Empty(r.Poll(120, 9, 28, 43));
        Assert.Empty(r.Poll(600, 1, 20, 35));
        Assert.Single(r.Events);
    }

    [Fact]
    public void First_pillar_ends_and_the_others_shift_left_without_duplicate_or_missed_alerts()
    {
        var r = new Run(10);
        r.Poll(0, 11, 30, 45);
        r.Poll(1, 11, 30, 45);
        Assert.Single(r.Poll(60, 10, 29, 44));            // A
        r.Poll(659, 0, 19, 34);
        // A ends: B and C move one slot left. B now sits where A was, at 18 (> 10): no alert.
        Assert.Empty(r.Poll(720, 18, 33));
        Assert.Empty(r.Poll(721, 18, 33));
        Assert.Empty(r.Poll(1199, 11, 26));
        var b = Assert.Single(r.Poll(1260, 10, 25));       // B, in slot 0
        Assert.Equal(new Rectangle(CornerX - 80, CornerY - 4, 128, 56), b.Crop);
        Assert.Empty(r.Poll(1261, 10, 25));
        Assert.Empty(r.Poll(1859, 0, 15));
        Assert.Empty(r.Poll(1920, 14));                     // B ended, C shifted into slot 0
        var c = Assert.Single(r.Poll(2160, 10));            // C
        Assert.Equal("เสาวอเหลือ 10 นาที!", c.Message);
        Assert.Empty(r.Poll(2161, 10));
        Assert.Equal(3, r.Events.Count);
    }

    [Fact]
    public void A_new_pillar_on_the_right_alerts_on_its_own()
    {
        var r = new Run(10);
        r.Poll(0, 8);
        Assert.Single(r.Poll(1, 8));
        Assert.Empty(r.Poll(2, 8, 60));
        Assert.Empty(r.Poll(3, 8, 60));
        Assert.Empty(r.Poll(480, 0, 52));
        Assert.Empty(r.Poll(540, 51));                       // first ended, new one shifted left
        var e = Assert.Single(r.Poll(2940, 10));
        Assert.Equal(new Rectangle(CornerX - 80, CornerY - 4, 128, 56), e.Crop);
        Assert.Equal(2, r.Events.Count);
    }

    [Fact]
    public void First_seen_already_at_or_below_the_minutes_alerts_once()
    {
        var r = new Run(10);
        Assert.Empty(r.Poll(0, 7));                        // read once: not confirmed yet
        var e = Assert.Single(r.Poll(1, 7));
        Assert.Equal("เสาวอเหลือ 7 นาที!", e.Message);
        for (int s = 2; s < 30; s++) Assert.Empty(r.Poll(s, 7));
        Assert.Empty(r.Poll(60, 6));
        Assert.Single(r.Events);
    }

    [Fact]
    public void A_hidden_panel_that_comes_back_is_the_same_pillar()
    {
        var r = new Run(10);
        r.Poll(0, 12);
        r.Poll(1, 12);
        r.Poll(120, 10);                                   // alert
        for (int s = 121; s < 300; s += 10) r.Poll(s);   // a game window over it: "ไม่มี"
        Assert.Equal("ไม่มี", r.Text);
        Assert.Empty(r.Poll(300, 7));
        Assert.Empty(r.Poll(301, 7));
        Assert.Single(r.Events);
    }

    [Fact]
    public void Missed_crossing_while_hidden_still_alerts_once_when_seen_again()
    {
        var r = new Run(10);
        r.Poll(0, 13);
        r.Poll(1, 13);
        // Came back at 9 (crossed 10 while hidden): it continues a confirmed pillar, so it alerts
        // on the first poll, once.
        Assert.Single(r.Poll(240, 9));
        Assert.Empty(r.Poll(241, 9));
        Assert.Empty(r.Poll(300, 8));
        Assert.Single(r.Events);
    }

    [Fact]
    public void Tracker_matches_by_remaining_time_not_slot()
    {
        var t = new WarPillarTracker(2, 2);
        var fire = new List<WarPillarTracker.Alert>();
        t.Update([20, 21], T0, 10, fire);
        t.Update([20, 21], T0.AddSeconds(1), 10, fire);
        Assert.Equal(2, t.Count);
        t.Update([21], T0.AddSeconds(2), 10, fire);         // first ended: 21 is the second pillar
        t.Update([30, 21], T0.AddSeconds(3), 10, fire);     // a new one, drawn LEFT of it for the test
        Assert.Equal(3, t.Count);
        Assert.Empty(fire);
        // Times never go up: 22 is a new pillar, not the 21 one.
        t.Update([22], T0.AddSeconds(4), 10, fire);
        Assert.Equal(4, t.Count);
    }

    [Fact]
    public void A_misread_new_value_seen_once_is_forgotten()
    {
        var t = new WarPillarTracker(2, 2);
        var fire = new List<WarPillarTracker.Alert>();
        t.Update([5], T0, 10, fire);
        t.Update([], T0.AddSeconds(5), 10, fire);
        Assert.Equal(0, t.Count);
        Assert.Empty(fire);
    }

    [Fact]
    public void Pillars_are_forgotten_after_their_countdown_ended()
    {
        var t = new WarPillarTracker(2, 2);
        var fire = new List<WarPillarTracker.Alert>();
        t.Update([3], T0, 10, fire);
        t.Update([3], T0.AddSeconds(1), 10, fire);
        Assert.Single(fire);
        t.Update([], T0.AddMinutes(4), 10, fire);
        Assert.Equal(1, t.Count);
        t.Update([], T0.AddMinutes(6), 10, fire);
        Assert.Equal(0, t.Count);
    }

    static Rectangle StackCrop(int slot) => new(CornerX - 80, CornerY - 4 + PanelFrames.StackPitch * slot, 128, 56);

    [Fact]
    public void Stacked_top_pillar_ends_and_the_others_shift_up_without_duplicate_or_missed_alerts()
    {
        var r = new Run(10);
        WatchEvent Only(double s, params int[] t) => Assert.Single(r.Poll(s, r.Frames.Stacked(t)));
        void None(double s, params int[] t) => Assert.Empty(r.Poll(s, r.Frames.Stacked(t)));

        None(0, 11, 30, 45);
        None(1, 11, 30, 45);
        Assert.Equal("00:11 (100%) · 00:30 (100%) · 00:45 (100%)", r.Text);
        var a = Only(60, 10, 29, 44);                          // A (top)
        Assert.Equal(StackCrop(0), a.Crop);
        None(61, 10, 29, 44);
        None(659, 0, 19, 34);
        // A ends: B and C move one slot UP. B now sits where A was, at 18 (> 10): no alert.
        None(720, 18, 33);
        None(721, 18, 33);
        None(1199, 11, 26);
        var b = Only(1260, 10, 25);                            // B, now on top
        Assert.Equal(StackCrop(0), b.Crop);
        None(1261, 10, 25);
        None(1859, 0, 15);
        None(1920, 14);                                        // B ended, C moved up
        var c = Only(2160, 10);                                // C
        Assert.Equal("เสาวอเหลือ 10 นาที!", c.Message);
        Assert.Equal(StackCrop(0), c.Crop);
        None(2161, 10);
        None(2400, 6);
        Assert.Equal(3, r.Events.Count);
    }

    [Fact]
    public void Stacked_hp_alerts_fire_once_per_pillar_across_the_shift_up()
    {
        var r = new Run(5);
        List<WatchEvent> Poll(double s, params PanelFrames.P[] p) => r.Poll(s, r.Frames.Stacked(p));

        // A (top, ends soon), B and C under it.
        Poll(0, Hp(8, 100), Hp(30, 90), Hp(40, 80));
        Assert.Empty(Poll(1, Hp(8, 100), Hp(30, 90), Hp(40, 80)));
        // A drops below 75 (two polls): one alert, top crop.
        Poll(2, Hp(8, 60), Hp(30, 90), Hp(40, 80));
        var a = Assert.Single(Poll(3, Hp(8, 60), Hp(30, 90), Hp(40, 80)));
        Assert.Equal("เสาวอเลือดเหลือ 75%!", a.Message);
        Assert.Equal(StackCrop(0), a.Crop);
        // C (bottom) drops below 75.
        Poll(4, Hp(8, 60), Hp(30, 90), Hp(40, 70));
        var c = Assert.Single(Poll(5, Hp(8, 60), Hp(30, 90), Hp(40, 70)));
        Assert.Equal("เสาวอเลือดเหลือ 75%!", c.Message);
        Assert.Equal(StackCrop(2), c.Crop);
        // A ends (its panel goes); B and C shift up. Same HP: nothing new (no second 75 % for C).
        for (int s = 6; s < 16; s++) Assert.Empty(Poll(s, Hp(30, 90), Hp(40, 70)));
        Assert.Equal($"00:30 ({PanelFrames.ReadBack(90)}%) · 00:40 ({PanelFrames.ReadBack(70)}%)", r.Text);
        // B (now on top) drops below 75: its own first HP alert, top crop.
        Assert.Empty(Poll(16, Hp(30, 70), Hp(40, 70)));
        var b = Assert.Single(Poll(17, Hp(30, 70), Hp(40, 70)));
        Assert.Equal("เสาวอเลือดเหลือ 75%!", b.Message);
        Assert.Equal(StackCrop(0), b.Crop);
        // C (now slot 1) drops below 50: one alert, slot-1 crop.
        Assert.Empty(Poll(18, Hp(30, 70), Hp(40, 45)));
        var c2 = Assert.Single(Poll(19, Hp(30, 70), Hp(40, 45)));
        Assert.Equal("เสาวอเลือดเหลือ 50%!", c2.Message);
        Assert.Equal(StackCrop(1), c2.Crop);
        for (int s = 20; s < 40; s++) Assert.Empty(Poll(s, Hp(30, 70), Hp(40, 45)));
        // No time alerts: the threshold is 5 min and nobody got there.
        Assert.Equal(4, r.Events.Count);
    }

    // ------------------------------------------------------------------ tooltip / unreadable

    [Fact]
    public void Tooltip_text_over_a_panel_shows_unknown_and_reports_unreadable_after_the_limit()
    {
        var r = new Run(10);
        var unread = new UnreadableDetector(C, r.Readings, r.Options, () => 0);
        var events = new List<WatchEvent>();
        foreach (var row in C.Unreadable.Rows) r.Readings.Set(row[0], RowReading.None);
        void Both(int s, Frame f) { r.Poll(s, f); unread.Process(r.Now, 1024, 768, events); }

        var covered = r.Frames.Frame(30, 40);
        PanelFrames.Tooltip(covered, CornerX - 10 + Pitch, CornerY + 22, "Attack Up 15%");
        Assert.Equal(30, Find(covered)[0].Minutes);
        Assert.True(Find(covered).Count == 1 || Find(covered)[1].Covered);

        for (int s = 0; s < 3; s++) Both(s, r.Frames.Frame(30, 40));
        Assert.Equal("00:30 (100%) · 00:40 (100%)", r.Text);
        for (int s = 3; s <= 13; s++) Both(s, covered);   // "?" from 3 s: 10 s at 13 s, not longer yet
        Assert.Equal(RowReading.Unknown, r.Text);
        Assert.Empty(events);
        for (int s = 14; s <= 17; s++) Both(s, covered);   // crosses at 14 s, combine window 3 s
        var e = Assert.Single(events);
        Assert.Equal(AlertIds.Unreadable, e.AlertId);
        Assert.Equal("อ่านค่าไม่ได้ : เสาวอ", e.Message);
        Both(18, r.Frames.Frame(30, 40));
        Assert.Equal("00:30 (100%) · 00:40 (100%)", r.Text);
        Assert.Empty(r.Events);
    }

    [Fact]
    public void Panels_gone_without_tooltip_are_none_and_never_unreadable()
    {
        var r = new Run(10);
        var unread = new UnreadableDetector(C, r.Readings, r.Options, () => 0);
        var events = new List<WatchEvent>();
        foreach (var row in C.Unreadable.Rows) r.Readings.Set(row[0], RowReading.None);
        for (int s = 0; s < 3; s++) { r.Poll(s, 30); unread.Process(r.Now, 1024, 768, events); }
        for (int s = 3; s < 40; s++) { r.Poll(s); unread.Process(r.Now, 1024, 768, events); }
        Assert.Equal("ไม่มี", r.Text);
        Assert.Empty(events);
    }

    [Fact]
    public void Row_value_counts_as_a_value_for_statistics()
    {
        Assert.True(Diagnostics.RowStats.TryValue("00:15 · 00:42", out int v));
        Assert.Equal(15, v);
        Assert.True(Diagnostics.RowStats.TryValue("02:05", out v));
        Assert.Equal(125, v);
        Assert.True(Diagnostics.RowStats.TryValue("00:15 (80%) · 00:42 (100%)", out v));
        Assert.Equal(15, v);
        Assert.True(Diagnostics.RowStats.TryValue("02:05 (7%)", out v));
        Assert.Equal(125, v);
        Assert.False(Diagnostics.RowStats.TryValue("ไม่มี", out _));
        Assert.False(Diagnostics.RowStats.TryValue("?", out _));
    }

    // ------------------------------------------------------------------ flashing box (1.2.2)

    // 2026-11-21 14:30, 60 native WGC frames, 1 s apart (client = whole 1024x768 image). The
    // pillar's new icon has white art reaching into the timer band. 3 frames are black (no panel)
    // and in 8 a game window ("GM") lies over the bottom of the icon box (timer and bar cut).
    static readonly string LiveDir = Path.Combine(TestData.SamplesDir, "live-2026-11-21");
    static readonly HashSet<int> LiveBlack = [32, 33, 34];
    static readonly HashSet<int> LiveWindowOver = [28, 29, 30, 31, 35, 36, 37, 38];

    static IEnumerable<(int Index, Frame Frame)> LiveFrames() =>
        Directory.GetFiles(LiveDir, "live_*.png").OrderBy(f => f, StringComparer.Ordinal).Select((f, i) =>
            (i, TestData.ClientFrame(Path.Combine("live-2026-11-21", Path.GetFileName(f)), 0, 0, 1024, 768)));

    [Fact]
    public void Live_frames_with_a_visible_panel_are_found_and_read()
    {
        int read = 0;
        foreach (var (i, f) in LiveFrames())
        {
            var list = Find(f);
            if (LiveBlack.Contains(i)) { Assert.Empty(list); continue; }
            var p = Assert.Single(list);
            Assert.Equal(new Point(CornerX - 1, CornerY - 1), p.Box.Location);
            if (LiveWindowOver.Contains(i))
            {
                Assert.Equal(-1, p.Minutes);   // cut by the window: unreadable, the row keeps its value
                continue;
            }
            Assert.InRange(p.Minutes, 36, 37);
            Assert.InRange(p.Hp, 11, 17);
            read++;
        }
        Assert.Equal(49, read);
    }

    [Fact]
    public void Red_flash_samples_are_found_and_read()
    {
        // warpillar_redframe_1426.png: the whole icon box flashed red while attacked, 00:41. Its bar:
        // track columns 262..296 (35), fill 262..275 full and 276 half lit = 14.5/35; the reader
        // counts full columns: 40 %.
        var red = TestData.ClientFrame(Path.Combine("from-chat", "warpillar_redframe_1426.png"), 0, 0, 1024, 768);
        var p = Assert.Single(Find(red));
        Assert.Equal(41, p.Minutes);
        Assert.Equal(40, p.Hp);
        Assert.Equal(new Point(CornerX - 1, CornerY - 1), p.Box.Location);
        // The 2026-11-19 PrintWindow frames (1600x1024) caught another red flash at 00:41.
        foreach (var file in new[] { "live_printwindow.png", "live2_printwindow.png", "live3_printwindow.png" })
        {
            var q = Assert.Single(Find(TestData.ClientFrame(file, 0, 0, 1600, 1024)));
            Assert.Equal(41, q.Minutes);
            Assert.InRange(q.Hp, 80, 90);
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0.25)]
    public void Flashed_and_blended_boxes_are_read_like_blue_ones(double flash)
    {
        var f = new PanelFrames().Stacked(new PanelFrames.P(15, 60, Flash: flash), new(42, 100), new(70, 24, Flash: flash));
        var list = Find(f);
        Assert.Equal([15, 42, 70], list.Select(p => p.Minutes));
        Assert.Equal([PanelFrames.ReadBack(60), 100, PanelFrames.ReadBack(24)], list.Select(p => p.Hp));
    }

    [Fact]
    public void Live_sequence_gives_one_time_alert_and_one_hp_alert()
    {
        // Threshold 36 min: the timer goes 37 -> 36 once; HP 17..11 is first seen below 25 (one
        // alert for 25). Black and window-covered frames never reset the row or the pillar.
        var r = new Run(36);
        int s = 0;
        foreach (var (i, f) in LiveFrames())
        {
            r.Poll(s++, f);
            if (i > 0) Assert.Matches(@"^00:3[67] \(\d+%\)$", r.Text);
        }
        Assert.Equal(["เสาวอเลือดเหลือ 25%!", "เสาวอเหลือ 36 นาที!"], r.Events.Select(e => e.Message).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Flashing_sequence_gives_one_time_alert_and_one_alert_per_hp_level()
    {
        // One pillar, 12 -> 9 min, HP 80 -> 60 -> 40, polled every second: the box flashes blue /
        // red / blend in turn and every 7th poll has no panel at all (a flash frame lost).
        var r = new Run(10);
        var cache = new Dictionary<(int, int, double), Frame>();
        for (int s = 0; s < 200; s++)
        {
            int minutes = 12 - s / 60, hp = s < 40 ? 80 : s < 80 ? 60 : 40;
            double flash = (s % 3) switch { 0 => 0, 1 => 1, _ => 0.5 };
            if (s % 7 == 6) { r.Poll(s); }
            else
            {
                var key = (minutes, hp, flash);
                if (!cache.TryGetValue(key, out var f)) cache[key] = f = r.Frames.Frame(new PanelFrames.P(minutes, hp, Flash: flash));
                r.Poll(s, f);
            }
            if (s > 0) Assert.StartsWith($"00:", r.Text);
        }
        Assert.Equal(["เสาวอเลือดเหลือ 50%!", "เสาวอเลือดเหลือ 75%!", "เสาวอเหลือ 10 นาที!"],
            r.Events.Select(e => e.Message).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_pending_hp_drop_survives_a_poll_without_the_pillar()
    {
        var t = new WarPillarTracker(2, 2, [75, 50, 25]);
        var fire = new List<WarPillarTracker.Alert>();
        int s = 0;
        void Poll(int hp = -2) =>   // -2: the pillar not seen this poll
            t.Update(hp == -2 ? [] : [new WarPillarTracker.Reading(60, hp)], T0.AddSeconds(s++), 10, fire, null);
        Poll(100); Poll(100);
        Poll(40); Poll(); Poll(40);                      // flash frame between the two readings
        var a = Assert.Single(fire);
        Assert.Equal(50, a.HpLevel);
        Poll(); Poll(40); Poll(40);
        Assert.Single(fire);                             // no re-alert after the gaps
    }

    // ------------------------------------------------------------------ HP bar

    [Fact]
    public void Real_samples_read_full_hp()
    {
        var frames = new[]
        {
            TestData.ClientFrame(Path.Combine("from-chat", "warpillar_full_0014_1407.png"), 1, 1, 1024, 766),
            TestData.ClientFrame(Path.Combine("from-chat", "warpillar_full_0015_1406.png"), 0, 0, 1023, 770),
            TestData.CropOnBlackClient(Path.Combine("from-chat", "warpillar_crop_0012.png"), 175, 39),
            TestData.CropOnBlackClient(Path.Combine("from-chat", "warpillar_crop_0017.png"), 8, 6),
            TestData.ClientFrame(Path.Combine("from-chat", "alive_small_a.png"), 164, 187, 1152, 864),
            TestData.ClientFrame(Path.Combine("from-chat", "alive_small_b.png"), 318, 341, 1280, 800),
            TestData.ClientFrame(Path.Combine("from-chat", "alive_small_c.png"), 134, 155, 1600, 1024),
        };
        foreach (var f in frames)
        {
            var p = Assert.Single(Find(f));
            Assert.Equal(100, p.Hp);   // fill reaches the light right border: all 35 columns
            Assert.False(p.HpCovered);
        }
    }

    [Theory]
    [InlineData(PanelFrames.BarFill.Red, PanelFrames.BarEmpty.Grey)]
    [InlineData(PanelFrames.BarFill.Red, PanelFrames.BarEmpty.DarkRed)]
    [InlineData(PanelFrames.BarFill.Orange, PanelFrames.BarEmpty.Grey)]
    [InlineData(PanelFrames.BarFill.Yellow, PanelFrames.BarEmpty.DarkRed)]
    public void Bars_cut_to_a_level_are_measured(PanelFrames.BarFill fill, PanelFrames.BarEmpty empty)
    {
        var frames = new PanelFrames();
        foreach (int hp in new[] { 100, 80, 74, 49, 24, 10, 0 })
        {
            var p = Assert.Single(Find(frames.Frame(new PanelFrames.P(20, hp, fill, empty))));
            Assert.Equal(20, p.Minutes);
            Assert.Equal(PanelFrames.ReadBack(hp), p.Hp);        // whole columns of 35: within 1.5 %
            Assert.True(Math.Abs(p.Hp - hp) <= 2, $"{hp} -> {p.Hp}");
            // The level side is right for every cut used by the alerts.
            foreach (int level in new[] { 75, 50, 25 }) Assert.Equal(hp < level, p.Hp < level);
        }
    }

    [Fact]
    public void Several_panels_each_have_their_own_hp()
    {
        var list = Find(new PanelFrames().Frame(new PanelFrames.P(15, 80), new(42, 100), new(70, 24, PanelFrames.BarFill.Yellow)));
        Assert.Equal([15, 42, 70], list.Select(p => p.Minutes));
        Assert.Equal([80, 100, PanelFrames.ReadBack(24)], list.Select(p => p.Hp));
    }

    [Fact]
    public void Row_shows_each_timer_with_its_hp()
    {
        var r = new Run();
        r.Poll(0, r.Frames.Frame(new PanelFrames.P(15, 80), new(42, 100)));
        Assert.Equal("00:15 (80%) · 00:42 (100%)", r.Text);
        Assert.True(WarPillarDetector.TryParseRow(r.Text, out int m));
        Assert.Equal(15, m);
    }

    static PanelFrames.P Hp(int minutes, int hp) => new(minutes, hp);

    [Fact]
    public void Drop_from_80_to_40_gives_one_50_percent_alert_with_the_pillar_crop()
    {
        var r = new Run(10);
        r.Poll(0, r.Frames.Frame(Hp(40, 80)));
        r.Poll(1, r.Frames.Frame(Hp(40, 80)));
        Assert.Empty(r.Events);
        Assert.Empty(r.Poll(2, r.Frames.Frame(Hp(40, 40))));        // one poll: not yet
        Assert.Equal("00:40 (80%)", r.Text);
        var e = Assert.Single(r.Poll(3, r.Frames.Frame(Hp(40, 40))));
        Assert.Equal("เสาวอเลือดเหลือ 50%!", e.Message);
        Assert.Equal(AlertIds.WarPillar, e.AlertId);
        Assert.Equal(new Rectangle(CornerX - 80, CornerY - 4, 128, 56), e.Crop);
        Assert.Equal($"00:40 ({PanelFrames.ReadBack(40)}%)", r.Text);
        for (int s = 4; s < 20; s++) Assert.Empty(r.Poll(s, r.Frames.Frame(Hp(40, 40))));
        Assert.Single(r.Events);
    }

    [Fact]
    public void Each_level_alerts_once_and_a_one_poll_dip_or_a_rise_does_not_count()
    {
        var t = new WarPillarTracker(2, 2, [75, 50, 25]);
        var fire = new List<WarPillarTracker.Alert>();
        var hpOut = new List<int>();
        int s = 0;
        void Poll(int hp) => t.Update([new WarPillarTracker.Reading(60, hp)], T0.AddSeconds(s++), 10, fire, hpOut);

        Poll(100); Poll(100);
        Poll(20); Poll(100); Poll(20); Poll(100);           // single-poll misreads: nothing
        Assert.Empty(fire);
        Assert.Equal(100, hpOut[0]);
        Poll(70); Poll(70);                                 // 75
        Poll(90); Poll(90);                                 // a rise is ignored
        Assert.Equal(70, hpOut[0]);
        Poll(74); Poll(72);                                 // still above 50: no alert
        Poll(45); Poll(45);                                 // 50
        Poll(30); Poll(30);
        Poll(20); Poll(20);                                 // 25
        Poll(10); Poll(10); Poll(0); Poll(0);
        Assert.Equal([75, 50, 25], fire.Select(a => a.HpLevel));
        Assert.All(fire, a => Assert.Equal(0, a.Slot));
    }

    [Fact]
    public void Skipping_several_levels_gives_one_alert_for_the_lowest()
    {
        var r = new Run(10);
        r.Poll(0, r.Frames.Frame(Hp(40, 80)));
        r.Poll(1, r.Frames.Frame(Hp(40, 80)));
        r.Poll(2, r.Frames.Frame(Hp(40, 10)));
        var e = Assert.Single(r.Poll(3, r.Frames.Frame(Hp(40, 10))));
        Assert.Equal("เสาวอเลือดเหลือ 25%!", e.Message);
        Assert.Empty(r.Poll(4, r.Frames.Frame(Hp(40, 0))));
        Assert.Empty(r.Poll(5, r.Frames.Frame(Hp(40, 0))));
        Assert.Single(r.Events);
    }

    [Fact]
    public void A_pillar_first_seen_below_a_level_alerts_once_for_the_lowest()
    {
        var r = new Run(10);
        Assert.Empty(r.Poll(0, r.Frames.Frame(Hp(40, 45))));
        var e = Assert.Single(r.Poll(1, r.Frames.Frame(Hp(40, 45))));
        Assert.Equal("เสาวอเลือดเหลือ 50%!", e.Message);
        Assert.Empty(r.Poll(2, r.Frames.Frame(Hp(40, 45))));
    }

    [Fact]
    public void Tooltip_over_the_bar_is_unreadable_never_a_drop_then_a_lower_reading_alerts()
    {
        var r = new Run(10);
        var unread = new UnreadableDetector(C, r.Readings, r.Options, () => 0);
        var events = new List<WatchEvent>();
        foreach (var row in C.Unreadable.Rows) r.Readings.Set(row[0], RowReading.None);
        void Both(int s, Frame f) { r.Poll(s, f); unread.Process(r.Now, 1024, 768, events); }

        var covered = r.Frames.Frame(Hp(40, 30));
        PanelFrames.Tooltip(covered, CornerX + 4, CornerY + 40, "HP 30%");
        var p = Assert.Single(Find(covered));
        Assert.Equal(40, p.Minutes);                        // the timer is still read
        Assert.True(p.HpCovered);
        Assert.Equal(-1, p.Hp);

        for (int s = 0; s < 3; s++) Both(s, r.Frames.Frame(Hp(40, 80)));
        Assert.Equal("00:40 (80%)", r.Text);
        for (int s = 3; s <= 13; s++) Both(s, covered);
        Assert.Equal(RowReading.Unknown, r.Text);
        Assert.Empty(events);
        for (int s = 14; s <= 17; s++) Both(s, covered);
        var u = Assert.Single(events);
        Assert.Equal("อ่านค่าไม่ได้ : เสาวอ", u.Message);
        Assert.Empty(r.Events);                            // no HP alert while covered

        // Tooltip gone, the bar now lower: alerts normally (two polls).
        Both(18, r.Frames.Frame(Hp(40, 30)));
        Assert.Empty(r.Events);
        Both(19, r.Frames.Frame(Hp(40, 30)));
        Assert.Equal("เสาวอเลือดเหลือ 50%!", Assert.Single(r.Events).Message);
        Assert.Equal($"00:40 ({PanelFrames.ReadBack(30)}%)", r.Text);
    }

    [Fact]
    public void Two_pillars_with_different_hp_are_tracked_separately_after_the_left_shift()
    {
        var r = new Run(10);
        // A ends soon; B and C have the SAME remaining time, so only HP tells them apart.
        r.Poll(0, r.Frames.Frame(Hp(5, 100), Hp(30, 90), Hp(30, 80)));
        var first = r.Poll(1, r.Frames.Frame(Hp(5, 100), Hp(30, 90), Hp(30, 80)));
        Assert.Equal("เสาวอเหลือ 5 นาที!", Assert.Single(first).Message);   // A's time alert
        // A gone: B and C shift one slot left.
        for (int s = 360; s < 365; s++) Assert.Empty(r.Poll(s, r.Frames.Frame(Hp(24, 90), Hp(24, 80))));
        Assert.Equal($"00:24 ({PanelFrames.ReadBack(90)}%) · 00:24 (80%)", r.Text);
        // C (now slot 1) drops below 75.
        Assert.Empty(r.Poll(365, r.Frames.Frame(Hp(24, 90), Hp(24, 70))));
        var c = Assert.Single(r.Poll(366, r.Frames.Frame(Hp(24, 90), Hp(24, 70))));
        Assert.Equal("เสาวอเลือดเหลือ 75%!", c.Message);
        Assert.Equal(new Rectangle(CornerX - 80 + Pitch, CornerY - 4, 128, 56), c.Crop);
        // B (slot 0) drops below 50 (skipping 75): one alert, its own crop; C unchanged: nothing.
        Assert.Empty(r.Poll(367, r.Frames.Frame(Hp(24, 45), Hp(24, 70))));
        var b = Assert.Single(r.Poll(368, r.Frames.Frame(Hp(24, 45), Hp(24, 70))));
        Assert.Equal("เสาวอเลือดเหลือ 50%!", b.Message);
        Assert.Equal(new Rectangle(CornerX - 80, CornerY - 4, 128, 56), b.Crop);
        for (int s = 369; s < 380; s++) Assert.Empty(r.Poll(s, r.Frames.Frame(Hp(24, 45), Hp(24, 70))));
        Assert.Equal(3, r.Events.Count);
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void Row_spec_and_settings_save_and_load()
    {
        var spec = AlertRowCatalog.Get(AlertIds.WarPillar);
        Assert.True(spec.Implemented);
        Assert.Equal("นาที", spec.Unit);
        Assert.Equal(10, spec.DefaultThreshold);
        Assert.Equal(C.WarPillar.DefaultMinutes, spec.DefaultThreshold);
        Assert.Equal((1, 120), (spec.MinThreshold, spec.MaxThreshold));

        var fresh = new AppSettings();
        Assert.True(fresh.IsAlertEnabled(AlertIds.WarPillar));                 // default on
        Assert.Equal(10, AlertRowCatalog.Threshold(fresh, spec));

        string path = Path.Combine(Path.GetTempPath(), "sw-pillar-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var s = new AppSettings();
            s.Thresholds[AlertIds.WarPillar] = 25;
            s.SetAlertEnabled(AlertIds.WarPillar, false);
            s.Save(path);
            var back = AppSettings.Load(path);
            Assert.False(back.IsAlertEnabled(AlertIds.WarPillar));
            Assert.Equal(25, AlertRowCatalog.Threshold(back, spec));
            foreach (int bad in new[] { 0, 121 })
            {
                back.Thresholds[AlertIds.WarPillar] = bad;
                Assert.Equal(10, AlertRowCatalog.Threshold(back, spec));
            }
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (IOException) { } }

        // The detector uses the option the UI sets (clamped to 1..120).
        var o = new HudOptions();
        var d = new WarPillarDetector(C, new HudReadings(), o);
        Assert.Equal(10, d.ThresholdMinutes);
        o.SetThreshold(AlertIds.WarPillar, 45);
        Assert.Equal(45, d.ThresholdMinutes);
        o.SetThreshold(AlertIds.WarPillar, 500);
        Assert.Equal(120, d.ThresholdMinutes);
    }
}

/// <summary>
/// Synthetic 1.0 clients built from warpillar_full_0014_1407.png: its panel (label box + icon,
/// 128x56 from the blue corner -80,-4) copied to slot i at +130*i px, each with its timer rewritten
/// in Tahoma 11 (the game's font: white, 1-px black outline, first digit's top-left ink at corner
/// +5,+28). Slots without a timer are left as the sample's scenery.
/// </summary>
public sealed class PanelFrames
{
    const int CornerX = 254, CornerY = 48, Pitch = 130;
    readonly Bitmap _base, _panel;

    public PanelFrames()
    {
        using var shot = new Bitmap(Path.Combine(TestData.SamplesDir, "from-chat", "warpillar_full_0014_1407.png"));
        _base = shot.Clone(new Rectangle(1, 1, 1024, 766), shot.PixelFormat);
        _panel = _base.Clone(new Rectangle(CornerX - 80, CornerY - 4, 128, 56), _base.PixelFormat);
        // Scenery where the sample's panel was: the dark strip just above it, stretched down.
        using var g = Graphics.FromImage(_base);
        using var bg = _base.Clone(new Rectangle(CornerX - 80, 30, 128, 8), _base.PixelFormat);
        g.DrawImage(bg, new Rectangle(CornerX - 80, CornerY - 4, 128, 56));
    }

    /// <summary>One panel: timer, HP % (the sample's full bar cut to it), fill colour and empty-track
    /// colour.</summary>
    public readonly record struct P(int Minutes, int Hp = 100, BarFill Fill = BarFill.Red, BarEmpty Empty = BarEmpty.Grey, double Flash = 0);

    public enum BarFill { Red, Orange, Yellow }
    public enum BarEmpty { Grey, DarkRed }

    /// <summary>Fill columns of the 35-px bar for an HP % (what the reader should report back).</summary>
    public static int Columns(int hp) => (int)Math.Round(BarLength * hp / 100.0);
    public static int ReadBack(int hp) => (int)Math.Round(100.0 * Columns(hp) / BarLength);
    const int BarLength = 35;

    public Frame Frame(params int[] minutes) => Frame(minutes.Select(m => new P(m)).ToArray());

    public Frame Frame(params P[] panels) => Draw(panels, i => (CornerX + Pitch * i, CornerY));

    /// <summary>Panels stacked downward (the game's layout, warpillar_two_stacked_1905.png): slot i's
    /// blue corner at (CornerX, CornerY + StackPitch*i).</summary>
    public Frame Stacked(params int[] minutes) => Stacked(minutes.Select(m => new P(m)).ToArray());

    public Frame Stacked(params P[] panels) => Draw(panels, i => (CornerX, CornerY + StackPitch * i));

    public const int StackPitch = 55;

    Frame Draw(P[] panels, Func<int, (int X, int Y)> corner)
    {
        using var bmp = new Bitmap(_base);
        using (var g = Graphics.FromImage(bmp))
            for (int i = 0; i < panels.Length; i++) g.DrawImageUnscaled(_panel, corner(i).X - 80, corner(i).Y - 4);
        for (int i = 0; i < panels.Length; i++)
        {
            var (cx, cy) = corner(i);
            WriteTimer(bmp, cx, cy, panels[i].Minutes);
            CutBar(bmp, cx, cy, panels[i]);
            if (panels[i].Flash > 0) Flash(bmp, cx, cy, panels[i].Flash);
        }
        return Imaging.Frame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
    }

    /// <summary>The bar (fill columns corner+8..+42, rows +43..+47): columns past the HP become
    /// empty track; the rest keep the sample's red shading, recoloured to orange/yellow.</summary>
    static void CutBar(Bitmap bmp, int cx, int cy, P p)
    {
        int cols = Columns(p.Hp);
        for (int dx = 0; dx < BarLength; dx++)
            for (int y = cy + 43; y <= cy + 47; y++)
            {
                int x = cx + 8 + dx;
                var c = bmp.GetPixel(x, y);
                if (dx >= cols)
                    bmp.SetPixel(x, y, p.Empty == BarEmpty.Grey ? Color.FromArgb(30, 30, 30) : Color.FromArgb(70, 18, 18));
                else if (p.Fill == BarFill.Orange)
                    bmp.SetPixel(x, y, Color.FromArgb(c.R, c.R * 55 / 100, c.B / 4));
                else if (p.Fill == BarFill.Yellow)
                    bmp.SetPixel(x, y, Color.FromArgb(c.R, c.R * 95 / 100, c.B / 4));
            }
    }

    /// <summary>The game's red/pink flash while the pillar is attacked (warpillar_redframe_1426.png),
    /// a = 1 fully red, 0.5 a mid-flash blend: every pixel of the icon box pushed to red (R -> 255,
    /// B down) except the white timer ink and outline (min channel >= 200) and the bar's fill/empty
    /// columns, which the game leaves as they are.</summary>
    static void Flash(Bitmap bmp, int cx, int cy, double a)
    {
        for (int y = cy - 1; y <= cy + 49; y++)
            for (int x = cx - 1; x <= cx + 45; x++)
            {
                if (y >= cy + 43 && y <= cy + 47 && x >= cx + 8 && x <= cx + 42) continue;
                var c = bmp.GetPixel(x, y);
                if (Math.Min(c.R, Math.Min(c.G, c.B)) >= 200) continue;
                bmp.SetPixel(x, y, Color.FromArgb((int)(c.R + (255 - c.R) * a), c.G, (int)(c.B * (1 - 0.35 * a))));
            }
    }

    static void WriteTimer(Bitmap bmp, int cx, int cy, int minutes)
    {
        // Wipe the old text (its white ink) inside the timer band, then draw the new one.
        for (int y = cy + 27; y <= cy + 36; y++)
            for (int x = cx + 4; x <= cx + 42; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (Math.Min(c.R, Math.Min(c.G, c.B)) >= 120) bmp.SetPixel(x, y, Color.FromArgb(40, 40, 40));
            }
        var mask = CountFont.TextMask("Tahoma", 11, WarPillarDetector.Format(minutes));
        // TextMask crops to ink: the top row is the digits' top (':' starts lower).
        Draw(bmp, mask, cx + 5, cy + 28, Color.White);
    }

    /// <summary>Light-grey tooltip text (222,222,222) with a black outline, like the game's.</summary>
    public static void Tooltip(Frame f, int x, int y, string text)
    {
        var mask = CountFont.TextMask("Tahoma", 11, text);
        var px = new byte[f.Width * f.Height * 4];
        f.CopyRect(new Rectangle(0, 0, f.Width, f.Height), px);
        using var bmp = new Bitmap(f.Width, f.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, f.Width, f.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
        Draw(bmp, mask, x, y, Color.FromArgb(222, 222, 222));
        f.CopyFrom(bmp, new Rectangle(0, 0, f.Width, f.Height));
    }

    static void Draw(Bitmap bmp, bool[,] mask, int x0, int y0, Color ink)
    {
        int h = mask.GetLength(0), w = mask.GetLength(1);
        for (int y = -1; y <= h; y++)
            for (int x = -1; x <= w; x++)
            {
                bool on = y >= 0 && x >= 0 && y < h && x < w && mask[y, x];
                if (on) { bmp.SetPixel(x0 + x, y0 + y, ink); continue; }
                bool near = false;
                for (int dy = -1; dy <= 1 && !near; dy++)
                    for (int dx = -1; dx <= 1 && !near; dx++)
                    {
                        int yy = y + dy, xx = x + dx;
                        near = yy >= 0 && xx >= 0 && yy < h && xx < w && mask[yy, xx];
                    }
                if (near) bmp.SetPixel(x0 + x, y0 + y, Color.Black);
            }
    }
}
