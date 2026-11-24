using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;
using ScreenWatcher.UI;

namespace ScreenWatcher.Tests;

/// <summary>Buff rows on the kill row: "บัพมอน 300 ตัว" (EXP card, alert id expCard) and "บัพจับมือ"
/// (handshake). Emblem search on the buff bar samples, the absent-for-10-s logic, the buff-bar-only
/// alert picture, labels and the settings carried over from 1.2.</summary>
public class BuffTests
{
    // Old sample: 1024x768 client at (1,8) of the screenshot; HUD 1.0; dark bar background.
    // EXP card = 10th icon at x 476..495, handshake at x 356..375, icons y 1..20.
    const string Old = "from-chat/expcard_buffbar_full.png";
    const int OldX = 1, OldY = 8, OldW = 1024, OldH = 768, CardX = 476, CardY = 1;
    // New sample: native /ss, 1920x1080 client (HUD measured 1.00), bar drawn over the sky.
    // Handshake = 5th icon at x 356..375 (template from x 357), EXP card = 8th icon at x 428.
    const string New = "from-chat/buffbar_handshake_and_kill300_1920x1080.png";
    const int NewW = 1920, NewH = 1080, NewHandX = 357, NewCardX = 428;
    const int HandIconX = 356;
    static readonly Color BarBackground = Color.FromArgb(0x13, 0x19, 0x1E);
    static readonly BuffConfig C = TestData.Config.ExpCard;
    static readonly BuffConfig H = TestData.Config.Handshake;

