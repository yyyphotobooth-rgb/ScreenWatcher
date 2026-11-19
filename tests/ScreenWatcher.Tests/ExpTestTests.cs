using System.Drawing;
using ScreenWatcher.Alerts;
using ScreenWatcher.Bot;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.ExpTest;
using ScreenWatcher.Imaging;
using Xunit.Abstractions;
using static ScreenWatcher.Tests.HudTestData;

namespace ScreenWatcher.Tests;

/// <summary>"ทดสอบ EXP": map label, runner logic, images, store, service and /exp.</summary>
public class ExpTestTests(ITestOutputHelper output)
{
    static readonly WatcherConfig C = TestData.Config;
    static readonly DateTime T0 = new(2026, 11, 19, 14, 0, 0);
    static readonly FullSample Inventory = new("field-2026-11-19/inventory_full_message.png", 0, 0, 1024, 768);   // Daisy, โซน 8
    static readonly FullSample Town = new("field-2026-11-19/full/095352_339_full_event_unreadable.png", 0, 0, 1024, 768); // เมืองไบเจนิว

    static MapLabel Label(FullSample s) => new MapLabelReader(C.ExpTest).Capture(s.Load(), HudScale.One);

    // ------------------------------------------------------------- map label

    [Fact]
    public void Map_label_crops_sit_at_the_measured_top_right_offsets_at_every_scale()
    {
        var r = new MapLabelReader(C.ExpTest);
        Assert.Equal((new Rectangle(764, 0, 260, 17), new Rectangle(866, 161, 58, 19)), r.Locate(1024, 768, HudScale.One));
        // 1920x1200 at 1.11: name text x W-121..W-37, zone x W-167..W-129 / y 184..193 (live capture).
        var (n, z) = r.Locate(1920, 1200, new HudScale(1.1125, 1.115));
        Assert.True(n.Left <= 1920 - 121 && n.Bottom >= 18 && n.Right == 1920);
        Assert.True(z.Left <= 1920 - 167 && z.Right >= 1920 - 129 && z.Top <= 184 && z.Bottom >= 194);
        // 1920x1409 at 1.315: zone y 223..229, x W-198..W-155 (dead_1920x1440_missed).
        (n, z) = r.Locate(1920, 1421, Dead1920x1440.Scale);
        Assert.True(z.Left <= 1920 - 198 && z.Right >= 1920 - 155 && z.Top <= 216 && z.Bottom >= 232);
        // Not in the HUD ring.
        Assert.DoesNotContain(C.Collect.Regions, x => x.Name.StartsWith("map"));
    }

    [Fact]
    public void Different_map_names_are_a_map_change()
    {
        var m = new MapLabelReader(C.ExpTest).Compare(Label(Inventory), Label(Town));
        output.WriteLine(m.ToString());
        Assert.True(m.Comparable);
        Assert.True(m.Changed);
        Assert.Equal("name", m.Why);
    }

    [Fact]
    public void Different_zone_labels_are_a_map_change()
    {
        var m = new MapLabelReader(C.ExpTest).Compare(Label(Inventory), Label(Live1));   // โซน 8 vs โซน 4, same map
        output.WriteLine(m.ToString());
        Assert.True(m.Changed);
        Assert.Equal(0, m.NameDiff);
        Assert.Equal("zone", m.Why);
    }

    [Theory]
    [InlineData(0, 1)]   // โซน 4: sky (live_printwindow) vs brown ground (alive_small_a)
    [InlineData(1, 2)]   // โซน 4: brown ground vs sea-blue sky (missile_empty)
    [InlineData(3, 4)]   // โซน 8: inventory_full vs killcounter_298 (other sky)
    public void Same_label_over_different_scenery_is_no_change(int a, int b)
    {
        FullSample[] s = [Live1, SmallA, MissileEmpty, Inventory, Kill298];
        var m = new MapLabelReader(C.ExpTest).Compare(Label(s[a]), Label(s[b]));
        output.WriteLine(m.ToString());
        Assert.True(m.Comparable);
        Assert.False(m.Changed);
        Assert.Equal((0, 0), (m.NameDiff, m.ZoneDiff));
    }

