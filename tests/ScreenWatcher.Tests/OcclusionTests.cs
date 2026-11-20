using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;
using Xunit.Abstractions;
using static ScreenWatcher.Tests.HudTestData;

namespace ScreenWatcher.Tests;

/// <summary>
/// Occlusion = "unreadable", never "empty/low" (field trial 2026-11-20 12:52:18: the hover tooltip
/// "Mini Stealth Card (75 ชิ้น)" covered the potion slots and gave three false "หมด"). The 12:52
/// alert crop and full frame are in samples/field-2026-11-20 (copied from the owner's evidence
/// folder). Synthetic covers: a cut of that real tooltip (black body, light-grey text) and an
/// opaque dark box, pasted over each HUD part (kill counter: only the tooltip text counts as covered).
/// </summary>
public class OcclusionTests(ITestOutputHelper output)
{
    static readonly WatcherConfig C = TestData.Config;
    static readonly IconSet Icons = IconSet.Load(Path.Combine(AppContext.BaseDirectory, IconSet.FileName));
    static readonly CountFont Font = CountFont.Render(C.Items.CountFontFace, C.Items.CountFontMinHeight, C.Items.CountFontMaxHeight, C.Items.CountGlyphs);
    static readonly DateTime T0 = new(2026, 11, 20, 12, 51, 0);
    static readonly Rectangle ItemCrop = new(322, 694, 380, 62);
    static readonly string[] Potions = [AlertIds.HpPotions, AlertIds.ShieldPotions, AlertIds.SpPotions];

    static string Field(string file) => Path.Combine(TestData.SamplesDir, "field-2026-11-20", file);
    static string TooltipCrop => Field("tooltip_potions_1252_crop.png");

    /// <summary>The tooltip body cut from the 12:52 crop (black with light-grey text).</summary>
    static readonly Rectangle TooltipCut = new(58, 24, 150, 16);

    public enum Cover { Tooltip, DarkBox }

    /// <summary>Paints the cover over native rectangle <paramref name="r"/> of a client bitmap.</summary>
    static void Paint(Bitmap client, Rectangle r, Cover cover)
    {
        using var g = Graphics.FromImage(client);
        if (cover == Cover.DarkBox)
        {
            using var b = new SolidBrush(Color.FromArgb(8, 10, 16));
            g.FillRectangle(b, r);
            return;
        }
        using var tip = new Bitmap(TooltipCrop);
        // Tile the real tooltip cut over the rectangle (it is wider than tall).
        for (int y = r.Top; y < r.Bottom; y += TooltipCut.Height)
            for (int x = r.Left; x < r.Right; x += TooltipCut.Width)
            {
                int w = Math.Min(TooltipCut.Width, r.Right - x), h = Math.Min(TooltipCut.Height, r.Bottom - y);
                g.DrawImage(tip, new Rectangle(x, y, w, h), new Rectangle(TooltipCut.X, TooltipCut.Y, w, h), GraphicsUnit.Pixel);
            }
    }

    static Bitmap SampleClient(FullSample s)
    {
        using var shot = new Bitmap(Path.Combine(TestData.SamplesDir, s.File));
        var client = new Bitmap(s.W, s.H);
        using var g = Graphics.FromImage(client);
        g.Clear(Color.Black);
        g.DrawImage(shot, new Rectangle(0, 0, s.W, s.H), new Rectangle(s.X, s.Y, s.W, s.H), GraphicsUnit.Pixel);
        return client;
    }

    static Bitmap CropClient(string cropPath, string region)
    {
        var rc = new Collection.CollectRegion(C.Collect.AllRegions.First(r => r.Name == region));
        var rect = rc.Locate(1024, 768, HudScale.One);
        using var crop = new Bitmap(cropPath);
        var client = new Bitmap(1024, 768);
        using var g = Graphics.FromImage(client);
        g.Clear(Color.Black);
        g.DrawImage(crop, new Rectangle(rect.X, rect.Y, crop.Width, crop.Height), new Rectangle(0, 0, crop.Width, crop.Height), GraphicsUnit.Pixel);
        return client;
    }

    static Frame ToFrame(Bitmap b) => Frame.FromBitmap(b, new Rectangle(0, 0, b.Width, b.Height));

    static string Describe(ItemBarReading r) =>
        string.Join(" ", r.Found.Select(f => $"{f.Slot}:{f.ItemId}={f.Count?.ToString() ?? "?"}")) +
        (r.UncertainKits.Count > 0 ? " uncertain:" + string.Join(",", r.UncertainKits) : "") +
        " empty:" + string.Join(",", r.EmptySlots);

