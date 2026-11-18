using System.Drawing;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Diagnostics;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Tests;

/// <summary>Collection mode "อัตโนมัติ": each rule turns a region's collection on and off, with
/// the pre window (from the ring) and the post window.</summary>
public sealed class AutoCollectTests : IDisposable
{
    static readonly DateTime T0 = new(2026, 11, 18, 12, 0, 0);
    static readonly WatcherConfig C = TestData.Config;
    static readonly string[] Regions = C.Collect.Regions.Select(r => r.Name).ToArray();
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "sw-auto-" + Guid.NewGuid().ToString("N"));
    readonly Dictionary<string, string> _rows = new(StringComparer.OrdinalIgnoreCase);
    readonly AutoCollectRules _rules = new(C, Regions);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    AutoInputs In(int second, HudScale? scale = null, bool hud = true) =>
        new(T0.AddSeconds(second), scale ?? HudScale.One, hud, id => _rows.GetValueOrDefault(id, "—"),
            id => C.Items.Items.FirstOrDefault(i => i.Id == id)?.DefaultThreshold ?? 10);

    int R(string name) => _rules.IndexOf(name);

    string[] Active() => Regions.Where((_, i) => _rules.IsActive(i)).ToArray();

    /// <summary>Holds a reading for seconds [0, holdUntil) then clears it; checks on / post window / off.</summary>
    void AssertOnThenOffAfterPost(string row, string value, string region, string rule)
    {
        _rows[row] = value;
        _rules.Evaluate(In(0));
        Assert.True(_rules.IsActive(R(region)));
        Assert.True(_rules.Started(R(region)));
        Assert.Equal(rule, _rules.RuleOf(R(region)));
        Assert.Equal([region], Active());
        _rules.Evaluate(In(1));
        Assert.False(_rules.Started(R(region)));
        _rows[row] = "—";
        _rules.Evaluate(In(2));                       // rule false, post window running
        Assert.True(_rules.IsActive(R(region)));
        _rules.Evaluate(In(11));                      // last true at 1 s + 10 s post = 11 s
        Assert.False(_rules.IsActive(R(region)));
        Assert.True(_rules.Stopped(R(region)));
        Assert.Empty(Active());
    }

    [Fact]
    public void A_fuel_below_30_percent_collects_the_gauge()
    {
        AssertOnThenOffAfterPost(AlertIds.FuelLow, "~29%", "gauge", AutoCollectRules.FuelLow);
        _rows[AlertIds.FuelLow] = "~30%";
        _rules.Evaluate(In(30));
        Assert.Empty(Active());
        _rows[AlertIds.FuelLow] = ">90%";
        _rules.Evaluate(In(31));
        Assert.Empty(Active());
    }

    [Fact]
    public void B_hud_scale_1_2_or_more_collects_every_region()
    {
        _rules.Evaluate(In(0, new HudScale(1.19, 1.19)));
        Assert.Empty(Active());
        _rules.Evaluate(In(1, new HudScale(1.2, 1.2)));
        Assert.Equal(Regions, Active());
        Assert.All(Enumerable.Range(0, Regions.Length), i => Assert.Equal(AutoCollectRules.HudScaleRule, _rules.RuleOf(i)));
        _rules.Evaluate(In(2, HudScale.One));
        _rules.Evaluate(In(12, HudScale.One));
        Assert.Empty(Active());
    }

    [Fact]
    public void C_kill_counter_290_or_more_collects_the_kill_panel()
    {
        _rows[AlertIds.Kill300] = "289";
        _rules.Evaluate(In(-5));
        Assert.Empty(Active());
        AssertOnThenOffAfterPost(AlertIds.Kill300, "290", "kill", AutoCollectRules.KillNear);
    }

    [Fact]
    public void D_item_count_within_2_of_its_threshold_collects_the_item_bar()
    {
        int threshold = C.Items.Items.Single(i => i.Id == AlertIds.AmmoBox).DefaultThreshold;
        _rows[AlertIds.AmmoBox] = (threshold + 3).ToString();
        _rules.Evaluate(In(-5));
        Assert.Empty(Active());
        AssertOnThenOffAfterPost(AlertIds.AmmoBox, (threshold + 2).ToString(), "items", AutoCollectRules.ItemNear);
    }

    [Fact]
    public void E_either_ammo_counter_below_50_collects_the_ammo_strip()
    {
        _rows[AlertIds.GunAmmoEmpty] = "50";
        _rows[AlertIds.MissileEmpty] = "999";
        _rules.Evaluate(In(-5));
        Assert.Empty(Active());
        AssertOnThenOffAfterPost(AlertIds.MissileEmpty, "49", "ammo", AutoCollectRules.AmmoLow);
    }

    [Fact]
    public void F_an_alert_collects_its_region_until_10_s_after_and_death_collects_everything()
    {
        _rules.NoteAlert(new WatchEvent(WatchEventKind.Hud, "x", T0, AlertIds.Kill300));
        _rules.Evaluate(In(0));
        Assert.Equal(["kill"], Active());
        Assert.Equal(AutoCollectRules.Alert, _rules.RuleOf(R("kill")));
        _rules.Evaluate(In(9));
        Assert.Equal(["kill"], Active());
        _rules.Evaluate(In(10));
        Assert.Empty(Active());

        _rules.NoteAlert(new WatchEvent(WatchEventKind.Death, "ตาย", T0.AddSeconds(20)));
        _rules.Evaluate(In(20));
        Assert.Equal(Regions, Active());
        _rules.Evaluate(In(30));
        Assert.Empty(Active());
    }

    [Fact]
    public void G_a_row_unknown_for_more_than_5_s_with_the_hud_present_collects_its_region()
    {
        _rows[AlertIds.Kill300] = "?";
        for (int s = 0; s <= 5; s++)
        {
            _rules.Evaluate(In(s));
            Assert.Empty(Active());
        }
        _rules.Evaluate(In(6));
        Assert.Equal(["kill"], Active());
        Assert.Equal(AutoCollectRules.Unknown, _rules.RuleOf(R("kill")));
        _rows[AlertIds.Kill300] = "120";
        _rules.Evaluate(In(7));
        _rules.Evaluate(In(17));
        Assert.Empty(Active());

        // Without the HUD (ring not measured) "?" never counts.
        var other = new AutoCollectRules(C, Regions);
        _rows[AlertIds.Kill300] = "?";
        for (int s = 0; s < 20; s++) other.Evaluate(In(s, hud: false));
        Assert.All(Enumerable.Range(0, Regions.Length), i => Assert.False(other.IsActive(i)));
    }

    [Fact]
    public void H_implausible_jumps_between_stable_reads_collect_that_region()
    {
        // Item: 10 (stable) -> 61 (stable): +51 > itemJump (50).
        int t = 0;
        void Feed(string row, string value, int polls) { _rows[row] = value; for (int i = 0; i < polls; i++) _rules.Evaluate(In(t++)); }
        Feed(AlertIds.MonsterCard, "10", 2);
        Feed(AlertIds.MonsterCard, "61", 1);
        Assert.False(_rules.IsActive(R("items")));     // one poll is not stable yet
        Feed(AlertIds.MonsterCard, "61", 1);
        Assert.True(_rules.IsActive(R("items")));
        Assert.Equal(AutoCollectRules.Jump, _rules.RuleOf(R("items")));

        // Kill counter going down.
        Feed(AlertIds.Kill300, "150", 2);
        Assert.False(_rules.IsActive(R("kill")));
        Feed(AlertIds.Kill300, "149", 2);
        Assert.True(_rules.IsActive(R("kill")));

        // Ammo: +1000 is allowed, +1001 is a jump.
        Feed(AlertIds.GunAmmoEmpty, "2000", 2);
        Feed(AlertIds.GunAmmoEmpty, "3000", 2);
        Assert.False(_rules.IsActive(R("ammo")));
        Feed(AlertIds.GunAmmoEmpty, "4001", 2);
        Assert.True(_rules.IsActive(R("ammo")));
        Assert.Equal(AutoCollectRules.Jump, _rules.RuleOf(R("ammo")));
    }

    // ------------------------------------------------------------------ with the collector

    /// <summary>Noise; with <paramref name="hpRed"/> a block in the gauge's HP-red key colour, so
    /// the gauge region's change test sees a change between the two frames.</summary>
    static Frame Noise(int w, int h, int seed, bool hpRed)
    {
        using var bmp = new Bitmap(w, h);
        var rnd = new Random(seed);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            for (int i = 0; i < 300; i++)
                using (var b = new SolidBrush(Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256))))
                    g.FillRectangle(b, rnd.Next(w), rnd.Next(h), 30, 12);
            if (hpRed)
                using (var red = new SolidBrush(Color.FromArgb(160, 4, 4)))
                    g.FillRectangle(red, 20, 60, 120, 80);
        }
        return Frame.FromBitmap(bmp, new Rectangle(0, 0, w, h));
    }

    [Fact]
    public void Collector_saves_only_active_regions_with_the_pre_window_and_rule_names_in_the_index()
    {
        var ring = new HudRing(C.Collect.Regions.Select(r => new CollectRegion(r)), 10);
        var store = new CollectStore(Path.Combine(_tmp, "collect"), 200L * 1024 * 1024, queueLimit: 500);
        store.Start();
        var changes = new List<string>();
        var collector = new DataCollector(C.Collect, store, _rules, ring, C.Diagnostics.Auto.PreSeconds);
        collector.AutoChanged += (region, rule) => changes.Add($"{region}:{rule ?? "off"}");
        var frames = new[] { Noise(1600, 1024, 1, hpRed: true), Noise(1600, 1024, 1, hpRed: false) };

        void Poll(int second)
        {
            var f = frames[second % 2];                // content changes every poll
            ring.Push(f, HudScale.One, T0.AddSeconds(second));
            collector.OnFrame(f, HudScale.One, T0.AddSeconds(second), In(second));
        }

        for (int s = 0; s < 20; s++) Poll(s);           // nothing true: nothing saved
        _rows[AlertIds.FuelLow] = "~12%";
        for (int s = 20; s < 25; s++) Poll(s);          // fuel low 20..24
        _rows[AlertIds.FuelLow] = "~45%";
        for (int s = 25; s < 60; s++) Poll(s);          // post window until 34, then off
        collector.Dispose();                            // waits for the writer

        Assert.Equal(["gauge:fuel_low", "gauge:off"], changes);
        var rows = File.ReadAllLines(Path.Combine(_tmp, "collect", "2026-11-18", CollectStore.IndexFileName)).Skip(1)
            .Select(l => l.Split(',')).ToList();
        Assert.All(rows, r => Assert.Equal("gauge", r[1]));
        var times = rows.Select(r => (int)(DateTime.Parse(r[0]) - T0).TotalSeconds).ToList();
        var pre = rows.Where(r => r[7] == "pre+fuel_low").Select(r => (int)(DateTime.Parse(r[0]) - T0).TotalSeconds).ToList();
        Assert.Equal([11, 13, 15, 17, 19], pre);        // ring holds 11..20 (20 = now, saved live); one per 2 s
        var live = rows.Where(r => r[7] != "pre+fuel_low").ToList();
        Assert.All(live, r => Assert.EndsWith("+fuel_low", r[7]));
        var liveTimes = live.Select(r => (int)(DateTime.Parse(r[0]) - T0).TotalSeconds).ToList();
        Assert.Equal(20, liveTimes.Min());
        Assert.InRange(liveTimes.Max(), 32, 34);       // post window: last true at 24 s, +10 s
        Assert.True(liveTimes.Zip(liveTimes.Skip(1)).All(p => p.Second - p.First >= 2)); // rate limit kept
    }
}
