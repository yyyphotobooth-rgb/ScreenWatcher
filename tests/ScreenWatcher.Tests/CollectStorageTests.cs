using System.Buffers;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;

namespace ScreenWatcher.Tests;

/// <summary>Collect mode: default OFF, save-rate limits and heartbeat, size cap.</summary>
public sealed class CollectStorageTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "ScreenWatcherTests_" + Guid.NewGuid().ToString("N"));

    public CollectStorageTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    // ---- settings ----

    [Fact]
    public void Collection_is_off_on_a_fresh_install()
    {
        var s = AppSettings.Load(Path.Combine(_tmp, "missing", "settings.json"));
        Assert.False(s.CollectData);
    }

    [Fact]
    public void Collection_choice_is_remembered_and_bad_file_means_off()
    {
        string path = Path.Combine(_tmp, "settings.json");
        new AppSettings { CollectData = true }.Save(path);
        Assert.True(AppSettings.Load(path).CollectData);
        File.WriteAllText(path, "{ not json");
        Assert.False(AppSettings.Load(path).CollectData);
    }

    // ---- save schedule ----

    static readonly DateTime T0 = new(2026, 11, 18, 12, 0, 0);

    static SaveSchedule Schedule() => new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));

    [Fact]
    public void First_poll_saves_a_heartbeat_baseline()
    {
        var s = Schedule();
        Assert.Equal(SaveReason.Heartbeat, s.Decide(changed: false, T0));
    }

    [Fact]
    public void At_most_one_save_per_two_seconds_even_if_changing_every_poll()
    {
        var s = Schedule();
        var saves = new List<double>();
        for (int i = 0; i <= 20; i++) // 1 poll per second, content changes every poll
        {
            var now = T0.AddSeconds(i);
            if (s.Decide(changed: true, now) is { } r) { s.MarkSaved(now); saves.Add(i); }
        }
        Assert.Equal(11, saves.Count);
        Assert.All(saves.Zip(saves.Skip(1)), p => Assert.True(p.Second - p.First >= 2));
    }

    [Fact]
    public void Change_inside_two_seconds_waits_then_saves_as_change()
    {
        var s = Schedule();
        s.MarkSaved(T0);
        Assert.Null(s.Decide(true, T0.AddSeconds(1)));
        Assert.Equal(SaveReason.Change, s.Decide(true, T0.AddSeconds(2)));
    }

    [Fact]
    public void Heartbeat_every_sixty_seconds_without_changes()
    {
        var s = Schedule();
        s.MarkSaved(T0);
        Assert.Null(s.Decide(false, T0.AddSeconds(59.9)));
        Assert.Equal(SaveReason.Heartbeat, s.Decide(false, T0.AddSeconds(60)));
        s.MarkSaved(T0.AddSeconds(60));
        Assert.Null(s.Decide(false, T0.AddSeconds(100)));
        Assert.Equal(SaveReason.Heartbeat, s.Decide(false, T0.AddSeconds(120)));
    }

    // ---- size cap ----

    string MakeFile(string relative, int bytes, DateTime written)
    {
        string path = Path.Combine(_tmp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTime(path, written);
        return path;
    }

    [Fact]
    public void Cap_deletes_oldest_pngs_only_inside_the_collect_folder()
    {
        string root = Path.Combine(_tmp, "collect");
        var oldest = MakeFile(@"collect\2026-11-16\100000_000_ammo_change.png", 1000, T0.AddDays(-2));
        var older = MakeFile(@"collect\2026-11-17\100000_000_ammo_change.png", 1000, T0.AddDays(-1));
        var newer = MakeFile(@"collect\2026-11-18\100000_000_ammo_change.png", 1000, T0);
        var newest = MakeFile(@"collect\2026-11-18\100001_000_kill_change.png", 1000, T0.AddSeconds(1));
        var oldIndex = MakeFile(@"collect\2026-11-16\index.csv", 50, T0.AddDays(-2));
        // Not this mode's files: must survive even though they are old.
        var outside = MakeFile(@"elsewhere\2026-11-01\000000_000_ammo_change.png", 5000, T0.AddYears(-1));
        var sibling = MakeFile(@"collect-old\2026-11-01\000000_000_ammo_change.png", 5000, T0.AddYears(-1));
        var foreignInRoot = MakeFile(@"collect\notes.png", 5000, T0.AddYears(-1));
        var foreignDir = MakeFile(@"collect\misc\000000_000_ammo_change.png", 5000, T0.AddYears(-1));
        var foreignType = MakeFile(@"collect\2026-11-16\readme.txt", 10, T0.AddYears(-1));

        var store = new CollectStore(root, capBytes: 2500);
        store.Initialize(); // scans and enforces the cap

        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(newest));
        Assert.False(File.Exists(oldIndex)); // that day has no PNG left
        Assert.True(File.Exists(foreignType)); // so its folder is kept too
        foreach (var f in new[] { outside, sibling, foreignInRoot, foreignDir }) Assert.True(File.Exists(f), f);
        Assert.Equal(2000, store.TotalBytes);
    }

    [Fact]
    public void Writes_go_to_dated_folder_with_index_and_trigger_the_cap()
    {
        string root = Path.Combine(_tmp, "collect");
        var store = new CollectStore(root, capBytes: 15000);
        var older = MakeFile(@"collect\2026-11-17\235959_000_gauge_heartbeat.png", 20000, T0.AddDays(-1));
        store.Initialize();

        var px = ArrayPool<byte>.Shared.Rent(64 * 32 * 4);
        new Random(1).NextBytes(px); // noise: PNG does not compress it, so the file is ~8 KB
        string path = store.Write(new SaveJob(px, 64, 32, T0, "ammo", SaveReason.Change, "", 1600, 1024, new HudScale(1.0, 0.99)));

        Assert.Equal(Path.Combine(root, "2026-11-18", "120000_000_ammo_change.png"), path);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(older)); // 20 KB + new file is over the 15 KB cap: the oldest goes first
        var lines = File.ReadAllLines(Path.Combine(root, "2026-11-18", CollectStore.IndexFileName));
        Assert.Equal("timestamp,region,file,client_width,client_height,scale_x,scale_y,reason", lines[0]);
        Assert.Equal("2026-11-18 12:00:00.000,ammo,120000_000_ammo_change.png,1600,1024,1.0000,0.9900,change", lines[1]);
    }
}
