using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;
using Xunit.Abstractions;

namespace ScreenWatcher.Tests;

/// <summary>
/// Level-up quest panel (field trial 2026-11-19). Positives: the only appearance seen, five JPEG
/// frames 06:20..09:38 (the 09:38 /miss frame is levelup_quest_popup_full.jpg). Negatives: every
/// other full frame of the trial and every full-size image under samples/.
/// </summary>
public class QuestPanelTests(ITestOutputHelper output)
{
    static readonly QuestPanelFinder Finder = new(TestData.Config.QuestPanel);
    static readonly DateTime T0 = new(2026, 11, 19, 6, 20, 0);
    static string Field(string f) => Path.Combine(TestData.SamplesDir, "field-2026-11-19", f);

    static Frame Load(string path)
    {
        using var bmp = new Bitmap(path);
        return Frame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
    }

    public static readonly string[] Positives =
        ["levelup_quest_popup_full.jpg", "false_shield_out_full.jpg", "full/062059_332_fuelLow_full.jpg", "full/083814_332_fuelLow_full.jpg"];

    [Fact]
    public void Every_frame_with_the_panel_is_recognised()
    {
        foreach (var f in Positives)
        {
            var finder = new QuestPanelFinder(TestData.Config.QuestPanel);
            Assert.True(finder.IsPresent(Load(Field(f)), HudScale.One), f);
            output.WriteLine($"{f}: check glyph box at {finder.LastAt}");
            Assert.InRange(finder.LastAt.X, 762, 764);
            Assert.InRange(finder.LastAt.Y, 149, 151);
        }
    }

