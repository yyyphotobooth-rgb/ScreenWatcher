using ScreenWatcher.Alerts;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;
using Xunit.Abstractions;
using static ScreenWatcher.Tests.HudTestData;

namespace ScreenWatcher.Tests;

/// <summary>
/// Field trial 2026-11-19: false "ยา SP หมด" (01:33:28) and "ยาเกราะหมด" (06:26:18) with the kit on
/// the bar, and the ซ้าย/ขวา chooser shown with only one kit of the type. Crops (1024x768, scale 1.0)
/// are in samples/field-2026-11-19 (alert crops) and its items/ folder (collected item crops and
/// /miss ring crops, copied from the owner's collect and evidence folders).
/// Cause: the game draws its own mouse cursor; over a kit it changed the mean colour of the
/// "saturated" cells the old rule used, so the red HP kit was read as a shield kit (07:51:28 crop).
/// One such poll split the row into a two-kit pair that was never forgotten, so the row watched
/// the slot of another potion and reported its own kit as gone.
/// </summary>
public class FieldTrialItemTests(ITestOutputHelper output)
{
    static readonly IconSet Icons = IconSet.Load(Path.Combine(AppContext.BaseDirectory, IconSet.FileName));
    static readonly ItemsConfig Cfg = TestData.Config.Items;
    static readonly CountFont Font = CountFont.Render(Cfg.CountFontFace, Cfg.CountFontMinHeight, Cfg.CountFontMaxHeight, Cfg.CountGlyphs);
    static readonly DateTime T0 = new(2026, 11, 19, 1, 33, 0);
    static readonly string[] Potions = [AlertIds.HpPotions, AlertIds.ShieldPotions, AlertIds.SpPotions];

    static string Field(string file) => Path.Combine(TestData.SamplesDir, "field-2026-11-19", file);
    static string Items(string file) => Directory.GetFiles(Path.Combine(TestData.SamplesDir, "field-2026-11-19", "items"), file + "*").Single();

    static ItemBarReading Read(string path)
    {
        var r = new ItemBarReading();
        new ItemBarReader(Cfg, Icons, Font).Read(RegionFrame(path, "items"), HudScale.One, r);
        return r;
    }

    static string Describe(ItemBarReading r) =>
        string.Join(" ", r.Found.Select(f => $"{f.Slot}:{f.ItemId}={f.Count?.ToString() ?? "?"}")) +
        (r.UncertainKits.Count > 0 ? " uncertain:" + string.Join(",", r.UncertainKits) : "");

    [Theory]
    // file, hp, shield, sp (slots 1, 2, 3). Kits C/B/C at 01:33, A/A/C from 06:26 (grade A never in the old samples).
    [InlineData("false_sp_out_crop.png", 125, 791, 43)]
    [InlineData("false_shield_out_crop.png", 1429, 1561, 43)]
    public void Alert_crops_show_each_kit_once_with_its_count(string file, int hp, int shield, int sp)
    {
        var r = Read(Field(file));
        output.WriteLine(Describe(r));
        Assert.Equal([(AlertIds.HpPotions, 1, hp), (AlertIds.ShieldPotions, 2, shield), (AlertIds.SpPotions, 3, sp)],
            r.Found.Where(f => Potions.Contains(f.ItemId)).Select(f => (f.ItemId, f.Slot, f.Count ?? -1)));
        Assert.Empty(r.UncertainKits);
        // Every other icon (skill icons, the purple fuel-bottle icon with "B", ammo box, card) is not a kit.
        Assert.DoesNotContain(r.Found, f => Potions.Contains(f.ItemId) && f.Slot is not (1 or 2 or 3));
    }

    [Fact]
    public void Ring_and_collected_crops_around_both_alerts_read_every_kit_once()
    {
        var files = Directory.GetFiles(Path.Combine(TestData.SamplesDir, "field-2026-11-19", "items"))
            .Where(f => !Path.GetFileName(f).StartsWith("0751") && !Path.GetFileName(f).StartsWith("0916") && !Path.GetFileName(f).StartsWith("0658"));
        int n = 0;
        foreach (var f in files)
        {
            var r = Read(f);
            output.WriteLine($"{Path.GetFileName(f)}: {Describe(r)}");
            Assert.Equal(Potions, r.Found.Where(x => Potions.Contains(x.ItemId)).Select(x => x.ItemId));
            Assert.Equal([1, 2, 3], r.Found.Where(x => Potions.Contains(x.ItemId)).Select(x => x.Slot));
            Assert.All(r.Found, x => Assert.NotNull(x.Count));
            Assert.Equal(43, r.Found.Single(x => x.ItemId == AlertIds.SpPotions).Count);
            n++;
        }
        Assert.Equal(15, n);
    }