    static ItemBarReading ReadBar(Frame f)
    {
        var r = new ItemBarReading();
        new ItemBarReader(C.Items, Icons, Font).Read(f, HudScale.One, r);
        return r;
    }

    /// <summary>The potion kits as they were just before 12:52 (alert journal "rearm": 2325 / 3001 / 40),
    /// in slots 1..3 like every other frame of that set-up.</summary>
    static ItemBarReading Before()
    {
        var r = new ItemBarReading { BarVisible = true };
        r.Found.Add(new(AlertIds.HpPotions, 1, 2325));
        r.Found.Add(new(AlertIds.ShieldPotions, 2, 3001));
        r.Found.Add(new(AlertIds.SpPotions, 3, 40));
        return r;
    }

    // ------------------------------------------------------------- items: the 12:52 frame

    [Fact]
    public void Tooltip_frame_1252_shows_no_potion_and_no_empty_potion_slot()
    {
        using var client = CropClient(TooltipCrop, "items");
        var r = ReadBar(ToFrame(client));
        output.WriteLine(Describe(r));
        Assert.True(r.BarVisible);
        Assert.DoesNotContain(r.Found, f => Potions.Contains(f.ItemId));
        Assert.DoesNotContain(1, r.EmptySlots);
        Assert.DoesNotContain(2, r.EmptySlots);
        Assert.DoesNotContain(3, r.EmptySlots);
        Assert.Contains(6, r.EmptySlots);   // the real empty slot next to it still reads as empty
    }

    [Fact]
    public void Tooltip_frame_1252_makes_the_potion_rows_unreadable_never_gone()
    {
        var readings = new HudReadings();
        var d = new ItemDetector(C, Icons, readings, new HudOptions(), Font);
        var events = new List<WatchEvent>();
        d.Update(Before(), T0, ItemCrop, events);
        d.Update(Before(), T0.AddSeconds(1), ItemCrop, events);
        using var client = CropClient(TooltipCrop, "items");
        var frame = ToFrame(client);
        for (int i = 2; i < 30; i++) d.Process(frame, HudScale.One, T0.AddSeconds(i), events);
        Assert.DoesNotContain(events, e => e.Message.Contains(ItemDetector.GoneText));
        Assert.Empty(events);
        foreach (var id in Potions) Assert.Equal(new RowReading(RowReading.Unknown, false), readings.Get(id));

        // Tooltip gone, kits back with the same counts: no alert either way.
        for (int i = 30; i < 33; i++) d.Update(Before(), T0.AddSeconds(i), ItemCrop, events);
        Assert.Empty(events);
        Assert.Equal("2325", readings.Get(AlertIds.HpPotions).Text);
    }

    [Fact]
    public void Tooltip_frame_1252_through_the_unreadable_rule_gives_one_combined_message_after_10_s()
    {
        var readings = new HudReadings();
        var options = new HudOptions();
        var items = new ItemDetector(C, Icons, readings, options, Font);
        var unreadable = new UnreadableDetector(C, readings, options, () => items.BarHiddenPolls);
        var events = new List<WatchEvent>();
        for (int i = 0; i < 3; i++)
        {
            items.Update(Before(), T0.AddSeconds(i), ItemCrop, events);
            unreadable.Process(T0.AddSeconds(i), 1024, 768, events);
        }
        using var client = CropClient(TooltipCrop, "items");
        var frame = ToFrame(client);
        var at = new List<int>();
        for (int i = 3; i < 40; i++)
        {
            int before = events.Count;
            items.Process(frame, HudScale.One, T0.AddSeconds(i), events);
            unreadable.Process(T0.AddSeconds(i), 1024, 768, events);
            if (events.Count > before) at.Add(i);
        }
        var e = Assert.Single(events);
        Assert.Equal(AlertIds.Unreadable, e.AlertId);
        Assert.Equal("อ่านค่าไม่ได้ : ยาเลือด, ยาเกราะ, ยา SP", e.Message);
        output.WriteLine($"sent at poll {at.Single()} ({e.Message})");
        // "?" from the 3rd covered poll (s 5), over the limit (> 10 s) at s 16, combined 3 s later.
        Assert.InRange(at.Single(), 15, 21);
    }