    [Fact]
    public void Owner_client_label_equals_itself_and_a_blank_label_is_not_comparable()
    {
        using var bmp = new Bitmap(Path.Combine(TestData.SamplesDir, "live-2026-11-19", "full_1920x1200.png"));
        var frame = Frame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var r = new MapLabelReader(C.ExpTest);
        var a = r.Capture(frame, new HudScale(1.1125, 1.115));
        Assert.Equal(new MapCompare(true, false, 0, 0, "same"), r.Compare(a, a));
        var blank = r.Capture(Frame.FromBitmap(new Bitmap(1024, 768), new Rectangle(0, 0, 1024, 768)), HudScale.One);
        Assert.False(r.Compare(blank, blank).Comparable);
    }

    // ------------------------------------------------------------- runner

    sealed class Rig
    {
        public readonly ExpTestRunner R = new(C, null);
        public readonly List<ExpResult> Results = [];
        public readonly List<string> Decided = [];
        public int T;
        public MapLabel? StartMap, EndMap;
        public Rig()
        {
            R.Finished += Results.Add;
            R.StartDecided += (o, s) => Decided.Add($"{o.Status}:{s}");
        }
        public DateTime Now => T0.AddSeconds(T);
        /// <summary>One poll per second. pct in hundredths; null level = unreadable line.</summary>
        public void Poll(int? level, int? pct, int? kill = null, bool killGone = false, bool dead = false, int polls = 1)
        {
            for (int i = 0; i < polls; i++, T++)
            {
                var k = kill is { } v ? KillObservation.Of(v) : killGone ? KillObservation.Gone : KillObservation.Unreadable;
                var map = R.Phase == ExpPhase.Starting ? StartMap : EndMap;
                R.OnPoll(Now, level is { } l ? new LevelLine(l, pct) : null, k, dead,
                    () => (new BgraImage(new byte[4 * 4 * 4], 4, 4), map));
            }
        }
        public Task<ExpStartOutcome> Start(int minutes = 10) => R.RequestStart(minutes, "button", Now);
    }

    [Fact]
    public async Task Start_needs_a_stable_reading_and_the_gain_is_end_minus_start()
    {
        var g = new Rig();
        var t = g.Start(1);
        g.Poll(88, 5061);                                   // first reading: nothing to compare yet
        Assert.False(t.IsCompleted);
        g.Poll(88, 5061);
        var o = await t;
        Assert.Equal(ExpStartStatus.Started, o.Status);
        Assert.Equal("เริ่มทดสอบ EXP · 1 นาที", o.Text);
        Assert.Equal(T0.AddSeconds(1), o.Start!.Time);
        g.Poll(88, 5100, polls: 58);
        g.Poll(88, 5200, polls: 4);                         // stable at end-1 (61 s) and end (62 s)
        var r = Assert.Single(g.Results);
        Assert.Equal(139, r.GainHundredths);
        Assert.Equal("ทดสอบ EXP จบ · 1 นาที · ได้ +1.39% (Lv.88 50.61% → Lv.88 52.00%) · ยิงมอน ไม่ทราบ", r.Text);
        Assert.True(r.Comparable);
        Assert.Equal(ExpPhase.Idle, g.R.Phase);
    }

    [Fact]
    public void Gain_across_a_level_up_counts_100_percent_per_level()
    {
        Assert.Equal(170, ExpResult.Gain(88, 9950, 89, 120));
        Assert.Equal(10000 + 3, ExpResult.Gain(88, 5000, 89, 5003));
        var g = new Rig();
        _ = g.Start(1);
        g.Poll(88, 9950, polls: 2);
        g.Poll(88, 9990, polls: 20);
        g.Poll(89, 50, polls: 40);
        var r = Assert.Single(g.Results);
        Assert.Equal(100, r.GainHundredths);
        Assert.True(r.Flags.LevelUp);
        Assert.Contains("· มีเลเวลอัพ", r.Text);
        Assert.True(r.Comparable);                          // a level-up alone does not stop comparing
    }

