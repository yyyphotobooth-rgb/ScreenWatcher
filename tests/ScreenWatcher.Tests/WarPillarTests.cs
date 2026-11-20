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
        Assert.Equal("00:15 · 00:42", r.Text);
        Assert.False(r.Readings.Get(AlertIds.WarPillar).Reached);
        r.Poll(2, 125);
        Assert.Equal("02:05", r.Text);
        r.Poll(3);
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
        var fire = new List<(int Slot, int Minutes)>();
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
        var fire = new List<(int Slot, int Minutes)>();
        t.Update([5], T0, 10, fire);
        t.Update([], T0.AddSeconds(5), 10, fire);
        Assert.Equal(0, t.Count);
        Assert.Empty(fire);
    }

    [Fact]
    public void Pillars_are_forgotten_after_their_countdown_ended()
    {
        var t = new WarPillarTracker(2, 2);
        var fire = new List<(int Slot, int Minutes)>();
        t.Update([3], T0, 10, fire);
        t.Update([3], T0.AddSeconds(1), 10, fire);
        Assert.Single(fire);
        t.Update([], T0.AddMinutes(4), 10, fire);
        Assert.Equal(1, t.Count);
        t.Update([], T0.AddMinutes(6), 10, fire);
        Assert.Equal(0, t.Count);
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
        Assert.Equal("00:30 · 00:40", r.Text);
        for (int s = 3; s <= 13; s++) Both(s, covered);   // "?" from 3 s: 10 s at 13 s, not longer yet
        Assert.Equal(RowReading.Unknown, r.Text);
        Assert.Empty(events);
        for (int s = 14; s <= 17; s++) Both(s, covered);   // crosses at 14 s, combine window 3 s
        var e = Assert.Single(events);
        Assert.Equal(AlertIds.Unreadable, e.AlertId);
        Assert.Equal("อ่านค่าไม่ได้ : เสาวอ", e.Message);
        Both(18, r.Frames.Frame(30, 40));
        Assert.Equal("00:30 · 00:40", r.Text);
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
        Assert.False(Diagnostics.RowStats.TryValue("ไม่มี", out _));
        Assert.False(Diagnostics.RowStats.TryValue("?", out _));
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
sealed class PanelFrames
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

    public Frame Frame(params int[] minutes)
    {
        using var bmp = new Bitmap(_base);
        using (var g = Graphics.FromImage(bmp))
            for (int i = 0; i < minutes.Length; i++) g.DrawImageUnscaled(_panel, CornerX - 80 + Pitch * i, CornerY - 4);
        for (int i = 0; i < minutes.Length; i++) WriteTimer(bmp, CornerX + Pitch * i, CornerY, minutes[i]);
        return Imaging.Frame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
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