    [Fact]
    public void Disabled_rows_are_left_out_of_the_combined_message()
    {
        var readings = new HudReadings();
        var options = new HudOptions();
        options.SetEnabled(AlertIds.ShieldPotions, false);
        var items = new ItemDetector(C, Icons, readings, options, Font);
        var unreadable = new UnreadableDetector(C, readings, options, () => items.BarHiddenPolls);
        var events = new List<WatchEvent>();
        for (int i = 0; i < 3; i++) { items.Update(Before(), T0.AddSeconds(i), ItemCrop, events); unreadable.Process(T0.AddSeconds(i), 1024, 768, events); }
        using var client = CropClient(TooltipCrop, "items");
        var frame = ToFrame(client);
        for (int i = 3; i < 40; i++) { items.Process(frame, HudScale.One, T0.AddSeconds(i), events); unreadable.Process(T0.AddSeconds(i), 1024, 768, events); }
        Assert.Equal("อ่านค่าไม่ได้ : ยาเลือด, ยา SP", Assert.Single(events).Message);
    }

    // ------------------------------------------------------------- items: true empty vs covered (town frame)

    /// <summary>town_items.png on a 1024x768 client, with slot pictures copied (from, to) and
    /// optional covers over native rectangles.</summary>
    static Frame Town((int From, int To)[] copies, Cover? cover = null, Rectangle? over = null)
    {
        using var crop = new Bitmap(Path.Combine(LabelledDir("items"), "town_items.png"));
        using var client = new Bitmap(1024, 768);
        using (var g = Graphics.FromImage(client))
        {
            g.Clear(Color.Black);
            g.DrawImage(crop, new Rectangle(ItemCrop.X, ItemCrop.Y, crop.Width, crop.Height), new Rectangle(0, 0, crop.Width, crop.Height), GraphicsUnit.Pixel);
            foreach (var (from, to) in copies)
                g.DrawImage(crop, new Rectangle(512 - 161 + 31 * to, 768 - 47, 30, 30), new Rectangle(29 + 31 * from, 27, 30, 30), GraphicsUnit.Pixel);
        }
        if (cover is { } c) Paint(client, over!.Value, c);
        return ToFrame(client);
    }

    /// <summary>Slots 1..3 (the kits) as native rectangles, frame included.</summary>
    static readonly Rectangle KitSlots = new(512 - 161 + 31, 768 - 47, 31 * 3 - 1, 30);

    [Fact]
    public void True_empty_slots_still_fire_gone_once()
    {
        var plain = Town([]);
        var emptied = Town([(6, 1), (6, 2), (6, 3)]);    // the real empty slot 6 copied over the kits
        var r = ReadBar(emptied);
        output.WriteLine(Describe(r));
        Assert.Contains(1, r.EmptySlots);
        Assert.Contains(2, r.EmptySlots);
        Assert.Contains(3, r.EmptySlots);
        Assert.Contains(5, ReadBar(plain).EmptySlots);

        var readings = new HudReadings();
        var d = new ItemDetector(C, Icons, readings, new HudOptions(), Font);
        var events = new List<WatchEvent>();
        for (int i = 0; i < 3; i++) d.Process(plain, HudScale.One, T0.AddSeconds(i), events);
        Assert.Empty(events);
        for (int i = 3; i < 10; i++) d.Process(emptied, HudScale.One, T0.AddSeconds(i), events);
        Assert.Equal(["ยาเลือดหมด! ตัวถังเสี่ยงพัง", "ยาเกราะหมด! โล่ป้องกันไม่มีสำรอง", "ยา SP หมด! ใช้สกิลไม่ได้"], events.Select(e => e.Message));
    }

    [Theory]
    [InlineData(Cover.Tooltip)]
    [InlineData(Cover.DarkBox)]
    public void Cover_over_the_kits_is_unreadable_not_gone(Cover cover)
    {
        var plain = Town([]);
        var covered = Town([], cover, Rectangle.Inflate(KitSlots, 2, 2));
        var r = ReadBar(covered);
        output.WriteLine(Describe(r));
        Assert.DoesNotContain(r.Found, f => Potions.Contains(f.ItemId));
        Assert.Empty(r.EmptySlots.Intersect([1, 2, 3]));

        var readings = new HudReadings();
        var d = new ItemDetector(C, Icons, readings, new HudOptions(), Font);
        var events = new List<WatchEvent>();
        for (int i = 0; i < 3; i++) d.Process(plain, HudScale.One, T0.AddSeconds(i), events);
        for (int i = 3; i < 20; i++) d.Process(covered, HudScale.One, T0.AddSeconds(i), events);
        Assert.Empty(events);
        foreach (var id in Potions) Assert.Equal(RowReading.Unknown, readings.Get(id).Text);
        // 176 -> covered -> 176: nothing.
        for (int i = 20; i < 23; i++) d.Process(plain, HudScale.One, T0.AddSeconds(i), events);
        Assert.Empty(events);
        Assert.Equal("176", readings.Get(AlertIds.HpPotions).Text);
    }