    [Fact]
    public void Unstable_readings_do_not_start_a_test()
    {
        var g = new Rig();
        var t = g.Start();
        g.Poll(88, 5061);
        g.Poll(88, 5300);                                   // +2.39 % in one poll: not stable
        g.Poll(87, 5300);                                   // other level
        g.Poll(87, null);                                   // percentage not readable
        Assert.False(t.IsCompleted);
    }

    [Fact]
    public async Task Start_is_refused_when_nothing_stable_is_read_within_15_s()
    {
        var g = new Rig();
        var t = g.Start();
        g.Poll(null, null, polls: 15);
        Assert.False(t.IsCompleted);
        g.Poll(null, null, polls: 2);
        var o = await t;
        Assert.Equal(ExpStartStatus.Refused, o.Status);
        Assert.Equal(ExpTexts.Refused, o.Text);
        Assert.Equal(["Refused:button"], g.Decided);
        Assert.Equal(ExpPhase.Idle, g.R.Phase);
        // Paused / no game: refused the same way.
        t = g.Start();
        for (int i = 0; i < 17; i++) g.R.OnPaused(g.Now.AddSeconds(i));
        Assert.Equal(ExpStartStatus.Refused, (await t).Status);
    }

    [Fact]
    public void End_without_a_stable_reading_within_15_s_is_unusable_and_gives_no_number()
    {
        var g = new Rig();
        _ = g.Start(1);
        g.Poll(88, 5061, polls: 2);
        g.Poll(88, 5100, polls: 30);
        g.Poll(null, null, polls: 30);                      // line covered from 31 s .. past the end
        Assert.Empty(g.Results);
        g.Poll(null, null, polls: 16);
        var r = Assert.Single(g.Results);
        Assert.False(r.Usable);
        Assert.Null(r.GainHundredths);
        Assert.Equal("ทดสอบ EXP จบ · 1 นาที · ผลใช้ไม่ได้ อ่านค่า EXP ตอนจบไม่ได้ (เริ่ม Lv.88 50.61%) · ยิงมอน ไม่ทราบ", r.Text);
        Assert.DoesNotContain("ได้ +", r.Text);
    }

    [Fact]
    public void End_takes_the_stable_reading_nearest_the_end_time()
    {
        // Last stable reading 10 s before the end, next one 4 s after: the one after is nearer.
        var g = new Rig();
        _ = g.Start(1);
        g.Poll(88, 5000, polls: 2);                         // start at 1 s, end at 61 s
        g.Poll(88, 5100, polls: 50);                        // stable up to 51 s
        g.Poll(null, null, polls: 14);                      // 52..65 s
        g.Poll(88, 5200, polls: 2);                         // 66 s, stable at 67 s? no: 66 has no previous -> 67 s is 6 s after
        var r = Assert.Single(g.Results);
        Assert.Equal(T0.AddSeconds(67), r.End!.Time);
        Assert.Equal(200, r.GainHundredths);

        // Stable 1 s before the end, nothing after: the one before is taken once nothing later can be nearer.
        g = new Rig();
        _ = g.Start(1);
        g.Poll(88, 5000, polls: 2);
        g.Poll(88, 5100, polls: 60);                        // up to 61 s = end - 0 ... stable at 60 s
        g.Poll(null, null, polls: 3);
        r = Assert.Single(g.Results);
        Assert.True((T0.AddSeconds(61) - r.End!.Time).TotalSeconds <= 1);
    }

    [Fact]
    public async Task Only_one_test_at_a_time_and_a_second_start_gets_the_time_left()
    {
        var g = new Rig();
        var first = g.Start(10);
        Assert.Equal(ExpStartStatus.AlreadyStarting, (await g.Start(5)).Status);
        g.Poll(88, 5000, polls: 2);
        Assert.Equal(ExpStartStatus.Started, (await first).Status);
        g.Poll(88, 5000, polls: 30);
        var busy = await g.Start(5);
        Assert.Equal(ExpStartStatus.Busy, busy.Status);
        Assert.Equal("กำลังทดสอบ EXP อยู่ เหลืออีก 9:29", busy.Text);
        Assert.Equal(TimeSpan.FromSeconds(569), g.R.TimeLeft(g.Now));
    }

