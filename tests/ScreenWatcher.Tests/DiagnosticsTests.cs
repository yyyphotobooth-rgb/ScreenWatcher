using System.Buffers;
using System.Drawing;
using System.Text.Json;
using ScreenWatcher.Alerts;
using ScreenWatcher.Bot;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Diagnostics;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Tests;

/// <summary>Field-trial diagnostics: journal, hourly row statistics, alert evidence, /miss, the
/// HUD ring buffer and the collection-mode settings.</summary>
public sealed class DiagnosticsTests : IDisposable
{
    const string Token = "MTIzNDU2Nzg5MDEyMzQ1Njc4" + "." + "GabcDE" + "." + "secret-token-value-xyz";
    static readonly DateTime T0 = new(2026, 11, 18, 12, 0, 0);
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "sw-diag-" + Guid.NewGuid().ToString("N"));

    public DiagnosticsTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    static Frame Noise(int w, int h, int seed = 1)
    {
        using var bmp = new Bitmap(w, h);
        var rnd = new Random(seed);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(30, 60, 90));
            for (int i = 0; i < 200; i++)
                using (var b = new SolidBrush(Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256))))
                    g.FillRectangle(b, rnd.Next(w), rnd.Next(h), 40, 20);
        }
        return Frame.FromBitmap(bmp, new Rectangle(0, 0, w, h));
    }

    static List<JsonElement> Records(string journalRoot)
    {
        var list = new List<JsonElement>();
        if (!Directory.Exists(journalRoot)) return list;
        foreach (var f in Directory.GetFiles(journalRoot, "*.jsonl").Order())
            foreach (var line in File.ReadAllLines(f))
                list.Add(JsonDocument.Parse(line).RootElement.Clone());
        return list;
    }

    // ------------------------------------------------------------------ journal

    [Fact]
    public void Journal_writes_one_json_record_per_line_into_a_file_per_day()
    {
        var clock = new DateTimeOffset(T0, TimeSpan.FromHours(7));
        var j = new Journal(Path.Combine(_tmp, "journal"), 1024 * 1024, clock: () => clock);
        j.Write("alert", ("id", "a1"), ("row", "fuelLow"), ("value", "~10%"), ("threshold", 10), ("result", "sent"),
            ("evidence", new[] { "2026-11-18/x.jpg" }));
        clock = clock.AddDays(1);
        j.Write("note", ("text", "line1\nline2 \"quoted\""), ("rows", new Dictionary<string, string> { ["kill300"] = "291" }));

        var day1 = Path.Combine(_tmp, "journal", "2026-11-18.jsonl");
        var day2 = Path.Combine(_tmp, "journal", "2026-11-19.jsonl");
        Assert.Single(File.ReadAllLines(day1));
        Assert.Single(File.ReadAllLines(day2));
        var a = JsonDocument.Parse(File.ReadAllLines(day1)[0]).RootElement;
        Assert.Equal("2026-11-18T12:00:00.000+07:00", a.GetProperty("t").GetString());
        Assert.Equal("alert", a.GetProperty("type").GetString());
        Assert.Equal(10, a.GetProperty("threshold").GetInt32());
        Assert.Equal("2026-11-18/x.jpg", a.GetProperty("evidence")[0].GetString());
        var n = JsonDocument.Parse(File.ReadAllLines(day2)[0]).RootElement;
        Assert.Equal("line1\nline2 \"quoted\"", n.GetProperty("text").GetString()); // escaped: still one line
        Assert.Equal("291", n.GetProperty("rows").GetProperty("kill300").GetString());
        Assert.Equal(new FileInfo(day1).Length + new FileInfo(day2).Length, j.TotalBytes);
    }

    [Fact]
    public void Journal_cap_deletes_oldest_day_files_first_and_only_its_own()
    {
        string root = Path.Combine(_tmp, "journal");
        Directory.CreateDirectory(root);
        void Make(string name, int size) => File.WriteAllBytes(Path.Combine(root, name), new byte[size]);
        Make("2026-11-15.jsonl", 4000);
        Make("2026-11-16.jsonl", 4000);
        Make("2026-11-17.jsonl", 4000);
        Make("notes.txt", 9000);           // not the journal's: never deleted, not counted
        Make("2026-11-14.jsonl.bak", 9000); // same

        var clock = new DateTimeOffset(T0, TimeSpan.FromHours(7));
        var j = new Journal(root, capBytes: 9000, clock: () => clock);
        j.Write("app_start", ("build", "x"));

        Assert.False(File.Exists(Path.Combine(root, "2026-11-15.jsonl")));
        Assert.True(File.Exists(Path.Combine(root, "2026-11-16.jsonl"))); // 8 KB + today is under the cap again
        Assert.True(File.Exists(Path.Combine(root, "2026-11-17.jsonl")));
        Assert.True(File.Exists(Path.Combine(root, "2026-11-18.jsonl")));
        Assert.True(File.Exists(Path.Combine(root, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(root, "2026-11-14.jsonl.bak")));
        Assert.True(j.TotalBytes <= 9000);
        Assert.Equal(j.TotalBytes, Journal.MeasureFolder(root));
    }

    [Fact]
    public void Journal_never_deletes_todays_file_and_stops_for_the_day_when_it_alone_is_over_the_cap()
    {
        string root = Path.Combine(_tmp, "journal");
        var clock = new DateTimeOffset(T0, TimeSpan.Zero);
        var j = new Journal(root, capBytes: 300, clock: () => clock);
        for (int i = 0; i < 20; i++) j.Write("x", ("i", i));
        long size = new FileInfo(Path.Combine(root, "2026-11-18.jsonl")).Length;
        Assert.InRange(size, 300, 400); // the record that crossed the cap is kept, then writing stops
        clock = clock.AddDays(1);
        j.Write("x", ("i", 99));       // a new day writes again (yesterday's file is now the oldest and goes)
        Assert.True(File.Exists(Path.Combine(root, "2026-11-19.jsonl")));
        Assert.False(File.Exists(Path.Combine(root, "2026-11-18.jsonl")));
    }

    // ------------------------------------------------------------------ hourly row statistics

    [Fact]
    public void Row_statistics_count_polls_reads_unknowns_and_min_max_per_clock_hour()
    {
        var s = new RowStats(["fuelLow", "kill300", "ammoBox"]);
        string[][] polls =
        [
            ["~40%", "250", "12"],
            ["?", "251", "หมด"],
            [">90%", "—", "?"],
            ["~20%", "260", "7"],
        ];
        for (int i = 0; i < polls.Length; i++)
        {
            Assert.Null(s.BeginPoll(T0.AddMinutes(10 * i)));
            for (int r = 0; r < 3; r++) s.Add(r, polls[i][r]);
        }
        var done = s.BeginPoll(T0.AddHours(1));
        Assert.NotNull(done);
        var fuel = done!.Single(h => h.Row == "fuelLow");
        Assert.Equal(new RowHour(T0, "fuelLow", 4, 3, 1, 20, 90), fuel);
        Assert.Equal(new RowHour(T0, "kill300", 4, 3, 0, 250, 260), done.Single(h => h.Row == "kill300"));
        Assert.Equal(new RowHour(T0, "ammoBox", 4, 3, 1, 0, 12), done.Single(h => h.Row == "ammoBox"));

        // The new hour starts empty; a row with no polls is not reported.
        s.Add(1, "?");
        var next = s.Flush();
        Assert.Equal([new RowHour(T0.AddHours(1), "kill300", 1, 0, 1, null, null)], next);
    }

    [Fact]
    public void Hub_writes_hourly_row_records_from_the_live_readings()
    {
        var (hub, readings, root) = MakeHub(_ => new MissCapture(GameState.NotFound, T0, null, 0, 0, HudScale.One, []));
        readings["fuelLow"] = "~30%";
        hub.OnPolled(T0.AddMinutes(1));
        readings["fuelLow"] = "?";
        hub.OnPolled(T0.AddMinutes(2));
        hub.OnPolled(T0.AddHours(1).AddMinutes(1)); // next hour: the first hour is written
        var rows = Records(root).Where(r => r.GetProperty("type").GetString() == "row_hour").ToList();
        var fuel = rows.Single(r => r.GetProperty("row").GetString() == "fuelLow");
        Assert.Equal("2026-11-18T12:00", fuel.GetProperty("hour").GetString());
        Assert.Equal(2, fuel.GetProperty("polls").GetInt32());
        Assert.Equal(1, fuel.GetProperty("reads").GetInt32());
        Assert.Equal(1, fuel.GetProperty("unknown").GetInt32());
        Assert.Equal(30, fuel.GetProperty("min").GetInt32());
        Assert.Equal(DiagnosticsHub.HudRowIds(TestData.Config).Count(), rows.Count);
    }

    // ------------------------------------------------------------------ hub helpers

    (DiagnosticsHub Hub, Dictionary<string, string> Readings, string JournalRoot) MakeHub(Func<int, MissCapture> capture)
    {
        string jroot = Path.Combine(_tmp, "journal"), eroot = Path.Combine(_tmp, "evidence");
        var scrub = new Logging.AppLog(null);
        scrub.AddSecret(Token);
        var readings = AlertRowIds.ToDictionary(id => id, _ => "—");
        int calls = 0;
        var hub = new DiagnosticsHub(TestData.Config,
            new Journal(jroot, 20 * 1024 * 1024, scrub.Scrub, () => new DateTimeOffset(T0)),
            new EvidenceStore(eroot, 200L * 1024 * 1024, 90),
            id => readings.GetValueOrDefault(id, "—"),
            id => id == AlertIds.FuelLow ? 10 : null,
            () => capture(calls++),
            AlertRowIds);
        hub.Evidence.Start();
        return (hub, readings, jroot);
    }

    static readonly string[] AlertRowIds = [AlertIds.Death, AlertIds.GameDropped, .. DiagnosticsHub.HudRowIds(TestData.Config)];

    // ------------------------------------------------------------------ alert evidence

    [Fact]
    public void Alert_evidence_is_saved_once_per_alert_and_linked_from_the_journal()
    {
        var (hub, readings, root) = MakeHub(_ => throw new InvalidOperationException());
        hub.SaveEvidence = true;
        var frame = Noise(1600, 1024);
        readings[AlertIds.FuelLow] = "~10%";
        var crop = new Rectangle(0, 24, 192, 164);
        var fuel = new WatchEvent(WatchEventKind.Hud, "น้ำมันเหลือ 10%", T0, AlertIds.FuelLow, crop);
        var msg = new AlertMessage(AlertIds.FuelLow, "น้ำมันเหลือ 10%", null, null, T0);
        string id = hub.OnAlert(fuel, frame, AlertResults.Pending, msg);
        hub.OnAlertCompleted(msg, ok: true);
        hub.OnAlert(new WatchEvent(WatchEventKind.Death, "ตายแล้ว", T0.AddSeconds(5)), frame, AlertResults.RowDisabled, null);
        hub.OnAlert(new WatchEvent(WatchEventKind.GameDropped, "เกมส์หลุด", T0.AddSeconds(9)), null, AlertResults.SendingOff, null);
        Assert.True(hub.Evidence.WaitIdle(TimeSpan.FromSeconds(10)));

        var recs = Records(root);
        var alerts = recs.Where(r => r.GetProperty("type").GetString() == "alert").ToList();
        Assert.Equal(3, alerts.Count);
        var a = alerts[0];
        Assert.Equal(id, a.GetProperty("id").GetString());
        Assert.Equal("fuelLow", a.GetProperty("row").GetString());
        Assert.Equal("~10%", a.GetProperty("value").GetString());
        Assert.Equal(10, a.GetProperty("threshold").GetInt32());
        Assert.Equal("pending", a.GetProperty("result").GetString());
        var files = a.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(["2026-11-18/120000_000_fuelLow_full.jpg", "2026-11-18/120000_000_fuelLow_crop.png"], files);
        using (var full = new Bitmap(Path.Combine(_tmp, "evidence", files[0]))) Assert.Equal(new Size(1600, 1024), full.Size);
        using (var c = new Bitmap(Path.Combine(_tmp, "evidence", files[1]))) Assert.Equal(crop.Size, c.Size);

        var result = recs.Single(r => r.GetProperty("type").GetString() == "alert_result");
        Assert.Equal(id, result.GetProperty("id").GetString());
        Assert.Equal("sent", result.GetProperty("result").GetString());

        // Death: full frame only; game dropped: no frame, no files. Results are as given.
        Assert.Equal(["2026-11-18/120005_000_death_full.jpg"], alerts[1].GetProperty("evidence").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("row_disabled", alerts[1].GetProperty("result").GetString());
        Assert.Empty(alerts[2].GetProperty("evidence").EnumerateArray());
        Assert.Equal("sending_off", alerts[2].GetProperty("result").GetString());

        // Exactly the linked files exist: one set per alert.
        var onDisk = Directory.GetFiles(Path.Combine(_tmp, "evidence"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(_tmp, "evidence"), f).Replace('\\', '/')).Order().ToList();
        Assert.Equal(alerts.SelectMany(x => x.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()!)).Order().ToList(), onDisk);
        hub.Dispose();
    }

    [Fact]
    public void Alert_evidence_is_off_by_default()
    {
        var (hub, _, root) = MakeHub(_ => throw new InvalidOperationException());
        Assert.False(hub.SaveEvidence);
        hub.OnAlert(new WatchEvent(WatchEventKind.Death, "ตายแล้ว", T0), Noise(320, 200), AlertResults.Pending, null);
        hub.Dispose();
        Assert.False(Directory.Exists(Path.Combine(_tmp, "evidence")) && Directory.EnumerateFiles(Path.Combine(_tmp, "evidence"), "*", SearchOption.AllDirectories).Any());
        Assert.Empty(Records(root).Single(r => r.GetProperty("type").GetString() == "alert").GetProperty("evidence").EnumerateArray());
    }

    [Fact]
    public void Evidence_cap_deletes_oldest_images_first_and_only_its_own()
    {
        string root = Path.Combine(_tmp, "evidence");
        Directory.CreateDirectory(Path.Combine(root, "2026-11-17"));
        string old = Path.Combine(root, "2026-11-17", "old_full.jpg");
        File.WriteAllBytes(old, new byte[50_000]);
        File.SetLastWriteTime(old, T0.AddDays(-1));
        string foreign = Path.Combine(root, "2026-11-17", "readme.txt");
        File.WriteAllBytes(foreign, new byte[50_000]);
        var store = new EvidenceStore(root, capBytes: 60_000, jpegQuality: 90);
        var px = ArrayPool<byte>.Shared.Rent(64 * 64 * 4);
        new Random(2).NextBytes(px);
        var written = store.Write(new EvidenceJob(T0, [new EvidenceImage("new_crop.png", px, 64, 64, Jpeg: false)]));
        Assert.True(File.Exists(written[0]));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(foreign));
        Assert.Equal(EvidenceStore.MeasureFolder(root), store.TotalBytes);
    }

    // ------------------------------------------------------------------ /miss

    sealed class FakeInteraction(string name, ulong? channel, string? note) : ISlashInteraction
    {
        public string CommandName => name;
        public ulong? ChannelId => channel;
        public readonly List<string> Calls = [];
        public Task RespondEphemeralAsync(string text) { Calls.Add("ephemeral:" + text); return Task.CompletedTask; }
        public Task DeferAsync() { Calls.Add("defer"); return Task.CompletedTask; }
        public Task FollowupAsync(string text) { Calls.Add("followup:" + text); return Task.CompletedTask; }
        public Task FollowupWithFileAsync(byte[] png, string fileName, string text) { Calls.Add("file"); return Task.CompletedTask; }
        public string? GetString(string n) => n == MissCommandHandler.NoteOption ? note : null;
    }

    static MissCapture CaptureWithFrame(HudRing ring, Frame frame)
    {
        var px = ArrayPool<byte>.Shared.Rent(frame.Width * frame.Height * 4);
        frame.CopyRect(new Rectangle(0, 0, frame.Width, frame.Height), px);
        return new MissCapture(GameState.Found, T0.AddSeconds(10), px, frame.Width, frame.Height, HudScale.One,
            ring.Snapshot(T0, T0.AddSeconds(11)));
    }

    [Fact]
    public async Task Miss_with_a_game_frame_saves_the_ring_and_the_frame_and_journals_note_and_rows()
    {
        var ring = new HudRing(TestData.Config.Collect.Regions.Select(r => new CollectRegion(r)), 10);
        var frame = Noise(1600, 1024);
        for (int k = 0; k < 10; k++) ring.Push(frame, HudScale.One, T0.AddSeconds(k));
        var (hub, readings, root) = MakeHub(_ => CaptureWithFrame(ring, frame));
        readings[AlertIds.FuelLow] = "~8%";
        readings[AlertIds.Kill300] = "297";
        var outcomes = new List<string>();
        var h = new MissCommandHandler(hub.RecordMiss, () => 42UL, outcomes.Add);
        var i = new FakeInteraction("miss", 42, "fuel was 8% and nothing came");

        Assert.True(await h.HandleAsync(i));
        Assert.True(hub.Evidence.WaitIdle(TimeSpan.FromSeconds(20)));

        Assert.Equal(["defer", "followup:" + MissCommandHandler.RecordedText], i.Calls);
        Assert.Equal(["recorded"], outcomes);
        var miss = Records(root).Single(r => r.GetProperty("type").GetString() == "miss");
        Assert.Equal("fuel was 8% and nothing came", miss.GetProperty("note").GetString());
        Assert.Equal("Found", miss.GetProperty("game").GetString());
        Assert.Equal("~8%", miss.GetProperty("rows").GetProperty("fuelLow").GetString());
        Assert.Equal("297", miss.GetProperty("rows").GetProperty("kill300").GetString());
        Assert.Equal(AlertRowIds.Length, miss.GetProperty("rows").EnumerateObject().Count());
        var files = miss.GetProperty("files").EnumerateArray().Select(e => e.GetString()!).ToList();
        int regions = TestData.Config.Collect.Regions.Length;
        Assert.Equal(1 + 10 * regions, files.Count); // full frame + 10 s of every region
        Assert.Equal("2026-11-18/120010_000_miss_full.jpg", files[0]);
        foreach (var f in files) Assert.True(File.Exists(Path.Combine(_tmp, "evidence", f)), f);
        foreach (var r in TestData.Config.Collect.Regions)
            Assert.Equal(10, files.Count(f => f.Contains($"_miss_{r.Name}_")));
        hub.Dispose();
    }

    [Fact]
    public async Task Miss_without_the_game_writes_only_the_journal_record()
    {
        var (hub, readings, root) = MakeHub(_ => new MissCapture(GameState.NotFound, T0, null, 0, 0, HudScale.One, []));
        var h = new MissCommandHandler(hub.RecordMiss, () => 42UL);
        var i = new FakeInteraction("miss", 42, null);

        Assert.True(await h.HandleAsync(i));
        hub.Dispose();

        Assert.Equal(["defer", "followup:" + MissCommandHandler.RecordedNoGameText], i.Calls);
        var miss = Records(root).Single(r => r.GetProperty("type").GetString() == "miss");
        Assert.Equal(JsonValueKind.Null, miss.GetProperty("note").ValueKind);
        Assert.Equal("NotFound", miss.GetProperty("game").GetString());
        Assert.Empty(miss.GetProperty("files").EnumerateArray());
        Assert.False(Directory.Exists(Path.Combine(_tmp, "evidence")) && Directory.EnumerateFiles(Path.Combine(_tmp, "evidence"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Miss_outside_the_chosen_channel_records_nothing()
    {
        int calls = 0;
        var h = new MissCommandHandler(_ => { calls++; return new MissResult(false, 0); }, () => 42UL);
        var i = new FakeInteraction("miss", 7, "x");
        Assert.True(await h.HandleAsync(i));
        Assert.Equal(0, calls);
        Assert.Equal(["ephemeral:" + MissCommandHandler.WrongChannelText], i.Calls);
        Assert.False(await h.HandleAsync(new FakeInteraction("ss", 42, null)));
    }

    // ------------------------------------------------------------------ nothing sensitive

    [Fact]
    public void Journal_never_contains_the_token_or_alert_message_texts()
    {
        var (hub, readings, root) = MakeHub(_ => new MissCapture(GameState.NotFound, T0, null, 0, 0, HudScale.One, []));
        const string alertText = "ข้อความแจ้งเตือนที่ส่งเข้า Discord";
        var e = new WatchEvent(WatchEventKind.Hud, alertText, T0, AlertIds.FuelLow, new Rectangle(0, 0, 10, 10));
        var m = new AlertMessage(AlertIds.FuelLow, alertText, null, null, T0);
        hub.OnAlert(e, null, AlertResults.Pending, m);
        hub.OnAlertCompleted(m, ok: false);
        hub.OnDiscordProblem($"Discord Warning: Gateway: bad token {Token}");
        hub.OnDiscordStatus("Error", "รหัสบอทไม่ถูกต้อง");
        hub.RecordMiss($"my token is {Token}");
        hub.OnSetting("discord.channel", "1/2");
        hub.AppStarted(noSend: false, "auto", saveEvidence: false);
        hub.AppStopped();
        hub.Dispose();

        string all = string.Concat(Directory.GetFiles(root).Select(File.ReadAllText));
        Assert.DoesNotContain(Token, all);
        Assert.DoesNotContain(alertText, all);
        Assert.Contains("***", all);
        Assert.Contains("\"type\":\"miss\"", all); // the /miss note is the only user text kept
    }

    // ------------------------------------------------------------------ ring buffer

    [Fact]
    public void Ring_keeps_a_fixed_number_of_slots_per_region_and_does_not_grow()
    {
        var regions = TestData.Config.Collect.Regions.Select(r => new CollectRegion(r)).ToArray();
        var ring = new HudRing(regions, 10);
        var frame = Noise(1600, 1024);
        for (int i = 0; i < 10; i++) ring.Push(frame, HudScale.One, T0.AddSeconds(i));
        long bytes = ring.Bytes;
        long expected = regions.Sum(r => { var b = r.Locate(1600, 1024, HudScale.One); return 10L * b.Width * b.Height * 4; });
        Assert.Equal(expected, bytes);
        Assert.True(bytes < 8 * 1024 * 1024, $"ring at 1.0 should stay small, is {bytes / 1024} KB");
        for (int i = 10; i < 35; i++) ring.Push(frame, HudScale.One, T0.AddSeconds(i));
        Assert.Equal(bytes, ring.Bytes); // buffers reused

        var all = ring.Snapshot(T0, T0.AddSeconds(100));
        Assert.Equal(10 * regions.Length, all.Count);
        Assert.All(all, c => Assert.InRange(c.Time, T0.AddSeconds(25), T0.AddSeconds(34)));
        var gauge = ring.Snapshot(T0.AddSeconds(30), T0.AddSeconds(33), "gauge");
        Assert.Equal([30, 31, 32], gauge.Select(c => (int)(c.Time - T0).TotalSeconds));
        Assert.Equal(new Size(192, 164), new Size(gauge[0].Width, gauge[0].Height));
        HudRing.Return(all);
        HudRing.Return(gauge);

        // A larger HUD scale grows the buffers once; going back reuses them.
        ring.Push(frame, new HudScale(1.5, 1.5), T0.AddSeconds(40));
        long grown = ring.Bytes;
        ring.Push(frame, HudScale.One, T0.AddSeconds(41));
        Assert.Equal(grown, ring.Bytes);
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void Collect_mode_fresh_install_is_off_and_evidence_is_off()
    {
        var s = AppSettings.Load(Path.Combine(_tmp, "missing", "settings.json"));
        Assert.Equal(CollectMode.Off, s.Collect);
        Assert.False(s.SaveAlertEvidence);
    }

    [Theory]
    [InlineData("{ \"CollectData\": true }", CollectMode.Always)]
    [InlineData("{ \"CollectData\": false }", CollectMode.Off)]
    [InlineData("{ }", CollectMode.Off)]
    [InlineData("{ \"CollectData\": true, \"CollectMode\": \"auto\" }", CollectMode.Auto)]
    [InlineData("{ \"CollectMode\": \"nonsense\", \"CollectData\": true }", CollectMode.Always)]
    public void Old_collect_setting_migrates(string json, CollectMode expected)
    {
        string path = Path.Combine(_tmp, "settings.json");
        File.WriteAllText(path, json);
        Assert.Equal(expected, AppSettings.Load(path).Collect);
    }

    [Fact]
    public void Collect_mode_and_evidence_are_remembered()
    {
        string path = Path.Combine(_tmp, "settings.json");
        foreach (var mode in new[] { CollectMode.Auto, CollectMode.Always, CollectMode.Off })
        {
            var s = new AppSettings { Collect = mode, SaveAlertEvidence = mode == CollectMode.Auto };
            s.Save(path);
            var back = AppSettings.Load(path);
            Assert.Equal(mode, back.Collect);
            Assert.Equal(mode == CollectMode.Auto, back.SaveAlertEvidence);
            Assert.Equal(mode == CollectMode.Always, back.CollectData);
        }
    }
}