    [Fact]
    public void Cursor_over_a_kit_never_turns_it_into_another_kit()
    {
        // 07:51:28: the cursor's blue tip lies over the lower half of the red HP kit (slot 1).
        var r = Read(Items("075128_333"));
        output.WriteLine(Describe(r));
        Assert.Equal(AlertIds.HpPotions, r.Found.Single(f => f.Slot == 1).ItemId);
        Assert.Single(r.Found, f => f.ItemId == AlertIds.ShieldPotions);

        // 09:16:09: the cursor hides most of the shield kit (slot 2) and part of the HP kit.
        r = Read(Items("091609_340"));
        output.WriteLine(Describe(r));
        Assert.Contains(2, r.UncertainKits);
        Assert.DoesNotContain(r.Found, f => f.ItemId == AlertIds.ShieldPotions);
        Assert.Equal(AlertIds.HpPotions, r.Found.Single(f => f.Slot == 1).ItemId);
    }

    static (List<string> Alerts, HudReadings Readings) Replay(params (string File, int Polls)[] seq)
    {
        var readings = new HudReadings();
        var d = new ItemDetector(TestData.Config, Icons, readings, new HudOptions(), Font);
        var events = new List<WatchEvent>();
        int t = 0;
        foreach (var (file, polls) in seq)
        {
            var frame = RegionFrame(Items(file), "items");
            for (int i = 0; i < polls; i++) d.Process(frame, HudScale.One, T0.AddSeconds(t++), events);
        }
        return (events.Select(e => e.Message).ToList(), readings);
    }

    [Fact]
    public void Replay_cursor_over_the_hp_kit_keeps_one_shield_kit_and_no_gone_alert()
    {
        // The real sequence (one poll per second): before, the cursor frame, after.
        var (alerts, readings) = Replay(("075118_341", 3), ("075128_333", 1), ("075131_331", 3), ("075134_339", 4));
        Assert.Empty(alerts);
        Assert.Equal(new RowReading("1426", false), readings.Get(AlertIds.ShieldPotions));
        Assert.Equal(new RowReading("1493", false), readings.Get(AlertIds.HpPotions));
        Assert.Equal(new RowReading("43", false), readings.Get(AlertIds.SpPotions));
    }

    [Fact]
    public void Replay_cursor_hiding_the_shield_kit_for_several_polls_holds_the_row()
    {
        var (alerts, readings) = Replay(("091604_331", 3), ("091609_340", 5), ("091612_337", 3));
        Assert.Empty(alerts);
        Assert.Equal(new RowReading("1231", false), readings.Get(AlertIds.ShieldPotions));
        Assert.All(Potions, id => Assert.False(readings.Get(id).TwoFound));
    }

    [Fact]
    public void Replay_cursor_over_the_sp_kit_and_the_polls_before_the_shield_alert_stay_quiet()
    {
        var (alerts, readings) = Replay(("065835_335", 3), ("065848_336", 3), ("065851_338", 2),
            ("062609_334", 2), ("062612_327", 2), ("062614_333", 3), ("062617_341", 1), ("062618_337", 3));
        Assert.Empty(alerts);
        Assert.Equal(new RowReading("1561", false), readings.Get(AlertIds.ShieldPotions));
        Assert.Equal(new RowReading("43", false), readings.Get(AlertIds.SpPotions));
        Assert.All(Potions, id => Assert.False(readings.Get(id).TwoFound));
    }

    [Fact]
    public void A_kit_misread_as_another_type_for_a_few_polls_does_not_split_the_row_for_good()
    {
        // 01:33 (the frames are lost to the collect size cap): the SP row read 125, the HP kit's
        // count, then reported "หมด" two polls later. Same shape, as readings: the HP kit in slot 1
        // misread as an SP kit on 2 polls, then read right again.
        var readings = new HudReadings();
        var d = new ItemDetector(TestData.Config, Icons, readings, new HudOptions(), Font);
        static ItemBarReading Bar(string slot1) => new() { BarVisible = true, Found = { new(slot1, 1, 125), new(AlertIds.ShieldPotions, 2, 790), new(AlertIds.SpPotions, 3, 43) } };
        var events = new List<WatchEvent>();
        var polls = new[] { Bar(AlertIds.HpPotions), Bar(AlertIds.HpPotions), Bar(AlertIds.SpPotions), Bar(AlertIds.SpPotions) }
            .Concat(Enumerable.Repeat(Bar(AlertIds.HpPotions), 6)).ToArray();
        for (int i = 0; i < polls.Length; i++) d.Update(polls[i], T0.AddSeconds(i), new(322, 694, 380, 62), events);
        Assert.DoesNotContain(events, e => e.Message.Contains(ItemDetector.GoneText));
        Assert.Equal(new RowReading("43", false), readings.Get(AlertIds.SpPotions));
        Assert.False(readings.Get(AlertIds.SpPotions).TwoFound);
    }
}
