using System.Drawing;
using System.Drawing.Imaging;
using ScreenWatcher.Bot;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;
using Xunit.Abstractions;

namespace ScreenWatcher.Tests;

/// <summary>
/// Row "ช่องเก็บของเต็ม" (field trial 2026-11-19): the yellow line
/// "ช่องเก็บของของคุณมีพื้นที่ไม่เพียงพอ" in the system message box (inventory_full_message.png,
/// 1024x768, framed box). The font is Windows' Tahoma 12 px unsmoothed (pixel exact).
/// </summary>
public class InventoryTests(ITestOutputHelper output)
{
    static readonly WatcherConfig C = TestData.Config;
    static readonly SystemMessageReader Reader = new(C.Inventory);
    static readonly DateTime T0 = new(2026, 11, 19, 12, 0, 0);
    static string Field(string f) => Path.Combine(TestData.SamplesDir, "field-2026-11-19", f);

    static Frame Load(string path)
    {
        using var bmp = new Bitmap(path);
        return Frame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
    }

    [Fact]
    public void The_phrase_is_Tahoma_12_px_pixel_for_pixel()
    {
        var mask = CountFont.TextMask("Tahoma", 12, C.Inventory.Phrase);
        using var bmp = new Bitmap(Field("inventory_full_message.png"));
        static bool Yellow(Color c) => c.R >= 230 && c.G >= 240 && c.B <= 40;
        // The yellow line's ink box in the sample (the only yellow in x 690..1019, y 630..655).
        int x0 = int.MaxValue, y0 = int.MaxValue;
        for (int y = 630; y < 656; y++)
            for (int x = 690; x < 1020; x++)
                if (Yellow(bmp.GetPixel(x, y))) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); }
        output.WriteLine($"ink box top-left ({x0},{y0})");
        int same = 0;
        for (int y = 0; y < mask.GetLength(0); y++)
            for (int x = 0; x < mask.GetLength(1); x++)
                if (Yellow(bmp.GetPixel(x0 + x, y0 + y)) == mask[y, x]) same++;
        Assert.Equal((177, 14), (mask.GetLength(1), mask.GetLength(0)));
        Assert.Equal(mask.Length, same);
    }

    [Fact]
    public void The_sample_shows_the_phrase_in_the_framed_box()
    {
        var r = Reader.Read(Load(Field("inventory_full_message.png")), HudScale.One);
        Assert.True(r.Phrase);
        Assert.True(r.Title);
        Assert.True(r.Text);
    }

    [Fact]
    public void No_other_sample_or_trial_frame_shows_the_phrase()
    {
        var files = Directory.GetFiles(TestData.SamplesDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith("inventory_full_message.png", StringComparison.OrdinalIgnoreCase))
            .Where(f => { using var b = Image.FromFile(f); return b.Width >= 1000 && b.Height >= 700; })
            .ToList();
        foreach (var f in files)
        {
            var frame = Load(f);
            foreach (var s in new[] { HudScale.One, new HudScale(1.305, 1.3) })
                Assert.False(Reader.Read(frame, s).Phrase, $"{f} at {s}");
        }
        output.WriteLine($"{files.Count} full-size images, no phrase");
        Assert.True(files.Count >= 30);
    }

    public static IEnumerable<object[]> FramedTitleVisible() =>
        new[] { "inventory_full_message.png", "false_shield_out_full.jpg", "false_sp_out_full.jpg", "levelup_quest_popup_full.jpg",
                "unreadable_in_town_shop_full.jpg", "full/004703_335_kill300_full.jpg", "full/021010_333_kill300_full.jpg",
                "full/024813_331_kill300_full.jpg", "full/032714_336_kill300_full.jpg", "full/095356_331_unreadable_full.jpg",
                "full/095643_337_full_event_unreadable.png", "full/095647_327_unreadable_full.jpg", "full/103335_340_kill300_full.jpg" }
            .Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(FramedTitleVisible))]
    public void The_framed_box_is_found_by_its_title(string file) =>
        Assert.True(Reader.Read(Load(Field(file)), HudScale.One).Title, file);

    [Theory]
    // The game's notification popup (operator portrait) covers the box; 09:53:52 another window covers the title.
    [InlineData("full/012821_333_kill300_full.jpg")]
    [InlineData("full/013353_581_miss_full.jpg")]
    [InlineData("full/062059_332_fuelLow_full.jpg")]
    [InlineData("full/083814_332_fuelLow_full.jpg")]
    [InlineData("full/095352_339_full_event_unreadable.png")]
    public void A_covered_title_is_not_found(string file) =>
        Assert.False(Reader.Read(Load(Field(file)), HudScale.One).Title, file);

    [Theory]
    // Older samples: the box without its window frame, text straight over the scene (1600x1024 and 1024x768).
    [InlineData("live_printwindow.png")]
    [InlineData("live2_printwindow.png")]
    [InlineData("live3_printwindow.png")]
    public void A_frameless_box_is_seen_by_its_text_and_has_no_title(string file)
    {
        var r = Reader.Read(TestData.ClientFrame(file, 0, 0, 1600, 1024), HudScale.One);
        Assert.False(r.Title);
        Assert.True(r.Text);
        Assert.False(r.Phrase);
    }

    /// <summary>The sample with the yellow line moved up two lines (lines scroll).</summary>
    [Fact]
    public void The_phrase_is_found_on_any_line_of_the_box()
    {
        using var bmp = new Bitmap(Field("inventory_full_message.png"));
        using var moved = new Bitmap(bmp);
        using (var g = Graphics.FromImage(moved))
        {
            g.DrawImage(bmp, new Rectangle(690, 628 - 32, 334, 28), new Rectangle(690, 628, 334, 28), GraphicsUnit.Pixel);
            g.FillRectangle(Brushes.Black, 690, 628, 334, 28);
        }
        var frame = Frame.FromBitmap(moved, new Rectangle(0, 0, 1024, 768));
        Assert.True(Reader.Read(frame, HudScale.One).Phrase);
        // And with the line gone altogether: no phrase.
        using (var g = Graphics.FromImage(moved)) g.FillRectangle(Brushes.Black, 690, 596, 334, 28);
        Assert.False(Reader.Read(Frame.FromBitmap(moved, new Rectangle(0, 0, 1024, 768)), HudScale.One).Phrase);
    }

    /// <summary>SYNTHETIC ~1.3 frame: the game draws text in a larger Tahoma at a large window (item
    /// counts: 11 px at 1.0, 15 px at ~1.3); no real ~1.3 frame with the phrase exists. The phrase is
    /// drawn at 16 px in the game's yellow with a black outline where the box sits on a 2560x1417 client.</summary>
    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    public void A_larger_Tahoma_at_a_stretched_HUD_is_found(int height)
    {
        var mask = CountFont.TextMask("Tahoma", height, C.Inventory.Phrase);
        int w = 2560, h = 1417;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(20, 40, 60));
        int x0 = w - (int)(330 * 1.3), y0 = h - (int)(120 * 1.3);
        for (int y = -1; y <= mask.GetLength(0); y++)
            for (int x = -1; x <= mask.GetLength(1); x++)
            {
                bool on = y >= 0 && x >= 0 && y < mask.GetLength(0) && x < mask.GetLength(1) && mask[y, x];
                if (on) bmp.SetPixel(x0 + x, y0 + y, Color.FromArgb(246, 255, 0));
                else bmp.SetPixel(x0 + x, y0 + y, Color.Black);
            }
        var r = Reader.Read(Frame.FromBitmap(bmp, new Rectangle(0, 0, w, h)), new HudScale(1.305, 1.3));
        Assert.True(r.Phrase);
    }

    // ------------------------------------------------------------- row logic

    static (InventoryDetector D, HudReadings R, List<WatchEvent> E) NewRow()
    {
        var readings = new HudReadings();
        return (new InventoryDetector(C, readings), readings, []);
    }

    static readonly Rectangle Box = new(684, 576, 340, 150);
    static SystemMessageReading Framed(bool phrase = false) => new() { Title = true, Text = true, Phrase = phrase };

    static void Feed(InventoryDetector d, List<WatchEvent> events, int from, int to, SystemMessageReading r)
    {
        for (int s = from; s < to; s++) d.Update(r, T0.AddSeconds(s), Box, events);
    }

    [Fact]
    public void Alerts_once_with_the_box_crop_and_stays_latched_until_reset()
    {
        var (d, readings, events) = NewRow();
        Feed(d, events, 0, 5, Framed());
        Assert.Equal(new RowReading("ปกติ", false), readings.Get(AlertIds.InventoryFull));
        Feed(d, events, 5, 60, Framed(phrase: true));
        var e = Assert.Single(events);
        Assert.Equal("ห้องเก็บสัมภาระเต็ม!", e.Message);
        Assert.Equal(AlertIds.InventoryFull, e.AlertId);
        Assert.Equal(Box, e.Crop);
        Assert.Equal(new RowReading("เต็ม (รอรีเซ็ต)", true), readings.Get(AlertIds.InventoryFull));
        Feed(d, events, 60, 90, Framed());                 // line scrolled away: still latched
        Feed(d, events, 90, 100, Framed(phrase: true));    // printed again: still one alert
        Assert.Single(events);
        Assert.True(d.Latched);

        // Pause / no frames: the row blanks, the latch stays.
        d.Reset();
        Assert.Equal(RowReading.None, readings.Get(AlertIds.InventoryFull).Text);
        Feed(d, events, 100, 102, Framed());
        Assert.Equal("เต็ม (รอรีเซ็ต)", readings.Get(AlertIds.InventoryFull).Text);

        // Reset: the old line still in the box does not alert again; a new fill after it is gone does.
        d.RequestReset();
        Feed(d, events, 102, 110, Framed(phrase: true));
        Assert.Single(events);
        Assert.Equal(new RowReading("ปกติ", false), readings.Get(AlertIds.InventoryFull));
        Feed(d, events, 110, 113, Framed());
        Feed(d, events, 113, 115, Framed(phrase: true));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void A_new_app_starts_un_latched()
    {
        var (d, readings, events) = NewRow();
        Assert.False(d.Latched);
        Assert.Equal(RowReading.None, readings.Get(AlertIds.InventoryFull).Text);
        Feed(d, events, 0, 1, Framed(phrase: true));
        Assert.Single(events);
    }

    [Fact]
    public void Framed_box_not_seen_shows_a_question_mark_and_a_frameless_empty_box_gives_no_news()
    {
        var (d, readings, events) = NewRow();
        Feed(d, events, 0, 3, Framed());
        Feed(d, events, 3, 5, new SystemMessageReading());
        Assert.Equal("ปกติ", readings.Get(AlertIds.InventoryFull).Text);   // two polls: not yet
        Feed(d, events, 5, 6, new SystemMessageReading());
        Assert.Equal(RowReading.Unknown, readings.Get(AlertIds.InventoryFull).Text);
        Feed(d, events, 6, 7, Framed());
        Assert.Equal("ปกติ", readings.Get(AlertIds.InventoryFull).Text);

        var (f, fr, fe) = NewRow();
        Feed(f, fe, 0, 3, new SystemMessageReading { Text = true });      // frameless with text
        Feed(f, fe, 3, 60, new SystemMessageReading());                   // text faded away
        Assert.Equal("ปกติ", fr.Get(AlertIds.InventoryFull).Text);
        Assert.Empty(fe);
    }

    [Fact]
    public void Box_not_found_longer_than_the_limit_goes_out_as_the_generic_unreadable_message()
    {
        var readings = new HudReadings();
        var options = new HudOptions();
        var d = new InventoryDetector(C, readings);
        var u = new UnreadableDetector(C, readings, options, () => 0);
        foreach (var r in C.Unreadable.Rows) if (r[0] != AlertIds.InventoryFull) readings.Set(r[0], "20");
        var events = new List<WatchEvent>();
        for (int s = 0; s < 30; s++)
        {
            d.Update(s < 5 ? Framed() : new SystemMessageReading(), T0.AddSeconds(s), Box, events);
            u.Process(T0.AddSeconds(s), 1024, 768, events);
        }
        var e = Assert.Single(events);
        Assert.Equal("เรดาร์ถูกบัง อ่านค่าไม่ได้: กล่องข้อความระบบ", e.Message);
        Assert.Equal(AlertIds.InventoryFull, Assert.Single(e.Unreadable!).Id);
    }

    [Fact]
    public void Its_alert_and_its_question_mark_collect_the_message_box_region()
    {
        var names = C.Collect.AllRegions.Select(r => r.Name).ToArray();
        var rules = new AutoCollectRules(C, names);
        int box = rules.IndexOf("msgbox");
        Assert.True(box >= 0);
        string text = "ปกติ";
        AutoInputs In(int s) => new(T0.AddSeconds(s), HudScale.One, true, id => id == AlertIds.InventoryFull ? text : RowReading.None, _ => 0);
        rules.NoteAlert(new WatchEvent(WatchEventKind.Hud, "ช่องเก็บของเต็ม", T0, AlertIds.InventoryFull, Box));
        rules.Evaluate(In(0));
        Assert.True(rules.IsActive(box));
        Assert.Equal(AutoCollectRules.Alert, rules.RuleOf(box));
        Assert.False(rules.IsActive(rules.IndexOf("ammo")));
        text = RowReading.Unknown;
        for (int s = 1; s < 30; s++) rules.Evaluate(In(s));
        Assert.True(rules.IsActive(box));
        Assert.Equal(AutoCollectRules.Unknown, rules.RuleOf(box));
    }

    [Fact]
    public void Message_box_region_sits_bottom_right_at_any_client_size()
    {
        var rc = new CollectRegion(C.Collect.AllRegions.First(r => r.Name == "msgbox"));
        Assert.DoesNotContain(C.Collect.Regions, r => r.Name == "msgbox");   // not in the 8 MB ring
        Assert.Equal(new Rectangle(684, 576, 340, 150), rc.Locate(1024, 768, HudScale.One));
        Assert.Equal(new Rectangle(1260, 832, 340, 150), rc.Locate(1600, 1024, HudScale.One));
    }

    // ------------------------------------------------------------- /reset

    sealed class FakeInteraction(string name, ulong? channel) : ISlashInteraction
    {
        public string CommandName => name;
        public ulong? ChannelId => channel;
        public readonly List<string> Calls = [];
        public Task RespondEphemeralAsync(string text) { Calls.Add("ephemeral:" + text); return Task.CompletedTask; }
        public Task DeferAsync() { Calls.Add("defer"); return Task.CompletedTask; }
        public Task FollowupAsync(string text) { Calls.Add("followup:" + text); return Task.CompletedTask; }
        public Task FollowupWithFileAsync(byte[] png, string fileName, string text) { Calls.Add("file"); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Reset_command_follows_the_ss_channel_rule_and_answers_in_Thai()
    {
        int resets = 0;
        bool latched = true;
        var outcomes = new List<string>();
        var h = new ResetCommandHandler(() => { resets++; bool was = latched; latched = false; return was; }, () => 42UL, outcomes.Add);

        var other = new FakeInteraction("reset", 7);
        Assert.True(await h.HandleAsync(other));
        Assert.Equal(["ephemeral:" + ResetCommandHandler.WrongChannelText], other.Calls);
        Assert.Equal(0, resets);

        var ok = new FakeInteraction("reset", 42);
        Assert.True(await h.HandleAsync(ok));
        Assert.Equal(["defer", "followup:" + ResetCommandHandler.ResetText], ok.Calls);
        var again = new FakeInteraction("reset", 42);
        await h.HandleAsync(again);
        Assert.Equal("followup:" + ResetCommandHandler.NotLatchedText, again.Calls[^1]);
        Assert.Equal(["wrong_channel", "reset", "not_latched"], outcomes);

        Assert.False(await h.HandleAsync(new FakeInteraction("ss", 42)));   // not ours
    }

    [Fact]
    public void Watcher_reset_un_latches_on_the_next_poll()
    {
        var readings = new HudReadings();
        var d = new InventoryDetector(C, readings);
        var events = new List<WatchEvent>();
        d.Update(Framed(phrase: true), T0, Box, events);
        Assert.True(d.Latched);
        d.RequestReset();                     // from the UI or Discord thread
        Assert.True(d.Latched);               // applied on the polling thread
        d.Update(Framed(), T0.AddSeconds(1), Box, events);
        Assert.False(d.Latched);
    }
}