    [Fact]
    public async Task Cancel_stops_the_test_without_a_result()
    {
        var g = new Rig();
        Assert.False(g.R.Cancel());
        var t = g.Start(1);
        Assert.True(g.R.Cancel());                          // while starting
        Assert.Equal(ExpTexts.Cancelled, (await t).Text);
        _ = g.Start(1);
        g.Poll(88, 5000, polls: 5);
        Assert.True(g.R.Cancel());                          // while running
        g.Poll(88, 5100, polls: 120);
        Assert.Empty(g.Results);
        Assert.Equal(ExpPhase.Idle, g.R.Phase);
    }

    [Fact]
    public void Kills_sum_the_counter_rises_including_its_restart_after_300()
    {
        var g = new Rig();
        _ = g.Start(1);
        g.Poll(88, 5000, kill: 290, polls: 2);              // started at 1 s with 290 on the panel
        int[] seq = [292, 295, 298, 298];
        foreach (int k in seq) g.Poll(88, 5000, kill: k);
        g.Poll(88, 5000, killGone: true, polls: 2);         // 300 reached: panel gone
        foreach (int k in new[] { 1, 1, 3, 3 }) g.Poll(88, 5000, kill: k);
        g.Poll(88, 5000, kill: 5, polls: 60);
        var r = Assert.Single(g.Results);
        // 290 -> 298: 8; 298 -> (300) -> 1: 3; 1 -> 3 -> 5: 4.
        Assert.Equal(15, r.Kills);
        Assert.Contains("ยิงมอน 15 ตัว", r.Text);
    }

    [Fact]
    public void Kills_are_unknown_when_the_counter_was_not_visible_for_most_of_the_test()
    {
        var g = new Rig();
        _ = g.Start(1);
        g.Poll(88, 5000, kill: 10, polls: 2);
        g.Poll(88, 5000, kill: 12, polls: 20);
        g.Poll(88, 5000, killGone: true, polls: 45);
        var r = Assert.Single(g.Results);
        Assert.Null(r.Kills);
        Assert.Contains("ยิงมอน ไม่ทราบ", r.Text);
    }

    [Fact]
    public void Death_drop_and_pause_are_flagged_and_the_last_two_make_it_not_comparable()
    {
        var g = new Rig();
        _ = g.Start(1);
        g.Poll(88, 5000, polls: 2);
        g.Poll(88, 5000, dead: true, polls: 3);
        g.Poll(88, 5100, polls: 60);
        var r = Assert.Single(g.Results);
        Assert.True(r.Flags.Died);
        Assert.True(r.Comparable);
        Assert.EndsWith("· ตายระหว่างทดสอบ", r.Text);

        g = new Rig();
        _ = g.Start(1);
        g.Poll(88, 5000, polls: 2);
        g.R.OnNoFrame(g.Now, gameMissing: true); g.T++;
        g.R.OnPaused(g.Now); g.T++;
        g.Poll(88, 5100, polls: 60);
        r = Assert.Single(g.Results);
        Assert.False(r.Comparable);
        Assert.EndsWith("· เกมหลุด · หยุดเฝ้าระหว่างทดสอบ · ใช้เทียบไม่ได้", r.Text);
    }

    [Fact]
    public void Map_change_between_start_and_end_is_flagged_and_same_label_is_not()
    {
        var g = new Rig { StartMap = Label(Inventory), EndMap = Label(Live1) };   // โซน 8 -> โซน 4
        _ = g.Start(1);
        g.Poll(88, 5000, polls: 64);
        var r = Assert.Single(g.Results);
        Assert.True(r.Flags.MapChanged);
        Assert.EndsWith("· เปลี่ยนแมป · ใช้เทียบไม่ได้", r.Text);

        g = new Rig { StartMap = Label(Live1), EndMap = Label(SmallA) };          // โซน 4 over other scenery
        _ = g.Start(1);
        g.Poll(88, 5000, polls: 64);
        r = Assert.Single(g.Results);
        Assert.False(r.Flags.MapChanged);
        Assert.True(r.Comparable);
    }

