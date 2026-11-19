using System.Drawing;
using System.Text.Json;
using ScreenWatcher.Alerts;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Diagnostics;
using ScreenWatcher.Imaging;
using ScreenWatcher.UI;

namespace ScreenWatcher.Tests;

/// <summary>Row "อ่านค่าไม่ได้เกิน N วินาที": which rows count, the limit, one combined message,
/// re-arming, the excluded cases, journal / evidence records and the auto-collection full frames.</summary>
public sealed class UnreadableTests : IDisposable
{
    static readonly DateTime T0 = new(2026, 11, 18, 12, 0, 0);
    static readonly WatcherConfig C = TestData.Config;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "sw-unread-" + Guid.NewGuid().ToString("N"));
    readonly HudReadings _readings = new();
    readonly HudOptions _options = new();
    readonly List<WatchEvent> _events = [];
    readonly List<UnreadableRow> _recovered = [];
    int _barHidden;
    readonly UnreadableDetector _d;

    public UnreadableTests()
    {
        _d = new UnreadableDetector(C, _readings, _options, () => _barHidden);
        _d.Recovered += _recovered.Add;
        foreach (var r in C.Unreadable.Rows) _readings.Set(r[0], RowReading.None);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    void Set(string id, string text) => _readings.Set(id, text);

    /// <summary>Polls once per second for seconds [from, to).</summary>
    void Poll(int from, int to)
    {
        for (int s = from; s < to; s++) _d.Process(T0.AddSeconds(s), 1024, 768, _events);
    }

    /// <summary>All rows show a value on seconds [0, 5): readable earlier in the session.</summary>
    void AllReadable()
    {
        Set(AlertIds.GunAmmoEmpty, "12345");
        Set(AlertIds.MissileEmpty, "00120");
        Set(AlertIds.FuelLow, "~45%");
        foreach (var i in C.Items.Items) Set(i.Id, "20");
        Set(AlertIds.Kill300, "150");
        Poll(0, 5);
    }

    [Fact]
    public void Config_lists_every_hud_row_in_table_order_with_a_short_name()
    {
        var hudRows = AlertRowCatalog.All.Select(r => r.Id).Where(id => id is not (AlertIds.Death or AlertIds.GameDropped or AlertIds.Unreadable or AlertIds.ExpStall));
        Assert.Equal(hudRows, C.Unreadable.Rows.Select(r => r[0]));
        Assert.All(C.Unreadable.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r[1])));
        Assert.Equal(AlertIds.Unreadable, AlertRowCatalog.All[^1].Id);   // last row in the table
        Assert.Equal(10, C.Unreadable.DefaultSeconds);
        Assert.Equal(10, AlertRowCatalog.Get(AlertIds.Unreadable).DefaultThreshold);
    }

    [Fact]
    public void Readable_then_unknown_longer_than_the_limit_alerts_once_with_the_full_frame()
    {
        AllReadable();
        Set(AlertIds.FuelLow, "?");
        Poll(5, 16);                                     // "?" since 5 s: 10 s at 15 s, not yet longer
        Assert.Empty(_events);
        Assert.Equal("ปกติ", _readings.Get(AlertIds.Unreadable).Text);
        Poll(16, 19);                                    // crosses at 16 s, combine window until 19 s
        Assert.Empty(_events);
        Assert.Equal("น้ำมัน", _readings.Get(AlertIds.Unreadable).Text);
        Poll(19, 20);
        var e = Assert.Single(_events);
        Assert.Equal(WatchEventKind.Hud, e.Kind);
        Assert.Equal(AlertIds.Unreadable, e.AlertId);
        Assert.Equal("เรดาร์ถูกบัง อ่านค่าไม่ได้: น้ำมัน", e.Message);
        Assert.Equal(new Rectangle(0, 0, 1024, 768), e.Crop);
        var row = Assert.Single(e.Unreadable!);
        Assert.Equal((AlertIds.FuelLow, "น้ำมัน", 14.0), (row.Id, row.Name, row.Seconds));
        Poll(20, 120);                                   // still "?": no second message
        Assert.Single(_events);

        // The composer sends the whole client frame with it.
        var frame = Frame.FromBitmap(new Bitmap(320, 200), new Rectangle(0, 0, 320, 200));
        var m = new AlertComposer(C).Compose(e with { Crop = new Rectangle(0, 0, 320, 200) }, frame, HudScale.One);
        using var png = new Bitmap(new MemoryStream(m.Png!));
        Assert.Equal(new Size(320, 200), png.Size);
    }

    [Fact]
    public void Showing_a_value_again_re_arms_the_row_and_reports_the_recovery()
    {
        AllReadable();
        Set(AlertIds.GunAmmoEmpty, "?");
        Poll(5, 20);
        Assert.Single(_events);
        Set(AlertIds.GunAmmoEmpty, "12000");
        Poll(20, 21);
        var rec = Assert.Single(_recovered);
        Assert.Equal((AlertIds.GunAmmoEmpty, 15.0), (rec.Id, rec.Seconds));
        Assert.Equal("ปกติ", _readings.Get(AlertIds.Unreadable).Text);
        Set(AlertIds.GunAmmoEmpty, "?");
        Poll(21, 40);
        Assert.Equal(2, _events.Count);
        Assert.Equal("เรดาร์ถูกบัง อ่านค่าไม่ได้: ปืนกล", _events[1].Message);
    }

    [Fact]
    public void A_row_readable_again_inside_the_combine_window_is_not_sent()
    {
        // Field trial 2026-11-19 09:56:44/47: the kill counter crossed, showed a value again on
        // the next poll, and 3 s later "อ่านค่าไม่ได้: นับมอน" went out anyway (0 s, row "ปกติ").
        AllReadable();
        Set(AlertIds.Kill300, "?");
        Poll(5, 17);                                     // crosses at 16 s, window until 19 s
        Set(AlertIds.Kill300, "79");
        Poll(17, 25);
        Assert.Empty(_events);
        Assert.Empty(_recovered);                        // nothing was reported, so no recovery either
        Assert.Equal("ปกติ", _readings.Get(AlertIds.Unreadable).Text);

        // Another row in the same window still goes out, without the recovered one.
        Set(AlertIds.Kill300, "?");
        Poll(25, 30);
        Set(AlertIds.FuelLow, "?");
        Poll(30, 39);                                    // kill crosses at 36 s (window until 39 s)
        Assert.Empty(_events);
        Set(AlertIds.Kill300, "80");                     // kill readable again at 39 s, before its message
        Poll(39, 45);                                    // fuel ("?" since 30 s) crosses at 41 s, sent at 44 s
        var e = Assert.Single(_events);
        Assert.Equal("เรดาร์ถูกบัง อ่านค่าไม่ได้: น้ำมัน", e.Message);

        // A row that recovers after its message was sent is reported as recovered, as before.
        Set(AlertIds.FuelLow, "~40%");
        Poll(45, 46);
        Assert.Equal(AlertIds.FuelLow, Assert.Single(_recovered).Id);
    }

    [Fact]
    public void Rows_crossing_within_3_s_share_one_message_and_a_later_row_sends_its_own()
    {
        AllReadable();
        Set(AlertIds.Kill300, "?");                      // crosses at 16 s
        Poll(5, 7);
        Set(AlertIds.GunAmmoEmpty, "?");                 // crosses at 18 s
        Set(AlertIds.HpPotions, "?");                    // crosses at 18 s
        Poll(7, 9);
        Set(AlertIds.FuelLow, "?");                      // crosses at 20 s: after the window (16..19)
        Poll(9, 30);

        Assert.Equal(2, _events.Count);
        Assert.Equal("เรดาร์ถูกบัง อ่านค่าไม่ได้: ปืนกล, ยาเลือด, นับมอน", _events[0].Message);   // table order
        Assert.Equal(T0.AddSeconds(19), _events[0].Time);
        Assert.Equal([AlertIds.GunAmmoEmpty, AlertIds.HpPotions, AlertIds.Kill300], _events[0].Unreadable!.Select(r => r.Id));
        Assert.Equal("เรดาร์ถูกบัง อ่านค่าไม่ได้: น้ำมัน", _events[1].Message);
        Assert.Equal(T0.AddSeconds(23), _events[1].Time);
        Assert.Equal("ปืนกล, น้ำมัน, ยาเลือด, นับมอน", _readings.Get(AlertIds.Unreadable).Text);
    }

    [Fact]
    public void Excluded_cases_never_alert()
    {
        AllReadable();
        // Item not on the bar (bar seen) and an item that ran out.
        Set(AlertIds.AmmoBox, RowReading.None);
        Set(AlertIds.MonsterCard, ItemDetector.GoneText);
        // Kill panel not shown.
        Set(AlertIds.Kill300, RowReading.None);
        // Row switched off by the user.
        _options.SetEnabled(AlertIds.MissileEmpty, false);
        Set(AlertIds.MissileEmpty, "?");
        Poll(5, 60);
        // An item that left the bar must show a value again before "?" counts.
        Set(AlertIds.AmmoBox, "?");
        Poll(60, 90);
        // A row that never showed a value in this session.
        _d.EndSession();
        Set(AlertIds.FuelLow, "?");
        Poll(90, 130);
        Assert.Empty(_events);
        Assert.Equal("ปกติ", _readings.Get(AlertIds.Unreadable).Text);
    }

    [Fact]
    public void A_whole_item_bar_not_seen_counts_as_unreadable_after_three_polls()
    {
        AllReadable();
        foreach (var i in C.Items.Items) Set(i.Id, RowReading.None);
        _barHidden = 1;                                  // just gone: neither counted nor cleared
        Poll(5, 7);
        _barHidden = 3;                                  // "?" from 7 s: crosses at 18, sent at 21
        Poll(7, 22);
        var e = Assert.Single(_events);
        Assert.Equal("เรดาร์ถูกบัง อ่านค่าไม่ได้: กล่องกระสุน, ขวดน้ำมัน, บัตรกันมอน, ยาเลือด, ยาเกราะ, ยา SP", e.Message);
    }

    [Fact]
    public void The_seconds_setting_is_used_and_persisted()
    {
        _options.SetThreshold(AlertIds.Unreadable, 20);
        AllReadable();
        Set(AlertIds.FuelLow, "?");
        Poll(5, 25);                                     // 20 s at 25 s: not longer yet
        Assert.Empty(_events);
        Poll(25, 30);                                    // crosses at 26, sent at 29
        Assert.Equal(T0.AddSeconds(29), Assert.Single(_events).Time);

        _options.SetThreshold(AlertIds.Unreadable, 0);   // never below 1 s
        Assert.Equal(1, _d.LimitSeconds);

        string path = Path.Combine(_tmp, "settings.json");
        var s = new AppSettings();
        s.Thresholds[AlertIds.Unreadable] = 25;
        s.Save(path);
        var spec = AlertRowCatalog.Get(AlertIds.Unreadable);
        Assert.Equal(25, AlertRowCatalog.Threshold(AppSettings.Load(path), spec));
        s.Thresholds[AlertIds.Unreadable] = 0;           // invalid: below the row's minimum -> default
        s.Save(path);
        Assert.Equal(10, AlertRowCatalog.Threshold(AppSettings.Load(path), spec));
        Assert.True(AppSettings.Load(path).IsAlertEnabled(AlertIds.Unreadable));   // default on
    }

    [Fact]
    public void Disabled_row_and_paused_watcher_stay_silent()
    {
        AllReadable();
        _options.SetEnabled(AlertIds.FuelLow, false);
        Set(AlertIds.FuelLow, "?");
        Poll(5, 40);
        Assert.Empty(_events);

        // Paused: the watcher stops polling and resets the rows (Interrupt). A crossing waiting in
        // the combine window is dropped too.
        _options.SetEnabled(AlertIds.FuelLow, true);
        Set(AlertIds.FuelLow, "~40%");
        Poll(40, 41);
        Set(AlertIds.FuelLow, "?");
        Poll(41, 53);                                    // crossed at 52, window until 55
        _d.Interrupt();
        Assert.Equal("—", _readings.Get(AlertIds.Unreadable).Text);
        Assert.Empty(_events);                           // (paused: no polls) nothing sent
        Poll(100, 110);                                  // resumed: the run starts again
        Assert.Empty(_events);
    }

    [Fact]
    public void Game_lost_sends_only_the_existing_game_dropped_alert()
    {
        var drop = new GameDropDetector(C.Drop);
        var sent = new List<WatchEvent>();
        AllReadable();
        drop.Update(true, T0);
        Set(AlertIds.GunAmmoEmpty, "?");
        Poll(5, 17);                                     // crossed at 16, window open
        // Game window gone: the watcher ends the session and resets the rows.
        for (int s = 17; s < 40; s++)
        {
            if (drop.Update(false, T0.AddSeconds(s)) is { } e) sent.Add(e);
            _d.EndSession();
        }
        Assert.Equal([WatchEventKind.GameDropped], sent.Select(e => e.Kind));
        Assert.Empty(_events);
        // Back: still "?" -> nothing until the row shows a value again.
        Poll(40, 80);
        Assert.Empty(_events);
    }

    // ------------------------------------------------------------------ journal, evidence

    static List<JsonElement> Records(string root) =>
        Directory.GetFiles(root, "*.jsonl").Order().SelectMany(File.ReadAllLines)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    [Fact]
    public void Journal_records_each_occurrence_and_recovery_and_evidence_is_the_full_frame()
    {
        string jroot = Path.Combine(_tmp, "journal"), eroot = Path.Combine(_tmp, "evidence");
        var hub = new DiagnosticsHub(C, new Journal(jroot, 1024 * 1024, s => s, () => new DateTimeOffset(T0)),
            new EvidenceStore(eroot, 50L * 1024 * 1024, 90), id => _readings.Get(id).Text, id => id == AlertIds.Unreadable ? 10 : null,
            () => throw new InvalidOperationException(), [AlertIds.Unreadable]) { SaveEvidence = true };
        hub.Evidence.Start();
        _d.Recovered += hub.OnUnreadableRecovered;

        AllReadable();
        Set(AlertIds.GunAmmoEmpty, "?");
        Set(AlertIds.FuelLow, "?");
        Poll(5, 20);
        var e = Assert.Single(_events);
        using var bmp = new Bitmap(1024, 768);
        var frame = Frame.FromBitmap(bmp, new Rectangle(0, 0, 1024, 768));
        string id = hub.OnAlert(e, frame, AlertResults.SendingOff, null);
        Set(AlertIds.FuelLow, "~40%");
        Poll(20, 21);
        Assert.True(hub.Evidence.WaitIdle(TimeSpan.FromSeconds(10)));
        hub.Dispose();

        var recs = Records(jroot);
        var alert = recs.Single(r => r.GetProperty("type").GetString() == "alert");
        Assert.Equal(AlertIds.Unreadable, alert.GetProperty("row").GetString());
        Assert.Equal("ปืนกล, น้ำมัน", alert.GetProperty("value").GetString());
        var files = alert.GetProperty("evidence").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(["2026-11-18/120019_000_unreadable_full.jpg"], files);           // no duplicate "crop"
        Assert.True(File.Exists(Path.Combine(eroot, files[0])));

        var u = recs.Where(r => r.GetProperty("type").GetString() == "unreadable").ToList();
        Assert.Equal(2, u.Count);
        Assert.Equal("occurred", u[0].GetProperty("event").GetString());
        Assert.Equal(id, u[0].GetProperty("alert_id").GetString());
        Assert.Equal([AlertIds.GunAmmoEmpty, AlertIds.FuelLow], u[0].GetProperty("rows").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal([14.0, 14.0], u[0].GetProperty("seconds").EnumerateArray().Select(x => x.GetDouble()));
        Assert.Equal(10, u[0].GetProperty("limit").GetInt32());
        Assert.Equal("recovered", u[1].GetProperty("event").GetString());
        Assert.Equal([AlertIds.FuelLow], u[1].GetProperty("rows").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal([15.0], u[1].GetProperty("seconds").EnumerateArray().Select(x => x.GetDouble()));
        Assert.Equal("~40%", u[1].GetProperty("value").GetString());
    }

    // ------------------------------------------------------------------ auto collection, rule g

    [Fact]
    public void Auto_collection_saves_the_full_frame_at_the_crossing_and_once_more_30_s_later_if_still_unreadable()
    {
        var regions = C.Collect.Regions.Select(r => r.Name).ToArray();
        var store = new CollectStore(Path.Combine(_tmp, "collect"), 200L * 1024 * 1024, queueLimit: 500);
        store.Start();
        var collector = new DataCollector(C.Collect, store, new AutoCollectRules(C, regions), null, 0,
            C.Diagnostics.Auto.UnreadableFullFrameRepeatSeconds);
        using var bmp = new Bitmap(1024, 768);
        var frame = Frame.FromBitmap(bmp, new Rectangle(0, 0, 1024, 768));

        void Run(int from, int to)
        {
            for (int s = from; s < to; s++)
            {
                var now = T0.AddSeconds(s);
                _d.Process(now, 1024, 768, _events);
                collector.OnFrame(frame, HudScale.One, now,
                    new AutoInputs(now, HudScale.One, true, id => _readings.Get(id).Text, _ => 5, _d));
            }
        }

        AllReadable();
        Set(AlertIds.GunAmmoEmpty, "?");                 // crosses at 16 s, still "?" at 46 s
        Run(5, 30);
        Set(AlertIds.FuelLow, "?");                      // crosses at 41 s, back to a value at 60 s
        Run(30, 60);
        Set(AlertIds.FuelLow, "~40%");
        Run(60, 100);
        collector.Dispose();

        var full = File.ReadAllLines(Path.Combine(_tmp, "collect", "2026-11-18", CollectStore.IndexFileName)).Skip(1)
            .Select(l => l.Split(',')).Where(r => r[1] == "full").ToList();
        Assert.All(full, r => Assert.Equal("event+unknown", r[7]));
        Assert.Equal([16, 41, 46], full.Select(r => (int)(DateTime.Parse(r[0]) - T0).TotalSeconds));
        Assert.EndsWith("_unreadable_later.png", full[2][2]);
        // The gun row's region is collected under rule g while it is over the limit.
        Assert.Contains(File.ReadAllLines(Path.Combine(_tmp, "collect", "2026-11-18", CollectStore.IndexFileName)),
            l => l.Contains(",ammo,") && l.EndsWith("+unknown"));
    }
}