    [Fact]
    public void No_other_sample_or_collected_full_frame_is_taken_for_the_panel()
    {
        var positives = Positives.Select(p => Path.GetFullPath(Field(p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(TestData.SamplesDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .Where(f => !positives.Contains(Path.GetFullPath(f)))
            .Where(f => { using var b = Image.FromFile(f); return b.Width >= 1000 && b.Height >= 700; })
            .ToList();
        foreach (var f in files)
        {
            var frame = Load(f);
            // At 1.0 and as if the HUD were stretched (looser glyph match), wherever the scale is.
            foreach (var s in new[] { HudScale.One, new HudScale(1.25, 1.25), new HudScale(1.305, 1.3) })
                Assert.False(new QuestPanelFinder(TestData.Config.QuestPanel).IsPresent(frame, s), $"{f} at {s}");
        }
        output.WriteLine($"{files.Count} full-size images, no hit at 1.0, 1.25 or 1.3");
        Assert.True(files.Count >= 25);
    }

    /// <summary>SYNTHETIC: the sample stretched by the HUD scale (no real ~1.3 frame with the panel
    /// exists), placed at <paramref name="left"/> on a client of the given width.</summary>
    static Frame Stretched(double up, int clientW, int left)
    {
        using var src = new Bitmap(Field("levelup_quest_popup_full.jpg"));
        int w = (int)Math.Round(1024 * up), h = (int)Math.Round(768 * up);
        using var client = new Bitmap(clientW, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(client))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(left, 0, w, h));
        }
        return Frame.FromBitmap(client, new Rectangle(0, 0, clientW, h));
    }

    [Theory]
    [InlineData(1.305, 1336, 0)]      // stretched about the top-left
    [InlineData(1.305, 2560, 612)]    // stretched and centred on a wide client (anchor unknown)
    [InlineData(1.25, 1280, 0)]
    public void Stretched_panel_is_recognised_at_about_1_3(double up, int clientW, int left)
    {
        var finder = new QuestPanelFinder(TestData.Config.QuestPanel);
        Assert.True(finder.IsPresent(Stretched(up, clientW, left), new HudScale(up, up)));
        output.WriteLine($"{up} at {left}: {finder.LastAt}");
    }

    // ------------------------------------------------------------- alert logic

    sealed class Rig
    {
        public readonly HudReadings Readings = new();
        public readonly HudOptions Options = new();
        public readonly List<WatchEvent> Events = [];
        public readonly QuestPanelDetector Quest;
        public readonly UnreadableDetector Unreadable;

        public Rig()
        {
            Unreadable = new UnreadableDetector(TestData.Config, Readings, Options, () => 0, id => Quest!.Covers(id));
            Quest = new QuestPanelDetector(TestData.Config, Readings, Options, () => Unreadable.LimitSeconds);
            foreach (var r in TestData.Config.Unreadable.Rows) Readings.Set(r[0], "20");
            Readings.Set(AlertIds.FuelLow, "~45%");
        }

        /// <summary>One poll per second for seconds [from, to): panel seen or not, then the unreadable rule.</summary>
        public void Poll(int from, int to, bool panel)
        {
            for (int s = from; s < to; s++)
            {
                var now = T0.AddSeconds(s);
                if (panel) Quest.Seen(now, 1024, 768, Events); else Quest.NotSeen();
                Unreadable.Process(now, 1024, 768, Events);
            }
        }
    }

    [Fact]
    public void Panel_over_the_missile_counter_sends_one_level_up_message_instead_of_the_generic_one()
    {
        var rig = new Rig();
        rig.Poll(0, 5, panel: false);                             // missile readable earlier
        rig.Readings.Set(AlertIds.MissileEmpty, RowReading.Unknown);
        rig.Poll(5, 16, panel: true);                             // 10 s: not longer than the limit yet
        Assert.Empty(rig.Events);
        rig.Poll(16, 200, panel: true);                           // hours under the panel
        var e = Assert.Single(rig.Events);
        Assert.Equal("มีภารกิจใหม่เข้ามา หน้าเควสบังข้อมูลอยู่", e.Message);
        Assert.Equal(AlertIds.Unreadable, e.AlertId);
        Assert.Equal(new Rectangle(0, 0, 1024, 768), e.Crop);  // full client frame
        Assert.Equal([AlertIds.MissileEmpty], e.Unreadable!.Select(r => r.Id));
        Assert.Equal(T0.AddSeconds(16), e.Time);

        // Another row going unreadable meanwhile still gets the generic message, without the missile.
        rig.Readings.Set(AlertIds.Kill300, RowReading.Unknown);
        rig.Poll(200, 215, panel: true);
        Assert.Equal(2, rig.Events.Count);
        Assert.Equal("อ่านค่าไม่ได้ : นับมอน", rig.Events[1].Message);
    }

    [Fact]
    public void Once_per_appearance_and_re_armed_when_the_panel_is_gone()
    {
        var rig = new Rig();
        rig.Poll(0, 5, panel: false);
        rig.Readings.Set(AlertIds.MissileEmpty, RowReading.Unknown);
        rig.Poll(5, 30, panel: true);
        Assert.Single(rig.Events);
        rig.Poll(30, 32, panel: false);                           // two polls without it: same appearance
        rig.Poll(32, 60, panel: true);
        Assert.Single(rig.Events);
        rig.Readings.Set(AlertIds.MissileEmpty, "01500");         // closed: missile readable again
        rig.Poll(60, 63, panel: false);
        rig.Readings.Set(AlertIds.MissileEmpty, RowReading.Unknown);
        rig.Poll(63, 80, panel: true);                            // next level-up
        Assert.Equal(2, rig.Events.Count);
        Assert.All(rig.Events, e => Assert.Equal("มีภารกิจใหม่เข้ามา หน้าเควสบังข้อมูลอยู่", e.Message));
    }

    [Fact]
    public void Nothing_is_sent_when_the_unreadable_row_is_switched_off()
    {
        var rig = new Rig();
        rig.Options.SetEnabled(AlertIds.Unreadable, false);
        rig.Poll(0, 5, panel: false);
        rig.Readings.Set(AlertIds.MissileEmpty, RowReading.Unknown);
        rig.Poll(5, 60, panel: true);
        Assert.Empty(rig.Events);
    }

    [Fact]
    public void Panel_is_not_searched_while_the_covered_rows_read_values()
    {
        // A frame WITH the panel, but the missile row reads a value: no search, no alert.
        var readings = new HudReadings();
        var options = new HudOptions();
        var q = new QuestPanelDetector(TestData.Config, readings, options, () => 10);
        readings.Set(AlertIds.MissileEmpty, "01500");
        var events = new List<WatchEvent>();
        var frame = Load(Field("levelup_quest_popup_full.jpg"));
        for (int s = 0; s < 30; s++) q.Process(frame, HudScale.One, T0.AddSeconds(s), events);
        Assert.Empty(events);
        Assert.False(q.Present);
        readings.Set(AlertIds.MissileEmpty, RowReading.Unknown);
        for (int s = 30; s < 45; s++) q.Process(frame, HudScale.One, T0.AddSeconds(s), events);
        Assert.True(q.Present);
        Assert.Equal("มีภารกิจใหม่เข้ามา หน้าเควสบังข้อมูลอยู่", Assert.Single(events).Message);
    }
}