    [Fact]
    public void Idle_runner_reads_nothing()
    {
        // No test: OnFrame returns before the percentage pass or any crop.
        var r = new ExpTestRunner(C, new LevelReader(C.Level));
        r.OnFrame(Inventory.Load(), HudScale.One, T0, 88, KillObservation.Gone, false);
        Assert.Equal(ExpPhase.Idle, r.Phase);
    }

    [Fact]
    public async Task Real_frames_through_OnFrame_start_with_the_read_percentage_and_crops()
    {
        var r = new ExpTestRunner(C, new LevelReader(C.Level));
        var frame = Inventory.Load();
        var t = r.RequestStart(10, "button", T0);
        r.OnFrame(frame, HudScale.One, T0.AddSeconds(1), 88, KillObservation.Of(100), false);
        r.OnFrame(frame, HudScale.One, T0.AddSeconds(2), 88, KillObservation.Of(100), false);
        var o = await t;
        Assert.Equal("Lv.88 50.61%", o.Start!.Text);
        Assert.Equal((260, 16), (o.Start.LevelCrop.Width, o.Start.LevelCrop.Height));
        Assert.Equal((260, 17 + 19), (o.Start.Map!.Composite().Width, o.Start.Map.Composite().Height));
    }

    // ------------------------------------------------------------- images and store

    static ExpResult Result(string id, DateTime start, double minutes, int startPct, int endPct, bool mapChanged = false, bool paused = false,
        int? kills = 50, MapLabel? map = null, MapLabel? endMap = null, int endLevel = 88)
    {
        var level = new FullSample("field-2026-11-19/inventory_full_message.png", 0, 0, 1024, 768).Load();
        var crop = BgraImage.Copy(level, new Rectangle(382, 752, 260, 16));
        var s = new ExpReading(start, 88, startPct, crop, map);
        var e = new ExpReading(start.AddMinutes(minutes), endLevel, endPct, crop, endMap ?? map);
        return new ExpResult(id, "button", (int)Math.Round(minutes), s, e, kills, new ExpFlags(endLevel > 88, false, false, mapChanged, paused), null);
    }

    [Fact]
    public void Result_image_holds_the_map_label_and_both_level_lines_and_the_end_map_when_it_changed()
    {
        var same = Result("a", T0, 10, 5000, 5200, map: Label(Inventory));
        var changed = Result("b", T0, 10, 5000, 5200, mapChanged: true, map: Label(Inventory), endMap: Label(Live1));
        using var a = new Bitmap(new MemoryStream(ExpTestImages.Result(same)));
        using var b = new Bitmap(new MemoryStream(ExpTestImages.Result(changed)));
        output.WriteLine($"{a.Size} {b.Size}");
        Assert.True(a.Width >= 520 + 24);                   // crops drawn at 2x (260 px wide)
        Assert.True(b.Height >= a.Height + 2 * 36);         // one more captioned map label at 2x
        var unusable = same with { End = null };
        using var c = new Bitmap(new MemoryStream(ExpTestImages.Result(unusable)));
        Assert.True(c.Height < a.Height);                   // no end crop
    }

