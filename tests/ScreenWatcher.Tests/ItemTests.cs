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
/// Stage 4, item bar. Labelled set: samples/labelled/items (item-bar crops at HUD scale 1.0 with
/// the count of each watched item; "-" = not on the bar) plus the full-frame samples, one of them
/// at HUD scale ~1.3.
/// </summary>
public class ItemTests(ITestOutputHelper output)
{
    static readonly IconSet Icons = IconSet.Load(Path.Combine(AppContext.BaseDirectory, IconSet.FileName));
    static readonly ItemsConfig Cfg = TestData.Config.Items;
    static readonly CountFont Font = CountFont.Render(Cfg.CountFontFace, Cfg.CountFontMinHeight, Cfg.CountFontMaxHeight, Cfg.CountGlyphs);
    static readonly string[] Order = [AlertIds.HpPotions, AlertIds.ShieldPotions, AlertIds.SpPotions, AlertIds.MonsterCard, AlertIds.FuelBottles, AlertIds.AmmoBox];
    static readonly Rectangle Crop = new(322, 694, 380, 62);
    static readonly DateTime T0 = new(2026, 11, 18, 20, 0, 0);

    static ItemBarReader NewReader() => new(Cfg, Icons, Font);

    static (ItemDetector Detector, HudReadings Readings, HudOptions Options) NewDetector()
    {
        var readings = new HudReadings();
        var options = new HudOptions();
        return (new ItemDetector(TestData.Config, Icons, readings, options, Font), readings, options);
    }

    static ItemBarReading Bar(bool visible, params (string Id, int Slot, int? Count)[] items)
    {
        var r = new ItemBarReading { BarVisible = visible };
        foreach (var (id, slot, count) in items.OrderBy(i => i.Slot)) r.Found.Add(new FoundItem(id, slot, count));
        return r;
    }

    /// <summary>Feeds the readings one poll each; returns the alert texts in order.</summary>
    static List<string> Feed(ItemDetector d, params ItemBarReading[] polls)
    {
        var events = new List<WatchEvent>();
        for (int i = 0; i < polls.Length; i++) d.Update(polls[i], T0.AddSeconds(i), Crop, events);
        return events.Select(e => e.Message).ToList();
    }

    static string Describe(ItemBarReading r) => string.Join(" ", r.Found.Select(f => $"{f.Slot}:{f.ItemId}={f.Count?.ToString() ?? "?"}"));

    // ------------------------------------------------------------- reading

    [Fact]
    public void Count_font_is_Tahoma_and_renders_the_digits_measured_at_scale_1()
    {
        foreach (var (digit, rows) in Cfg.CountGlyphs)
            Assert.Equal(string.Join("/", rows), CountFont.Glyph(Cfg.CountFontFace, 11, digit[0]));
        Assert.Contains(8, Font.Heights);     // 11 px Tahoma: scale 1.0
        Assert.Contains(11, Font.Heights);    // 15 px Tahoma: the ~1.3 sample
    }

