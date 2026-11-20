using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Tests;

/// <summary>"บัตร exp ครบ 30 นาที": emblem search on the buff bar sample, and the absent-for-10-s logic.</summary>
public class ExpCardTests
{
    // 1024x768 client at (1,8) of the screenshot; HUD 1.0. The card is the 10th buff icon at x 476..495, y 1..20.
    const string File = "from-chat/expcard_buffbar_full.png";
    const int ClientX = 1, ClientY = 8, W = 1024, H = 768, CardX = 476, CardY = 1;
    static readonly Color BarBackground = Color.FromArgb(0x13, 0x19, 0x1E);
    static readonly ExpCardConfig C = TestData.Config.ExpCard;

    /// <summary>The sample's client area, optionally changed (mask, shift), optionally stretched by <paramref name="up"/>.</summary>
    static Frame Client(Action<Graphics, Bitmap>? edit = null, double up = 1)
    {
        using var shot = new Bitmap(Path.Combine(TestData.SamplesDir, File));
        using var client = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(client))
            g.DrawImage(shot, new Rectangle(0, 0, W, H), new Rectangle(ClientX, ClientY, W, H), GraphicsUnit.Pixel);
        if (edit is not null)
            using (var g = Graphics.FromImage(client)) edit(g, client);
        if (up == 1) return Frame.FromBitmap(client, new Rectangle(0, 0, W, H));
        int w = (int)Math.Round(W * up), h = (int)Math.Round(H * up);
        using var big = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(client, new Rectangle(0, 0, w, h), 0, 0, W, H, GraphicsUnit.Pixel, ia);
        }
        return Frame.FromBitmap(big, new Rectangle(0, 0, w, h));
    }

    static void MaskCard(Graphics g, Bitmap _)
    {
        using var b = new SolidBrush(BarBackground);
        g.FillRectangle(b, CardX, CardY, 20, 20);
    }

    /// <summary>Moves the whole buff bar strip (rows 0..35, from x 250) right by dx; the gap is bar background.</summary>
    static Action<Graphics, Bitmap> ShiftBar(int dx) => (g, bmp) =>
    {
        using var strip = bmp.Clone(new Rectangle(250, 0, W - 250 - dx, 36), bmp.PixelFormat);
        using var b = new SolidBrush(BarBackground);
        g.FillRectangle(b, 250, 0, W - 250, 36);
        g.DrawImageUnscaled(strip, 250 + dx, 0);
    };

    static ExpCardMatcher Matcher() => new(C);

    [Fact]
    public void Sample_card_is_found_at_its_slot()
    {
        var (d, at) = Matcher().Find(Client(), HudScale.One);
        Assert.True(d <= C.MaxDistance, $"distance {d:F1}");
        Assert.Equal(new Point(CardX, CardY), at);
    }

    [Fact]
    public void Same_sample_with_card_masked_is_absent()
    {
        var m = Matcher();
        var (d, _) = m.Find(Client(MaskCard), HudScale.One);
        Assert.True(d > C.MaxDistance, $"distance {d:F1}");
        Assert.False(m.IsPresent(Client(MaskCard), HudScale.One));
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
        var (d, at) = Matcher().Find(Client(edit), HudScale.One);
        Assert.True(d <= C.MaxDistance, $"distance {d:F1}");
        Assert.Equal(new Point(CardX + dx, CardY), at);
    }

    [Fact]
    public void Stretched_hud_finds_card_and_not_masked_one()
    {
        // SYNTHETIC: bilinear stretch 1.3 (no real scaled sample of the buff bar yet).
        var scale = new HudScale(1.3, 1.3);
        var m = Matcher();
        var (d, _) = m.Find(Client(up: 1.3), scale);
        Assert.True(d <= C.MaxDistance, $"present distance {d:F1}");
        var (dm, _) = m.Find(Client(MaskCard, up: 1.3), scale);
        Assert.True(dm > C.MaxDistance, $"masked distance {dm:F1}");
    }

    static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0);

    [Fact]
    public void Flicker_shorter_than_limit_never_alerts()
    {
        var det = new ExpCardDetector(TestData.Config, new HudReadings());
        int t = 0;
        bool Poll(bool present) => det.Update(present, T0.AddSeconds(t++));
        Assert.False(Poll(true));
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 9; i++) Assert.False(Poll(false));   // 9 s absent (cursor over it, flicker)
            Assert.False(Poll(true));
        }
    }

    [Fact]
    public void Absent_for_limit_alerts_once_and_rearms_only_after_seen()
    {
        var readings = new HudReadings();
        var det = new ExpCardDetector(TestData.Config, readings);
        int t = 0, fired = 0;
        void Poll(bool present) { if (det.Update(present, T0.AddSeconds(t++))) fired++; }

        for (int i = 0; i < 30; i++) Poll(false);          // never seen: no alert
        Assert.Equal(0, fired);
        Poll(true);
        Assert.Equal(ExpCardDetector.HaveText, readings.Get(AlertIds.ExpCard).Text);
        for (int i = 0; i < 10; i++) Poll(false);           // absent at +0..+9 s
        Assert.Equal(0, fired);
        Poll(false);                                        // +10 s
        Assert.Equal(1, fired);
        Assert.Equal(ExpCardDetector.NoneText, readings.Get(AlertIds.ExpCard).Text);
        for (int i = 0; i < 60; i++) Poll(false);
        Assert.Equal(1, fired);
        Poll(true);                                         // new card
        for (int i = 0; i < 11; i++) Poll(false);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Paused_frames_restart_the_wait()
    {
        var det = new ExpCardDetector(TestData.Config, new HudReadings());
        Assert.False(det.Update(true, T0));
        for (int i = 1; i <= 8; i++) Assert.False(det.Update(false, T0.AddSeconds(i)));
        det.Reset();                                        // frames stopped for a minute
        for (int i = 70; i < 79; i++) Assert.False(det.Update(false, T0.AddSeconds(i)));
        Assert.True(det.Update(false, T0.AddSeconds(80)));
    }

    [Fact]
    public void Sample_frames_raise_one_alert_with_buff_bar_crop()
    {
        var det = new ExpCardDetector(TestData.Config, new HudReadings());
        var present = Client();
        var gone = Client(MaskCard);
        var events = new List<WatchEvent>();
        det.Process(present, HudScale.One, T0, events);
        for (int i = 1; i <= 15; i++) det.Process(gone, HudScale.One, T0.AddSeconds(i), events);
        var e = Assert.Single(events);
        Assert.Equal(AlertIds.ExpCard, e.AlertId);
        Assert.Equal(C.Message, e.Message);
        Assert.Equal(new Rectangle(250, 0, W - 250, 36), e.Crop);
    }

    [Fact]
    public void Not_watched_by_unreadable_rule()
    {
        Assert.DoesNotContain(TestData.Config.Unreadable.Rows, r => r[0] == AlertIds.ExpCard);
    }
}