    [Fact]
    public void Store_lists_the_last_comparable_results_by_gain_per_10_minutes()
    {
        string root = Path.Combine(Path.GetTempPath(), "sw-exp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ExpTestStore(root);
            var map = Label(Inventory);
            store.Save(Result("1", T0, 10, 5000, 5100, map: map), [1]);           // +1.00 / 10
            store.Save(Result("2", T0.AddHours(1), 5, 5000, 5100, map: map), [1]);  // +1.00 in 5 min = +2.00 / 10
            store.Save(Result("3", T0.AddHours(2), 20, 5000, 5300, map: map), [1]); // +3.00 in 20 = +1.50 / 10
            store.Save(Result("4", T0.AddHours(3), 10, 5000, 9000, mapChanged: true, map: map), [1]);   // not comparable
            store.Save(Result("5", T0.AddHours(4), 10, 5000, 9000, paused: true, map: map), [1]);       // not comparable
            var rows = store.LastComparable(10);
            Assert.Equal(["2", "3", "1"], rows.Select(r => r.Data.Id));
            Assert.Equal(200, rows[0].Data.GainPer10Min!.Value, 6);
            Assert.All(rows, r => Assert.NotNull(r.MapPng));
            Assert.Equal(["2", "3"], store.LastComparable(2).Select(r => r.Data.Id));   // the last two by time
            using var img = new Bitmap(new MemoryStream(ExpTestImages.List(rows)));
            using var one = new Bitmap(new MemoryStream(ExpTestImages.List(rows.Take(1).ToList())));
            Assert.Equal(2 * 64, img.Height - one.Height);
            Assert.True(File.Exists(Path.Combine(root, "1.json")) && File.Exists(Path.Combine(root, "1.png")) && File.Exists(Path.Combine(root, "1_map.png")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // ------------------------------------------------------------- service and /exp

    sealed class FakeInteraction(string name, ulong? channel, string? value = null) : ISlashInteraction
    {
        public string CommandName => name;
        public ulong? ChannelId => channel;
        public readonly List<string> Calls = [];
        public byte[]? File;
        public Task RespondEphemeralAsync(string text) { Calls.Add("ephemeral:" + text); return Task.CompletedTask; }
        public Task DeferAsync() { Calls.Add("defer"); return Task.CompletedTask; }
        public Task FollowupAsync(string text) { Calls.Add("followup:" + text); return Task.CompletedTask; }
        public Task FollowupWithFileAsync(byte[] png, string fileName, string text) { File = png; Calls.Add("file:" + text); return Task.CompletedTask; }
        public string? GetString(string n) => n == ExpCommandHandler.ValueOption ? value : null;
    }

    sealed class ServiceRig : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "sw-exp-" + Guid.NewGuid().ToString("N"));
        public readonly ExpTestRunner Runner = new(C, null);
        public readonly List<AlertMessage> Sent = [];
        public readonly ExpTestService Service;
        public readonly ExpCommandHandler Handler;
        public int T;
        public ServiceRig()
        {
            Service = new ExpTestService(Runner, new ExpTestStore(Root), m => { lock (Sent) Sent.Add(m); return true; }, null, 10, () => T0.AddSeconds(T)) { Synchronous = true };
            Handler = new ExpCommandHandler(Service, () => 42UL, 10, 1, 120);
        }
        public void Poll(int polls)
        {
            for (int i = 0; i < polls; i++, T++)
                Runner.OnPoll(T0.AddSeconds(T), new LevelLine(88, 5000 + T), KillObservation.Unreadable, false, () => (new BgraImage(new byte[16], 2, 2), null));
        }
        public async Task WaitStarted()
        {
            for (int i = 0; i < 100 && Runner.Phase == ExpPhase.Idle; i++) await Task.Delay(10);
        }
        public void Dispose() { Service.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task Exp_command_starts_with_default_10_minutes_and_replies_in_the_channel()
    {
        using var g = new ServiceRig();
        var i = new FakeInteraction("exp", 42);
        var handled = g.Handler.HandleAsync(i);
        await g.WaitStarted();
        g.Poll(2);
        Assert.True(await handled);
        Assert.Equal(["defer", "followup:เริ่มทดสอบ EXP · 10 นาที"], i.Calls);
        Assert.Empty(g.Sent);                              // the reply is the start message
        Assert.Equal(TimeSpan.FromMinutes(10), g.Runner.TimeLeft(T0.AddSeconds(1)));

        var again = new FakeInteraction("exp", 42, "5");
        await g.Handler.HandleAsync(again);
        Assert.Equal(["defer", "followup:กำลังทดสอบ EXP อยู่ เหลืออีก 9:59"], again.Calls);

        var cancel = new FakeInteraction("exp", 42, "cancel");
        await g.Handler.HandleAsync(cancel);
        Assert.Equal(["defer", "followup:ยกเลิกทดสอบ EXP แล้ว"], cancel.Calls);
        Assert.Equal(ExpPhase.Idle, g.Runner.Phase);
        var none = new FakeInteraction("exp", 42, "cancel");
        await g.Handler.HandleAsync(none);
        Assert.Equal(["defer", "followup:" + ExpTexts.NothingToCancel], none.Calls);
    }

    [Fact]
    public async Task Exp_command_with_minutes_runs_to_a_result_sent_with_its_image_and_list_shows_it()
    {
        using var g = new ServiceRig();
        var empty = new FakeInteraction("exp", 42, "list");
        await g.Handler.HandleAsync(empty);
        Assert.Equal(["defer", "followup:" + ExpTexts.ListEmpty], empty.Calls);

        var i = new FakeInteraction("exp", 42, "2");
        var handled = g.Handler.HandleAsync(i);
        await g.WaitStarted();
        g.Poll(2);
        await handled;
        Assert.Equal("followup:เริ่มทดสอบ EXP · 2 นาที", i.Calls[^1]);
        g.Poll(125);
        var m = Assert.Single(g.Sent);
        Assert.Equal(ExpTestService.AlertId, m.AlertId);
        Assert.StartsWith("ทดสอบ EXP จบ · 2 นาที · ได้ +1.20% (Lv.88 50.01% → Lv.88 51.21%)", m.Text);
        Assert.NotNull(m.Png);
        Assert.True(File.Exists(Path.Combine(g.Root, "20261119_140001.json")));

        var list = new FakeInteraction("exp", 42, "list");
        await g.Handler.HandleAsync(list);
        Assert.Equal(["defer", "file:" + ExpTexts.ListCaption], list.Calls);
        using var img = new Bitmap(new MemoryStream(list.File!));
        Assert.True(img.Width > 500);
    }

    [Fact]
    public async Task Exp_command_follows_the_channel_rule_and_rejects_bad_values()
    {
        using var g = new ServiceRig();
        var other = new FakeInteraction("exp", 7);
        await g.Handler.HandleAsync(other);
        Assert.Equal(["ephemeral:" + ExpCommandHandler.WrongChannelText], other.Calls);
        foreach (var bad in new[] { "0", "121", "abc", "-5" })
        {
            var i = new FakeInteraction("exp", 42, bad);
            await g.Handler.HandleAsync(i);
            Assert.Equal(["ephemeral:ใช้ /exp แล้วใส่จำนวนนาที 1-120, cancel หรือ list"], i.Calls);
        }
        Assert.Equal(ExpPhase.Idle, g.Runner.Phase);
        Assert.False(await g.Handler.HandleAsync(new FakeInteraction("ss", 42)));
    }

    [Fact]
    public async Task Button_start_and_cancel_send_their_texts_to_the_channel_and_a_refused_start_too()
    {
        using var g = new ServiceRig();
        var t = g.Service.StartAsync(10, ExpTestService.SourceButton);
        g.Poll(2);
        Assert.Equal("เริ่มทดสอบ EXP · 10 นาที", await t);
        Assert.Equal(ExpTexts.Cancelled, g.Service.Cancel(ExpTestService.SourceButton));
        Assert.Equal(["เริ่มทดสอบ EXP · 10 นาที", ExpTexts.Cancelled], g.Sent.Select(m => m.Text));

        t = g.Service.StartAsync(10, ExpTestService.SourceButton);
        for (int k = 0; k < 17; k++, g.T++) g.Runner.OnNoFrame(T0.AddSeconds(g.T), gameMissing: true);
        Assert.Equal(ExpTexts.Refused, await t);
        Assert.Equal(ExpTexts.Refused, g.Sent[^1].Text);
    }

    [Fact]
    public async Task Closing_the_app_drops_a_running_test_without_a_result()
    {
        var g = new ServiceRig();
        var t = g.Service.StartAsync(10, ExpTestService.SourceButton);
        g.Poll(2);
        await t;
        g.Dispose();
        Assert.Equal(ExpPhase.Idle, g.Runner.Phase);
        g.Poll(700);
        Assert.Single(g.Sent);                             // only the start message
    }
}