    /// <summary>A sample's client area, optionally changed (mask, shift), optionally stretched by <paramref name="up"/>.</summary>
    static Frame Client(string file, int cx, int cy, int w, int h, Action<Graphics, Bitmap>? edit = null, double up = 1)
    {
        using var shot = new Bitmap(Path.Combine(TestData.SamplesDir, file));
        using var client = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(client))
            g.DrawImage(shot, new Rectangle(0, 0, w, h), new Rectangle(cx, cy, w, h), GraphicsUnit.Pixel);
        if (edit is not null)
            using (var g = Graphics.FromImage(client)) edit(g, client);
        if (up == 1) return Frame.FromBitmap(client, new Rectangle(0, 0, w, h));
        int bw = (int)Math.Round(w * up), bh = (int)Math.Round(h * up);
        using var big = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(client, new Rectangle(0, 0, bw, bh), 0, 0, w, h, GraphicsUnit.Pixel, ia);
        }
        return Frame.FromBitmap(big, new Rectangle(0, 0, bw, bh));
    }

    static Frame OldClient(Action<Graphics, Bitmap>? edit = null, double up = 1) => Client(Old, OldX, OldY, OldW, OldH, edit, up);
    static Frame NewClient(Action<Graphics, Bitmap>? edit = null, double up = 1) => Client(New, 0, 0, NewW, NewH, edit, up);

    static void MaskCard(Graphics g, Bitmap _)
    {
        using var b = new SolidBrush(BarBackground);
        g.FillRectangle(b, CardX, CardY, 20, 20);
    }

    /// <summary>Paints a 20x20 icon (and its text rows) with what is just left of it, row by row:
    /// bar background in the old sample, sky in the new one.</summary>
    static Action<Graphics, Bitmap> MaskIcon(int iconX) => (_, bmp) =>
    {
        for (int y = 0; y < 22; y++)
        {
            var c = bmp.GetPixel(iconX - 3, y);
            for (int x = iconX - 2; x < iconX + 22; x++) bmp.SetPixel(x, y, c);
        }
    };

    /// <summary>Moves the whole buff bar strip (rows 0..35, from x 250) right by dx; the gap is bar background.</summary>
    static Action<Graphics, Bitmap> ShiftBar(int dx) => (g, bmp) =>
    {
        using var strip = bmp.Clone(new Rectangle(250, 0, OldW - 250 - dx, 36), bmp.PixelFormat);
        using var b = new SolidBrush(BarBackground);
        g.FillRectangle(b, 250, 0, OldW - 250, 36);
        g.DrawImageUnscaled(strip, 250 + dx, 0);
    };

    static BuffMatcher CardMatcher() => new(C);
    static BuffMatcher HandMatcher() => new(H);
    /// <summary>Same emblem with no limit: the best spot anywhere (how close a wrong icon gets).</summary>
    static double BestAnywhere(BuffConfig c, Frame f, HudScale s) => new BuffMatcher(c with { MaxDistance = 255 }).Find(f, s).Distance;

    // ---- EXP card on the old 1031x778 sample (1.2 tests, unchanged) ----

    [Fact]
    public void Old_sample_card_is_found_at_its_slot()
    {
        var (d, at) = CardMatcher().Find(OldClient(), HudScale.One);
        Assert.True(d <= C.MaxDistance, $"distance {d:F1}");
        Assert.Equal(new Point(CardX, CardY), at);
    }

    [Fact]
    public void Old_sample_with_card_masked_is_absent()
    {
        var m = CardMatcher();
        var (d, _) = m.Find(OldClient(MaskCard), HudScale.One);
        Assert.True(d > C.MaxDistance, $"distance {d:F1}");
        Assert.False(m.IsPresent(OldClient(MaskCard), HudScale.One));
    }

    [Theory]
    [InlineData(137)]    // not a multiple of the icon pitch
    [InlineData(-200)]   // further left (fewer buffs before it)
    public void Shifted_card_is_still_found(int dx)
    {
        Action<Graphics, Bitmap> edit = dx >= 0 ? ShiftBar(dx) : (g, bmp) =>
        {
            // Left: copy the card's 20x20 icon to x+dx and mask the original.
            using var icon = bmp.Clone(new Rectangle(CardX, CardY, 20, 20), bmp.PixelFormat);
            MaskCard(g, bmp);
            g.DrawImageUnscaled(icon, CardX + dx, CardY);
        };
        var (d, at) = CardMatcher().Find(OldClient(edit), HudScale.One);
        Assert.True(d <= C.MaxDistance, $"distance {d:F1}");
        Assert.Equal(new Point(CardX + dx, CardY), at);
    }

    [Fact]
    public void Old_sample_stretched_hud_finds_card_and_not_masked_one()
    {
        // SYNTHETIC: bilinear stretch 1.3 (no real scaled sample of the buff bar yet).
        var scale = new HudScale(1.3, 1.3);
        var m = CardMatcher();
        var (d, _) = m.Find(OldClient(up: 1.3), scale);
        Assert.True(d <= C.MaxDistance, $"present distance {d:F1}");
        var (dm, _) = m.Find(OldClient(MaskCard, up: 1.3), scale);
        Assert.True(dm > C.MaxDistance, $"masked distance {dm:F1}");
    }

    // ---- Handshake on the old sample (it is there too, x 356) ----

    [Fact]
    public void Old_sample_handshake_found_and_absent_when_masked()
    {
        var (d, at) = HandMatcher().Find(OldClient(), HudScale.One);
        Assert.True(d <= H.MaxDistance, $"distance {d:F1}");
        Assert.Equal(new Point(NewHandX, 1), at);
        var masked = OldClient(MaskIcon(HandIconX));
        double best = BestAnywhere(H, masked, HudScale.One);
        Assert.True(best > H.MaxDistance, $"best wrong spot {best:F1}");
        Assert.True(CardMatcher().IsPresent(masked, HudScale.One), "EXP card must still be found");
    }

    // ---- New 1920x1080 sample: both buffs ----

    [Fact]
    public void New_sample_hud_scale_is_measured_one()
    {
        // The game keeps the HUD at 1.0 up to a 1080 px client; this sample is NOT a scale > 1 sample.
        var m = new HudScaleLocator(TestData.Config.HudScale).Measure(NewClient());
        Assert.True(m.Found);
        Assert.Equal(1.0, m.Scale.X, 2);
        Assert.Equal(1.0, m.Scale.Y, 2);
    }

    [Fact]
    public void New_sample_finds_both_buffs_at_their_slots()
    {
        var f = NewClient();
        var (dh, ah) = HandMatcher().Find(f, HudScale.One);
        Assert.True(dh <= H.MaxDistance, $"handshake distance {dh:F1}");
        Assert.Equal(new Point(NewHandX, 1), ah);
        var (dc, ac) = CardMatcher().Find(f, HudScale.One);
        Assert.True(dc <= C.MaxDistance, $"card distance {dc:F1}");
        Assert.Equal(new Point(NewCardX, 1), ac);
    }

    [Fact]
    public void New_sample_handshake_masked_is_absent_card_still_found()
    {
        var f = NewClient(MaskIcon(HandIconX));
        double best = BestAnywhere(H, f, HudScale.One);
        Assert.True(best > H.MaxDistance, $"best wrong spot {best:F1}");
        Assert.False(HandMatcher().IsPresent(f, HudScale.One));
        Assert.True(CardMatcher().IsPresent(f, HudScale.One));
    }

    [Fact]
    public void New_sample_card_masked_is_absent_handshake_still_found()
    {
        var f = NewClient(MaskIcon(NewCardX));
        double best = BestAnywhere(C, f, HudScale.One);
        Assert.True(best > C.MaxDistance, $"best wrong spot {best:F1}");
        Assert.False(CardMatcher().IsPresent(f, HudScale.One));
        Assert.True(HandMatcher().IsPresent(f, HudScale.One));
    }

    [Theory]
    [InlineData(1.3)]
    [InlineData(1.5)]
    public void New_sample_stretched_hud_finds_both_and_not_masked(double up)
    {
        // SYNTHETIC: the only real sample is HUD 1.0, so scale > 1 is a bilinear stretch of it.
        var scale = new HudScale(up, up);
        var f = NewClient(up: up);
        var (dh, _) = HandMatcher().Find(f, scale);
        Assert.True(dh <= H.MaxDistance, $"handshake distance {dh:F1}");
        var (dc, _) = CardMatcher().Find(f, scale);
        Assert.True(dc <= C.MaxDistance, $"card distance {dc:F1}");
        Assert.True(BestAnywhere(H, NewClient(MaskIcon(HandIconX), up), scale) > H.MaxDistance);
        Assert.True(BestAnywhere(C, NewClient(MaskIcon(NewCardX), up), scale) > C.MaxDistance);
    }

    [Fact]
    public void Handshake_crop_from_another_capture_matches()
    {
        // samples/from-chat/handshake_icon_crop_26x20.png: a different moment ("35 นา"), icon at x 3.
        var crop = TestData.ClientFrame("from-chat/handshake_icon_crop_26x20.png", 0, 0, 26, 20);
        var (d, at) = HandMatcher().Find(crop, HudScale.One);
        Assert.True(d <= H.MaxDistance, $"distance {d:F1}");
        Assert.Equal(new Point(4, 0), at);
    }

    // ---- Alert logic ----

    static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0);

    public static TheoryData<string> Ids => [AlertIds.ExpCard, AlertIds.Handshake];

    static BuffDetector Detector(string id, HudReadings r) =>
        id == AlertIds.ExpCard ? BuffDetector.ExpCard(TestData.Config, r) : BuffDetector.Handshake(TestData.Config, r);

    [Theory, MemberData(nameof(Ids))]
    public void Flicker_shorter_than_limit_never_alerts(string id)
    {
        var det = Detector(id, new HudReadings());
        int t = 0;
        bool Poll(bool present) => det.Update(present, T0.AddSeconds(t++));
        Assert.False(Poll(true));
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 9; i++) Assert.False(Poll(false));   // 9 s absent (cursor over it, flicker)
            Assert.False(Poll(true));
        }
    }

    [Theory, MemberData(nameof(Ids))]
    public void Absent_for_limit_alerts_once_and_rearms_only_after_seen(string id)
    {
        var readings = new HudReadings();
        var det = Detector(id, readings);
        int t = 0, fired = 0;
        void Poll(bool present) { if (det.Update(present, T0.AddSeconds(t++))) fired++; }

        for (int i = 0; i < 30; i++) Poll(false);          // never seen: no alert
        Assert.Equal(0, fired);
        Poll(true);
        Assert.Equal("มีบัพ", readings.Get(id).Text);
        for (int i = 0; i < 10; i++) Poll(false);           // absent at +0..+9 s
        Assert.Equal(0, fired);
        Poll(false);                                        // +10 s
        Assert.Equal(1, fired);
        Assert.Equal("ไม่มีบัพ", readings.Get(id).Text);
        for (int i = 0; i < 60; i++) Poll(false);
        Assert.Equal(1, fired);
        Poll(true);                                         // new buff
        for (int i = 0; i < 11; i++) Poll(false);
        Assert.Equal(2, fired);
    }

    [Theory, MemberData(nameof(Ids))]
    public void Paused_frames_restart_the_wait(string id)
    {
        var det = Detector(id, new HudReadings());
        Assert.False(det.Update(true, T0));
        for (int i = 1; i <= 8; i++) Assert.False(det.Update(false, T0.AddSeconds(i)));
        det.Reset();                                        // frames stopped for a minute
        for (int i = 70; i < 79; i++) Assert.False(det.Update(false, T0.AddSeconds(i)));
        Assert.True(det.Update(false, T0.AddSeconds(80)));
    }

    /// <summary>Both detectors on the real 1920x1080 frames: the handshake disappears, then the card.
    /// 9 s gap: nothing; at 10 s: exactly one alert each, with its own message and the buff bar crop.</summary>
    [Fact]
    public void New_sample_frames_raise_one_alert_each_after_10_s()
    {
        var both = NewClient();
        var noHand = NewClient(MaskIcon(HandIconX));
        var none = NewClient((g, b) => { MaskIcon(HandIconX)(g, b); MaskIcon(NewCardX)(g, b); });
        var r = new HudReadings();
        var card = BuffDetector.ExpCard(TestData.Config, r);
        var hand = BuffDetector.Handshake(TestData.Config, r);
        var events = new List<WatchEvent>();
        void Poll(Frame f, int s) { card.Process(f, HudScale.One, T0.AddSeconds(s), events); hand.Process(f, HudScale.One, T0.AddSeconds(s), events); }

        Poll(both, 0);
        Assert.Equal("มีบัพ", r.Get(AlertIds.ExpCard).Text);
        Assert.Equal("มีบัพ", r.Get(AlertIds.Handshake).Text);
        for (int s = 1; s <= 9; s++) Poll(noHand, s);       // handshake gone 1..9: 8 s
        Poll(both, 10);                                     // seen again: wait restarts
        for (int s = 11; s <= 20; s++) Poll(noHand, s);     // gone 11..20: 9 s
        Assert.Empty(events);
        Assert.Equal("ไม่มีบัพ", r.Get(AlertIds.Handshake).Text);
        Assert.Equal("มีบัพ", r.Get(AlertIds.ExpCard).Text);
        Poll(noHand, 21);                                   // 10 s
        var e = Assert.Single(events);
        Assert.Equal(AlertIds.Handshake, e.AlertId);
        Assert.Equal("บัพจับมือ ครบ 60 นาทีแล้ว", e.Message);
        for (int s = 22; s <= 31; s++) Poll(none, s);       // card gone 22..31: 9 s
        Assert.Single(events);
        Poll(none, 32);                                     // 10 s
        Assert.Equal(2, events.Count);
        Assert.Equal(AlertIds.ExpCard, events[1].AlertId);
        Assert.Equal("บัพมอน 300 ตัว ครบ 30 นาทีแล้ว", events[1].Message);
        for (int s = 33; s <= 60; s++) Poll(none, s);
        Assert.Equal(2, events.Count);
        Assert.Equal("ไม่มีบัพ", r.Get(AlertIds.ExpCard).Text);
        foreach (var ev in events) Assert.Equal(new Rectangle(250, 0, 695 + 1 + 24 - 250, 22), ev.Crop);
    }

    // ---- Alert picture: the buff bar only ----

    [Fact]
    public void Old_sample_alert_crop_is_buff_bar_only()
    {
        var det = BuffDetector.ExpCard(TestData.Config, new HudReadings());
        var events = new List<WatchEvent>();
        det.Process(OldClient(), HudScale.One, T0, events);
        for (int i = 1; i <= 15; i++) det.Process(OldClient(MaskCard), HudScale.One, T0.AddSeconds(i), events);
        var e = Assert.Single(events);
        // Icons from x ~258, last countdown text x 719; 22 rows: the target HP bar (y >= ~25) is left out.
        Assert.Equal(new Rectangle(250, 0, 719 + 1 + 24 - 250, 22), e.Crop);
    }

    [Fact]
    public void New_sample_crop_is_buff_bar_only()
    {
        var crop = BuffBar.CropRect(NewClient(), HudScale.One, TestData.Config.BuffBar);
        // Last countdown text x 695; the target name/HP bar starts at x ~780, the quest text at ~1510.
        Assert.Equal(new Rectangle(250, 0, 695 + 1 + 24 - 250, 22), crop);
        Assert.True(crop.Right < 780);
    }

    [Fact]
    public void Crop_scales_with_hud_and_falls_back_without_text()
    {
        var c = TestData.Config.BuffBar;
        var big = BuffBar.CropRect(NewClient(up: 1.3), new HudScale(1.3, 1.3), c);
        Assert.Equal(new HudScale(1.3, 1.3).MapX(250), big.X);
        Assert.Equal(29, big.Height);                       // 22 * 1.3
        Assert.InRange(big.Right, (int)(695 * 1.3), (int)(695 * 1.3) + 40);
        // No countdown text at all (blank frame): fixed width.
        var blank = Frame.FromBitmap(new Bitmap(1024, 768), new Rectangle(0, 0, 1024, 768));
        Assert.Equal(new Rectangle(250, 0, c.FallbackWidth, 22), BuffBar.CropRect(blank, HudScale.One, c));
    }

    // ---- Labels, texts, settings ----

    [Fact]
    public void Kill_row_lines_and_texts_are_exact()
    {
        var row = AlertRowCatalog.Get(AlertIds.Kill300);
        Assert.Equal("ยิงมอนครบ 300 ตัว", row.Label);
        Assert.Equal([new SubLine("expCard", "บัพมอน 300 ตัว"), new SubLine("handshake", "บัพจับมือ")], row.Subs!);
        Assert.Equal(108, row.Height);                      // three lines of 36
        Assert.Equal("บัพมอน 300 ตัว ครบ 30 นาทีแล้ว", C.Message);
        Assert.Equal("บัพจับมือ ครบ 60 นาทีแล้ว", H.Message);
        Assert.Equal("มีบัพ", BuffDetector.HaveText);
        Assert.Equal("ไม่มีบัพ", BuffDetector.NoneText);
        Assert.Equal(10, C.AbsentSeconds);
        Assert.Equal(10, H.AbsentSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settings_from_1_2_keep_exp_card_checkbox(bool saved)
    {
        // A 1.2 settings.json (EXP card line was "บัตร exp ครบ 30 นาที", same id "expCard"; no handshake yet).
        string dir = Path.Combine(Path.GetTempPath(), "sw-buff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, $$"""
                { "CollectMode": "off", "AlertsEnabled": { "kill300": true, "expCard": {{(saved ? "true" : "false")}} },
                  "Thresholds": { "warPillar": 10 } }
                """);
            var s = AppSettings.Load(path);
            Assert.Equal(saved, s.IsAlertEnabled(AlertIds.ExpCard));
            Assert.True(s.IsAlertEnabled(AlertIds.Handshake));   // new line: default on
            s.SetAlertEnabled(AlertIds.Handshake, false);
            s.Save(path);
            var again = AppSettings.Load(path);
            Assert.Equal(saved, again.IsAlertEnabled(AlertIds.ExpCard));
            Assert.False(again.IsAlertEnabled(AlertIds.Handshake));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Not_watched_by_unreadable_rule()
    {
        Assert.DoesNotContain(TestData.Config.Unreadable.Rows, r => r[0] == AlertIds.ExpCard);
        Assert.DoesNotContain(TestData.Config.Unreadable.Rows, r => r[0] == AlertIds.Handshake);
    }
}