    [Fact]
    public void Labelled_crops_find_every_item_by_icon_and_read_every_count()
    {
        var reader = NewReader();
        var result = new ItemBarReading();
        int crops = 0, counts = 0, icons = 0;
        int[] digitTotal = new int[10];
        foreach (var row in Labels("items"))
        {
            reader.Read(RegionFrame(Path.Combine(LabelledDir("items"), row[0]), "items"), HudScale.One, result);
            Assert.True(result.BarVisible, row[0]);
            var expected = Order.Select((id, i) => (id, label: row[i + 1])).Where(x => x.label != "-").ToList();
            Assert.Equal(expected.Count, result.Found.Count);
            foreach (var (id, label) in expected)
            {
                var found = Assert.Single(result.Found, f => f.ItemId == id);
                icons++;
                Assert.Equal(int.Parse(label), found.Count);
                counts++;
                foreach (char c in label) digitTotal[c - '0']++;
            }
            crops++;
        }
        output.WriteLine($"scale 1: {crops} crops, {icons}/{icons} icons found, {counts}/{counts} counts read, 0 wrong; digits seen " +
            string.Join(" ", Enumerable.Range(0, 10).Select(d => $"{d}:{digitTotal[d]}")));
        Assert.True(crops >= 70);
        Assert.All(digitTotal, n => Assert.True(n > 0));
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.305)]
    [InlineData(1.32)]
    public void Labelled_crops_stretched_like_a_large_window_still_find_the_right_icons(double up)
    {
        // Only the icons are checked: a stretched picture is not how the game draws counts at a
        // large window (it draws the text in a larger font; see the ~1.3 full-frame sample).
        var reader = NewReader();
        var result = new ItemBarReading();
        int n = 0;
        foreach (var row in Labels("items"))
        {
            reader.Read(RegionFrame(Path.Combine(LabelledDir("items"), row[0]), "items", up), new HudScale(up, up), result);
            var expected = Order.Where((id, i) => row[i + 1] != "-").OrderBy(id => id);
            Assert.Equal(expected, result.Found.Select(f => f.ItemId).OrderBy(id => id));
            n++;
        }
        output.WriteLine($"scale {up}: icons right in {n}/{n} crops");
    }

    public static IEnumerable<object[]> FullFrames() =>
    [
        // sample, hp, shield, sp, card, bottle  (slots 1, 2, 3, 7, 9)
        [new FullSample("from-chat/alive_2560x1440.png", 8, 31, 2560, 1417, 1.305, 1.300), 261, 452, 44, 11, 4], // real ~1.3
        [Dead1024, 261, 454, 46, 12, 4],
        [Alive1920, 261, 452, 45, 11, 4],
        [SmallA, 291, 562, 47, 13, 5],
        [SmallB, 297, 553, 47, 13, 5],
        [SmallC, 297, 553, 46, 13, 5],
        [MissileEmpty, 297, 536, 46, 13, 5],
        [GunEmpty, 242, 358, 43, 10, 4],
        [Kill298, 238, 348, 43, 10, 4],
        [Kill299, 238, 348, 43, 10, 4],
        [Live1, 297, 519, 46, 12, 5],
        [Live2, 297, 515, 46, 12, 5],
        [Live3, 297, 515, 46, 12, 5],
    ];

    [Theory]
    [MemberData(nameof(FullFrames))]
    public void Full_frame_samples_read_the_counts_seen_in_the_image(FullSample sample, int hp, int shield, int sp, int card, int bottle)
    {
        var result = new ItemBarReading();
        NewReader().Read(sample.Load(), sample.Scale, result);
        output.WriteLine($"{sample}: {Describe(result)}");
        Assert.True(result.BarVisible);
        Assert.Equal(
            [(AlertIds.HpPotions, 1, hp), (AlertIds.ShieldPotions, 2, shield), (AlertIds.SpPotions, 3, sp), (AlertIds.MonsterCard, 7, card), (AlertIds.FuelBottles, 9, bottle)],
            result.Found.Select(f => (f.ItemId, f.Slot, f.Count ?? -1)));
    }

    /// <summary>A labelled crop on a 1024x768 client, with slot pictures copied to other slots
    /// (30x30 frame included): the way to get an item into "any slot" and two potions of one type.</summary>
    static Frame WithSlotsCopied(string file, params (int From, int To)[] copies)
    {
        using var crop = new Bitmap(Path.Combine(LabelledDir("items"), file));
        using var client = new Bitmap(1024, 768);
        using (var g = Graphics.FromImage(client))
        {
            g.Clear(Color.Black);
            g.DrawImage(crop, new Rectangle(Crop.X, Crop.Y, crop.Width, crop.Height), new Rectangle(0, 0, crop.Width, crop.Height), GraphicsUnit.Pixel);
            foreach (var (from, to) in copies)
                g.DrawImage(crop, new Rectangle(512 - 161 + 31 * to, 768 - 47, 30, 30), new Rectangle(29 + 31 * from, 27, 30, 30), GraphicsUnit.Pixel);
        }
        return Frame.FromBitmap(client, new Rectangle(0, 0, 1024, 768));
    }

    [Fact]
    public void Items_are_found_by_icon_in_whatever_slot_they_sit()
    {
        // Town crop: hp 176 (slot 1), shield 1733 (2), sp 43 (3), card 51 (7), ammo box 61 (8), bottles 3 (9).
        // Move the card to slot 5, the ammo box to slot 6 and the bottles to slot 0; empty 7..9 with slot 6's picture first.
        var result = new ItemBarReading();
        NewReader().Read(WithSlotsCopied("town_items.png", (7, 5), (9, 0), (6, 7), (6, 9)), HudScale.One, result);
        output.WriteLine(Describe(result));
        Assert.Equal(
            [(AlertIds.FuelBottles, 0, 3), (AlertIds.HpPotions, 1, 176), (AlertIds.ShieldPotions, 2, 1733), (AlertIds.SpPotions, 3, 43),
             (AlertIds.MonsterCard, 5, 51), (AlertIds.AmmoBox, 8, 61)],
            result.Found.Select(f => (f.ItemId, f.Slot, f.Count ?? -1)));
    }

    [Fact]
    public void Frames_without_the_item_bar_report_no_bar_and_no_items()
    {
        var reader = NewReader();
        var result = new ItemBarReading();
        // Full desktop without the game; a gauge crop where the bar would be.
        reader.Read(TestData.ClientFrame("{1BB4016B-B041-4A52-9FE4-810346C3AC59}.png", 0, 0, 1024, 768), HudScale.One, result);
        Assert.False(result.BarVisible);
        Assert.Empty(result.Found);
        reader.Read(TestData.CropOnBlackClient("from-chat/crop_fuel_179of300.png", 400, 600), HudScale.One, result);
        Assert.False(result.BarVisible);
        Assert.Empty(result.Found);
    }

    [Fact]
    public void Bar_with_none_of_the_watched_items_is_still_seen_as_visible()
    {
        // Every watched slot replaced by the empty slot 6: only the skill icons and frames remain.
        var result = new ItemBarReading();
        NewReader().Read(WithSlotsCopied("town_items.png", (6, 1), (6, 2), (6, 3), (6, 7), (6, 8), (6, 9)), HudScale.One, result);
        Assert.Empty(result.Found);
        Assert.True(result.BarVisible);
    }

    // ------------------------------------------------------------- alert logic

    [Fact]
    public void Low_count_fires_once_at_the_threshold_and_rearms_above_it()
    {
        var (d, readings, _) = NewDetector();       // card default threshold 3
        ItemBarReading Card(int? n) => Bar(true, (AlertIds.MonsterCard, 7, n));

        Assert.Empty(Feed(d, Card(5), Card(5), Card(4), Card(4)));
        Assert.Equal(new RowReading("4", false), readings.Get(AlertIds.MonsterCard));
        Assert.Empty(Feed(d, Card(3)));                                  // first poll at 3: not stable yet
        Assert.Equal(["บัตรกันมอนเหลือ 3 ใบ"], Feed(d, Card(3)));
        Assert.Equal(new RowReading("3", true), readings.Get(AlertIds.MonsterCard));
        Assert.Empty(Feed(d, Card(3), Card(2), Card(2), Card(1), Card(1)));   // lower: no repeat
        Assert.Empty(Feed(d, Card(10), Card(10)));                       // restocked: re-armed
        Assert.False(readings.Get(AlertIds.MonsterCard).Reached);
        Assert.Equal(["บัตรกันมอนเหลือ 2 ใบ"], Feed(d, Card(2), Card(2)));
    }

    [Fact]
    public void Each_item_uses_its_own_name_unit_and_default_threshold()
    {
        var (d, _, _) = NewDetector();
        var bar = Bar(true, (AlertIds.AmmoBox, 8, 5), (AlertIds.FuelBottles, 9, 2), (AlertIds.MonsterCard, 7, 3),
            (AlertIds.HpPotions, 1, 5), (AlertIds.ShieldPotions, 2, 5), (AlertIds.SpPotions, 3, 5));
        Assert.Equal(
            ["กล่องเติมกระสุนเหลือ 5 กล่อง", "ขวดน้ำมันเหลือ 2 ขวด", "บัตรกันมอนเหลือ 3 ใบ", "ยาเลือดเหลือ 5 ขวด", "ยาเกราะเหลือ 5 ขวด", "ยา SP เหลือ 5 ขวด"],
            Feed(d, bar, bar));

        var (above, _, _) = NewDetector();
        var ok = Bar(true, (AlertIds.AmmoBox, 8, 6), (AlertIds.FuelBottles, 9, 3), (AlertIds.MonsterCard, 7, 4),
            (AlertIds.HpPotions, 1, 6), (AlertIds.ShieldPotions, 2, 6), (AlertIds.SpPotions, 3, 6));
        Assert.Empty(Feed(above, ok, ok, ok));
    }

    [Fact]
    public void Threshold_edited_in_the_row_is_used()
    {
        var (d, readings, options) = NewDetector();
        options.SetThreshold(AlertIds.HpPotions, 200);
        var bar = Bar(true, (AlertIds.HpPotions, 1, 176));
        Assert.Equal(["ยาเลือดเหลือ 176 ขวด"], Feed(d, bar, bar));
        Assert.True(readings.Get(AlertIds.HpPotions).Reached);
    }

    [Fact]
    public void Unreadable_count_shows_a_question_mark_and_does_not_alert()
    {
        var (d, readings, _) = NewDetector();
        ItemBarReading Card(int? n) => Bar(true, (AlertIds.MonsterCard, 7, n));
        Assert.Empty(Feed(d, Card(9), Card(9), Card(null), Card(1), Card(null), Card(2), Card(null), Card(null), Card(null)));
        Assert.Equal(RowReading.Unknown, readings.Get(AlertIds.MonsterCard).Text);
    }

    [Fact]
    public void Gone_fires_when_a_seen_icon_disappears_while_the_bar_is_visible()
    {
        var (d, readings, _) = NewDetector();
        var with = Bar(true, (AlertIds.FuelBottles, 9, 1), (AlertIds.HpPotions, 1, 100));
        var without = Bar(true, (AlertIds.HpPotions, 1, 100));
        Assert.Equal(3, Cfg.GoneStablePolls);

        Assert.Equal(["ขวดน้ำมันเหลือ 1 ขวด"], Feed(d, with, with));
        Assert.Empty(Feed(d, without, without));                         // two polls: not yet
        Assert.Equal(["ขวดน้ำมันหมด"], Feed(d, without));
        Assert.Equal(new RowReading(ItemDetector.GoneText, true), readings.Get(AlertIds.FuelBottles));
        Assert.Empty(Feed(d, without, without, without, without));       // once

        // Bought more: back on the bar, re-armed for both alerts.
        var restocked = Bar(true, (AlertIds.FuelBottles, 9, 20), (AlertIds.HpPotions, 1, 100));
        Assert.Empty(Feed(d, restocked, restocked));
        Assert.Equal(new RowReading("20", false), readings.Get(AlertIds.FuelBottles));
        Assert.Equal(["ขวดน้ำมันหมด"], Feed(d, without, without, without));
    }

    [Fact]
    public void Gone_never_fires_for_an_item_that_was_never_seen_or_while_the_bar_is_hidden()
    {
        var (d, readings, _) = NewDetector();
        var others = Bar(true, (AlertIds.HpPotions, 1, 100));
        Assert.Empty(Feed(d, others, others, others, others, others));
        Assert.Equal(RowReading.None, readings.Get(AlertIds.AmmoBox).Text);

        // Seen, then the whole bar goes away (no frames, no icons): nothing fires however long.
        var (d2, readings2, _) = NewDetector();
        var with = Bar(true, (AlertIds.AmmoBox, 8, 61));
        var hidden = Bar(false);
        Assert.Empty(Feed(d2, with, with, hidden, hidden, hidden, hidden, hidden, hidden));
        Assert.Equal(RowReading.None, readings2.Get(AlertIds.AmmoBox).Text);
        // Bar back without the box for one or two polls, then the box is there again: still nothing.
        Assert.Empty(Feed(d2, Bar(true), Bar(true), with, with, Bar(true), hidden, Bar(true), Bar(true), with));
    }

    [Fact]
    public void Two_potions_of_one_type_show_the_chooser_and_only_the_chosen_side_is_watched()
    {
        var (d, readings, options) = NewDetector();
        // Red potion in slots 1 (left, 40) and 4 (right, 3). Default: left.
        ItemBarReading Two(int? left, int? right) => Bar(true, (AlertIds.HpPotions, 1, left), (AlertIds.HpPotions, 4, right), (AlertIds.SpPotions, 3, 50));
        Assert.Empty(Feed(d, Two(40, 3), Two(40, 3), Two(40, 3)));     // right is at 3 <= 5, but left is watched
        Assert.Equal(new RowReading("40", false, TwoFound: true), readings.Get(AlertIds.HpPotions));
        Assert.False(readings.Get(AlertIds.SpPotions).TwoFound);

        options.SetWatchRight(AlertIds.HpPotions, true);                // user picks ขวา
        Assert.Equal(["ยาเลือดเหลือ 3 ขวด"], Feed(d, Two(40, 3), Two(40, 3)));
        Assert.Equal(new RowReading("3", true, TwoFound: true), readings.Get(AlertIds.HpPotions));
    }

    [Fact]
    public void When_the_chosen_potion_runs_out_the_row_does_not_switch_to_the_other_one()
    {
        var (d, readings, options) = NewDetector();
        ItemBarReading Two(int left, int right) => Bar(true, (AlertIds.ShieldPotions, 2, left), (AlertIds.ShieldPotions, 6, right));
        var onlyRight = Bar(true, (AlertIds.ShieldPotions, 6, 300));

        Assert.Empty(Feed(d, Two(8, 300), Two(8, 300), Two(7, 300), Two(6, 300), Two(6, 300)));
        Assert.Equal(["ยาเกราะเหลือ 5 ขวด"], Feed(d, Two(5, 300), Two(5, 300)));
        // Left used up: its icon is gone, the right one (300) is still there.
        Assert.Equal(["ยาเกราะหมด"], Feed(d, onlyRight, onlyRight, onlyRight));
        Assert.Equal(new RowReading(ItemDetector.GoneText, true, TwoFound: true), readings.Get(AlertIds.ShieldPotions));
        Assert.Empty(Feed(d, onlyRight, onlyRight, onlyRight, onlyRight));   // stays on "left": no switch, no repeat

        // Only when the user picks ขวา does the row follow the other icon.
        options.SetWatchRight(AlertIds.ShieldPotions, true);
        Assert.Empty(Feed(d, onlyRight, onlyRight));
        Assert.Equal(new RowReading("300", false, TwoFound: true), readings.Get(AlertIds.ShieldPotions));
    }

    [Fact]
    public void Real_frames_with_two_red_kits_read_left_and_right_and_alert_with_the_bar_crop()
    {
        // Town crop with the red kit (176) also copied over the empty slot 5.
        var frame = WithSlotsCopied("town_items.png", (1, 5));
        var (d, readings, options) = NewDetector();
        options.SetThreshold(AlertIds.HpPotions, 180);
        var events = new List<WatchEvent>();
        d.Process(frame, HudScale.One, T0, events);
        d.Process(frame, HudScale.One, T0.AddSeconds(1), events);
        Assert.True(readings.Get(AlertIds.HpPotions).TwoFound);
        var e = Assert.Single(events, x => x.AlertId == AlertIds.HpPotions);
        Assert.Equal("ยาเลือดเหลือ 176 ขวด", e.Message);
        Assert.Equal(Crop, e.Crop);
        using var img = new Bitmap(new MemoryStream(new AlertComposer(TestData.Config).Compose(e, frame, HudScale.One).Png!));
        Assert.Equal(Crop.Size, img.Size);
        Assert.Equal("61", readings.Get(AlertIds.AmmoBox).Text);
        Assert.Equal("1733", readings.Get(AlertIds.ShieldPotions).Text);
    }
}