    [Fact]
    public void Dark_box_inside_the_frame_of_a_slot_is_not_an_empty_slot()
    {
        // Frame left intact, interior painted near-black: the empty slot's interior is dark navy.
        var inside = Town([], Cover.DarkBox, new Rectangle(512 - 161 + 31 + 1, 768 - 47 + 1, 28, 28));
        var r = ReadBar(inside);
        output.WriteLine(Describe(r));
        Assert.DoesNotContain(1, r.EmptySlots);
    }

    [Fact]
    public void Covered_then_truly_empty_fires_once_on_the_positive_read()
    {
        var plain = Town([]);
        var covered = Town([], Cover.Tooltip, Rectangle.Inflate(KitSlots, 2, 2));
        var emptied = Town([(6, 1), (6, 2), (6, 3)]);
        var d = new ItemDetector(C, Icons, new HudReadings(), new HudOptions(), Font);
        var events = new List<WatchEvent>();
        int t = 0;
        foreach (var (f, n) in new[] { (plain, 3), (covered, 6), (emptied, 2), (covered, 2), (emptied, 6), (covered, 3), (emptied, 3) })
            for (int i = 0; i < n; i++) d.Process(f, HudScale.One, T0.AddSeconds(t++), events);
        // The 2 empty polls before the second cover do not count towards the 3 needed.
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Contains(ItemDetector.GoneText, e.Message));
    }

    // ------------------------------------------------------------- ammo

    /// <summary>Ammo strip of a full sample with both counters covered (1.0 layout x 50..255, y 8..30).</summary>
    static Frame AmmoCovered(FullSample s, Cover cover)
    {
        using var client = SampleClient(s);
        Paint(client, new Rectangle(50, 8, 205, 22), cover);
        return ToFrame(client);
    }

    [Theory]
    [InlineData(Cover.Tooltip)]
    [InlineData(Cover.DarkBox)]
    public void Cover_over_the_ammo_counters_is_unreadable(Cover cover)
    {
        foreach (var s in new[] { GunEmpty, MissileEmpty, Kill299 })
        {
            var (gun, missile) = new AmmoDetector(C, Glyphs, new HudReadings()).ReadRaw(AmmoCovered(s, cover), s.Scale);
            output.WriteLine($"{s} {cover}: gun {gun}, missile {missile}");
            Assert.Null(AmmoDetector.Positive(gun));
            Assert.Null(AmmoDetector.Positive(missile));
        }
    }

    [Fact]
    public void True_empty_ammo_samples_still_read_a_positive_zero()
    {
        var (gun, _) = new AmmoDetector(C, Glyphs, new HudReadings()).ReadRaw(GunEmpty.Load(), GunEmpty.Scale);
        var (_, missile) = new AmmoDetector(C, Glyphs, new HudReadings()).ReadRaw(MissileEmpty.Load(), MissileEmpty.Scale);
        Assert.Equal(0, AmmoDetector.Positive(gun));
        Assert.Equal(0, AmmoDetector.Positive(missile));
        // A 00000 read from blue digits would not be the game's empty counter.
        Assert.Null(AmmoDetector.Positive(new DigitReading(true, 0, 5, 0.9, 0.2, false)));
    }

    [Fact]
    public void Ammo_30_unreadable_30_is_silent_and_30_unreadable_0_fires_once()
    {
        var d = new AmmoDetector(C, Glyphs, new HudReadings());
        var fired = new List<int>();
        int?[] polls = [30, 30, null, null, null, null, 30, 30, null, null, null, 0, 0, 0, null, 0, 0];
        for (int i = 0; i < polls.Length; i++) if (d.Update(1, polls[i])) fired.Add(i);
        Assert.Equal([12], fired);
    }

    // ------------------------------------------------------------- fuel

    [Theory]
    [InlineData(Cover.Tooltip)]
    [InlineData(Cover.DarkBox)]
    public void Cover_over_the_fuel_arc_is_unreadable(Cover cover)
    {
        var reader = new FuelArcReader(C.Fuel);
        foreach (var s in new[] { Live1, SmallA, Kill299 })
        {
            Assert.True(reader.Read(s.Load(), s.Scale).Ok, $"{s} uncovered");
            using var client = SampleClient(s);
            // The arc: top of the ring to the pump (1.0 layout ~x 80..160, y 28..110).
            Paint(client, new Rectangle(78, 26, 84, 86), cover);
            var r = reader.Read(ToFrame(client), s.Scale);
            output.WriteLine($"{s} {cover}: {r}");
            Assert.False(r.Ok);
        }
    }

    [Fact]
    public void Fuel_45_unreadable_45_is_silent_and_45_unreadable_low_fires_once()
    {
        var d = new FuelDetector(C, new HudReadings(), new HudOptions());   // default 10 %
        var fired = new List<int>();
        int?[] polls = [8, 8, 8, null, null, null, null, 8, 8, 8, null, null, null, 0, 0, 0, null, 0, 0, 0];
        for (int i = 0; i < polls.Length; i++) if (d.Update(polls[i]) is not null) fired.Add(i);
        Assert.Equal([15], fired);
    }

    // ------------------------------------------------------------- kill counter
    // The panel shows only while an EXP card is in use and stays away after 300 (owner, 2026-11-20).
    // A blank or dark field is "no panel"; only tooltip text over it is "covered".

    /// <summary>Kill digits field (topRight anchor, cells W-129..W-73, y 333..350) with margin.</summary>
    static Rectangle KillField(int w) => new(w - 140, 326, 80, 32);

    static Frame KillWith(FullSample s, Cover cover)
    {
        using var client = SampleClient(s);
        Paint(client, KillField(s.W), cover);
        return ToFrame(client);
    }

    [Fact]
    public void Tooltip_over_the_kill_counter_is_covered_and_a_dark_box_is_no_panel()
    {
        foreach (var s in new[] { Kill298, Kill299, Live1 })
        {
            var d = new KillDetector(C, Glyphs, new HudReadings());
            var tip = d.Observe(KillWith(s, Cover.Tooltip), s.Scale);
            var box = d.Observe(KillWith(s, Cover.DarkBox), s.Scale);
            output.WriteLine($"{s}: tooltip {tip}, dark box {box}");
            Assert.Equal(KillObservation.CoveredByText, tip);
            Assert.Null(box.Value);
            Assert.False(box.Covered);
        }
    }

    /// <summary>Kill + unreadable rows together, one poll per second; returns all events with the second.</summary>
    sealed class KillRig
    {
        public readonly HudReadings Readings = new();
        public readonly KillDetector Kill;
        public readonly UnreadableDetector Unreadable;
        public readonly List<(int At, WatchEvent E)> Events = [];
        int _t;
        public KillRig()
        {
            Kill = new KillDetector(C, Glyphs, Readings);
            Unreadable = new UnreadableDetector(C, Readings, new HudOptions(), () => 0);
            foreach (var r in C.Unreadable.Rows) Readings.Set(r[0], RowReading.None);
        }
        public void Poll(Frame f, int times = 1)
        {
            for (int i = 0; i < times; i++, _t++)
            {
                var ev = new List<WatchEvent>();
                Kill.Process(f, HudScale.One, T0.AddSeconds(_t), ev);
                Unreadable.Process(T0.AddSeconds(_t), f.Width, f.Height, ev);
                foreach (var e in ev) Events.Add((_t, e));
            }
        }
    }

    [Fact]
    public void Tooltip_at_297_for_5_s_then_panel_back_gives_no_alert()
    {
        var rig = new KillRig();
        var plain = Kill298.Load();
        rig.Poll(plain, 3);
        rig.Poll(KillWith(Kill298, Cover.Tooltip), 5);
        Assert.Equal(RowReading.Unknown, rig.Readings.Get(AlertIds.Kill300).Text);
        rig.Poll(plain, 5);
        Assert.Empty(rig.Events);
        Assert.Equal("298", rig.Readings.Get(AlertIds.Kill300).Text);
    }

    [Fact]
    public void Tooltip_over_the_counter_for_more_than_10_s_is_reported_unreadable_and_never_done()
    {
        var rig = new KillRig();
        rig.Poll(Kill298.Load(), 3);
        rig.Poll(KillWith(Kill298, Cover.Tooltip), 20);
        var (at, e) = Assert.Single(rig.Events);
        Assert.Equal(AlertIds.Unreadable, e.AlertId);
        Assert.Equal("อ่านค่าไม่ได้ : นับมอน", e.Message);
        output.WriteLine($"sent at {at}");
        // Tooltip away, panel back: readable again, still armed for the real end.
        rig.Poll(Kill298.Load(), 2);
        Assert.Equal("298", rig.Readings.Get(AlertIds.Kill300).Text);
        Assert.False(rig.Unreadable.IsOverLimit(AlertIds.Kill300));
        rig.Poll(KillWith(Kill298, Cover.DarkBox), 11);
        Assert.Equal("ภารกิจสำเร็จ! ยิงครบ 300 ตัว (เลขล่าสุดที่เห็น 298)", rig.Events[^1].E.Message);
    }

    [Fact]
    public void Dark_box_over_the_counter_at_298_counts_as_no_panel_and_fires_done_after_10_s()
    {
        var rig = new KillRig();
        rig.Poll(Kill298.Load(), 2);
        rig.Poll(KillWith(Kill298, Cover.DarkBox), 10);   // absent from s 2: s 2..11
        Assert.Empty(rig.Events);
        rig.Poll(KillWith(Kill298, Cover.DarkBox), 1);
        Assert.Equal(AlertIds.Kill300, Assert.Single(rig.Events).E.AlertId);
    }

    /// <summary>Field event 2026-11-20 13:27: every kill crop the collect mode kept from 13:26:37
    /// to 13:39:59 (samples/field-2026-11-20/kill), replayed one poll per second (a crop stays on
    /// screen until the next one). The old rule sent the right alert at 13:27:22 and then a false
    /// "อ่านค่าไม่ได้ : นับมอน" at 13:33:12 because the panel never came back.</summary>
    [Fact]
    public void Replay_1327_one_done_alert_and_no_unreadable()
    {
        var files = Directory.GetFiles(Field("kill"), "*.png").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
        static int Sec(string f) { var n = Path.GetFileName(f); return int.Parse(n[..2]) * 3600 + int.Parse(n[2..4]) * 60 + int.Parse(n[4..6]); }
        var rig = new KillRig();
        var frames = files.Select(f => (Sec(f), RegionFrame(f, "kill"))).ToArray();
        int start = frames[0].Item1, end = frames[^1].Item1 + 30, k = 0;
        var seen = new List<string>();
        for (int s = start; s <= end; s++)
        {
            while (k + 1 < frames.Length && frames[k + 1].Item1 <= s) k++;
            rig.Poll(frames[k].Item2);
            string text = rig.Readings.Get(AlertIds.Kill300).Text;
            if (seen.Count == 0 || seen[^1] != text) seen.Add(text);
        }
        output.WriteLine("row: " + string.Join(" > ", seen));
        foreach (var (at, e) in rig.Events) output.WriteLine($"{TimeSpan.FromSeconds(start + at - rig.Events[0].At + (rig.Events[0].At)):c}: {e.Message}");
        var (when, done) = Assert.Single(rig.Events);
        Assert.Equal(AlertIds.Kill300, done.AlertId);
        Assert.Equal("ภารกิจสำเร็จ! ยิงครบ 300 ตัว (เลขล่าสุดที่เห็น 298)", done.Message);
        output.WriteLine($"done alert at {TimeSpan.FromSeconds(start + when):c}");
        Assert.Equal(KillDetector.DoneText, rig.Readings.Get(AlertIds.Kill300).Text);
        Assert.DoesNotContain(rig.Events, x => x.E.AlertId == AlertIds.Unreadable);
    }

    [Fact]
    public void Panel_back_after_done_rearms()
    {
        var rig = new KillRig();
        rig.Poll(Kill299.Load(), 2);
        var gone = RegionFrame(Path.Combine(LabelledDir("kill"), "174627_690_kill_change.png"), "kill");
        rig.Poll(gone, 15);
        Assert.Single(rig.Events);
        Assert.Equal(KillDetector.DoneText, rig.Readings.Get(AlertIds.Kill300).Text);
        var armed = new Dictionary<string, bool>();
        rig.Kill.CopyArmed(armed);
        Assert.False(armed[AlertIds.Kill300]);
        // New card: the panel is back counting from low numbers.
        foreach (int v in new[] { 0, 0, 1 }) rig.Kill.Update(KillObservation.Of(v), T0.AddMinutes(10).AddSeconds(v));
        rig.Kill.CopyArmed(armed);
        Assert.True(armed[AlertIds.Kill300]);
        Assert.Equal("1", rig.Readings.Get(AlertIds.Kill300).Text);
    }
}
