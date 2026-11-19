using System.Drawing;
using System.Text;
using ScreenWatcher.Alerts;
using ScreenWatcher.Bot;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Imaging;
using ScreenWatcher.Logging;

namespace ScreenWatcher.Tests;

/// <summary>Discord alerts: token storage, log scrubbing, alert composition, per-alert switches,
/// /ss and retries. No real Discord calls: the bot is replaced by fakes.</summary>
public sealed class DiscordAlertTests : IDisposable
{
    const string FakeToken = "MTIzNDU2Nzg5MDEyMzQ1Njc4" + "." + "GhIjKl" + "." + "fake_token_for_tests_only_0123456789";
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "sw-discord-tests-" + Guid.NewGuid().ToString("N"));

    public DiscordAlertTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { } }

    static AppLog NullLog() => new(null);

    // ------------------------------------------------------------- token

    [Fact]
    public void Token_round_trips_and_file_is_encrypted()
    {
        var store = new TokenStore(Path.Combine(_tmp, "sub", "discord-token.bin"));
        Assert.False(store.Exists);
        Assert.Null(store.Load());

        store.Save("  " + FakeToken + "  ");
        Assert.True(store.Exists);
        Assert.Equal(FakeToken, store.Load());

        byte[] raw = File.ReadAllBytes(store.FilePath);
        Assert.DoesNotContain(FakeToken, Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain(FakeToken, Encoding.Unicode.GetString(raw));
        Assert.DoesNotContain("fake_token_for_tests", Encoding.Latin1.GetString(raw));

        store.Clear();
        Assert.False(store.Exists);
    }

    [Fact]
    public void Token_from_a_corrupt_file_is_null_not_a_crash()
    {
        var path = Path.Combine(_tmp, "bad.bin");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        Assert.Null(new TokenStore(path).Load());
    }

    [Fact]
    public void Token_never_appears_in_the_log()
    {
        var path = Path.Combine(_tmp, "log.txt");
        var log = new AppLog(path);
        var lines = new List<string>();
        log.LineWritten += lines.Add;
        log.AddSecret(FakeToken);

        log.Write($"Discord Warning: Gateway: bad token {FakeToken} rejected");
        log.Write("Discord login failed", new InvalidOperationException("token was " + FakeToken));
        log.Write("plain line");

        string text = File.ReadAllText(path);
        Assert.DoesNotContain(FakeToken, text);
        Assert.Contains("***", text);
        Assert.Contains("plain line", text);
        Assert.All(lines, l => Assert.DoesNotContain(FakeToken, l));
        Assert.Equal(3, lines.Count);
    }

    // ------------------------------------------------------------- composition

    static Frame DeadFrame() => TestData.DesktopClientFrame("from-chat/dead_1024x768_detected.png", 9, 37, 1024, 768);
    static Frame DeadFrameScaled() => TestData.DesktopClientFrame("from-chat/dead_1920x1440_missed.png", 34, 31, 1920, 1421);

    [Fact]
    public void Death_alert_is_Thai_text_plus_the_full_client_frame()
    {
        var frame = DeadFrame();
        var composer = new AlertComposer(TestData.Config);
        var e = new WatchEvent(WatchEventKind.Death, TestData.Config.Death.Message, new DateTime(2026, 11, 18, 16, 48, 0));

        var m = composer.Compose(e, frame, HudScale.One);

        Assert.Equal("ยานถูกยิงตก!", m.Text);
        Assert.Equal(AlertIds.Death, m.AlertId);
        Assert.NotNull(m.Png);
        Assert.EndsWith(".png", m.FileName);

        // The whole client area of the triggering frame, pixel for pixel (no crops, no gap).
        using var img = new Bitmap(new MemoryStream(m.Png!));
        Assert.Equal(new Size(1024, 768), img.Size);
        AssertSamePixel(frame, 0, 0, img, 0, 0);
        AssertSamePixel(frame, 82, 104, img, 82, 104);              // gauge
        AssertSamePixel(frame, 512 - 169, 384 - 31, img, 512 - 169, 384 - 31); // death dialog body
        AssertSamePixel(frame, 700, 500, img, 700, 500);
        AssertSamePixel(frame, 1023, 767, img, 1023, 767);
    }

    [Fact]
    public void Death_alert_is_the_full_frame_at_any_client_size()
    {
        var frame = DeadFrameScaled();
        var composer = new AlertComposer(TestData.Config);
        var m = composer.Compose(new WatchEvent(WatchEventKind.Death, "ตายแล้ว", DateTime.Now), frame, new HudScale(1.315, 1.318));

        using var img = new Bitmap(new MemoryStream(m.Png!));
        Assert.Equal(new Size(1920, 1421), img.Size);
        AssertSamePixel(frame, 960, 710, img, 960, 710);
        AssertSamePixel(frame, 1919, 1420, img, 1919, 1420);
    }

    [Fact]
    public void Game_dropped_alert_is_text_only()
    {
        var composer = new AlertComposer(TestData.Config);
        var e = new WatchEvent(WatchEventKind.GameDropped, TestData.Config.Drop.Message, DateTime.Now);

        var withoutFrame = composer.Compose(e, null, HudScale.One);
        var evenWithFrame = composer.Compose(e, DeadFrame(), HudScale.One);

        Assert.Equal("ขาดการติดต่อกับยาน (เกมส์หลุด)", withoutFrame.Text);
        Assert.Equal(AlertIds.GameDropped, withoutFrame.AlertId);
        Assert.Null(withoutFrame.Png);
        Assert.Null(evenWithFrame.Png);
    }

    static void AssertSamePixel(Frame f, int fx, int fy, Bitmap img, int ix, int iy)
    {
        var expected = f.Pixel(fx, fy);
        var actual = img.GetPixel(ix, iy);
        Assert.Equal(((int)expected.R, (int)expected.G, (int)expected.B), ((int)actual.R, (int)actual.G, (int)actual.B));
    }

    // ------------------------------------------------------------- enable / disable

    sealed class FakeSink(int failures = 0) : IAlertSink
    {
        public readonly List<AlertMessage> Sent = [];
        public int Attempts;
        public readonly TaskCompletionSource Delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(AlertMessage message, CancellationToken ct)
        {
            Attempts++;
            if (Attempts <= failures) throw new HttpRequestException("network down");
            Sent.Add(message);
            Delivered.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Disabled_alert_is_not_sent_enabled_one_is()
    {
        var settings = new AppSettings();
        settings.SetAlertEnabled(AlertIds.Death, false);
        var sink = new FakeSink();
        using var svc = new AlertService(new AlertComposer(TestData.Config), sink, settings.IsAlertEnabled, NullLog(), []);

        var death = new WatchEvent(WatchEventKind.Death, "ตายแล้ว", DateTime.Now);
        var drop = new WatchEvent(WatchEventKind.GameDropped, "เกมส์หลุด", DateTime.Now);

        Assert.Null(svc.OnEvent(death, DeadFrame(), HudScale.One));
        Assert.NotNull(svc.OnEvent(drop, null, HudScale.One));
        await sink.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["เกมส์หลุด"], sink.Sent.Select(m => m.Text));

        // Switching death back on takes effect for the next event.
        settings.SetAlertEnabled(AlertIds.Death, true);
        Assert.NotNull(svc.OnEvent(death, DeadFrame(), HudScale.One));
    }

    [Fact]
    public void Alert_switches_are_persisted_and_default_to_on()
    {
        var path = Path.Combine(_tmp, "settings.json");
        var s = new AppSettings();
        Assert.True(s.IsAlertEnabled(AlertIds.Death));
        Assert.True(s.IsAlertEnabled(AlertIds.GameDropped));

        s.SetAlertEnabled(AlertIds.GameDropped, false);
        s.DiscordGuildId = 11;
        s.DiscordChannelId = 22;
        s.Save(path);

        var back = AppSettings.Load(path);
        Assert.True(back.IsAlertEnabled(AlertIds.Death));
        Assert.False(back.IsAlertEnabled(AlertIds.GameDropped));
        Assert.Equal(22UL, back.DiscordChannelId);
        Assert.DoesNotContain("token", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------- retries

    [Fact]
    public async Task Failed_send_is_retried_with_the_configured_delays_then_succeeds()
    {
        var sink = new FakeSink(failures: 2);
        var waited = new List<TimeSpan>();
        var lines = new List<string>();
        var log = NullLog();
        log.LineWritten += lines.Add;
        using var svc = new AlertService(new AlertComposer(TestData.Config), sink, _ => true, log,
            TestData.Config.Alerts.RetryDelays, delay: (d, _) => { waited.Add(d); return Task.CompletedTask; });

        bool ok = await svc.SendWithRetryAsync(new AlertMessage("death", "ตายแล้ว", null, null, DateTime.Now), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(3, sink.Attempts);
        Assert.Equal(TestData.Config.Alerts.RetryDelays.Take(2), waited);
        Assert.Contains(lines, l => l.Contains("sent on attempt 3"));
    }

    [Fact]
    public async Task Send_that_keeps_failing_gives_up_logs_and_does_not_throw()
    {
        var sink = new FakeSink(failures: int.MaxValue);
        var lines = new List<string>();
        var log = NullLog();
        log.LineWritten += lines.Add;
        var delays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var svc = new AlertService(new AlertComposer(TestData.Config), sink, _ => true, log, delays,
            delay: (_, _) => Task.CompletedTask);
        svc.Completed += (_, ok) => completed.TrySetResult(ok);

        // Through the real queue, as the watcher uses it.
        Assert.NotNull(svc.OnEvent(new WatchEvent(WatchEventKind.GameDropped, "เกมส์หลุด", DateTime.Now), null, HudScale.One));
        bool result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result);
        Assert.Equal(1 + delays.Length, sink.Attempts);
        Assert.Contains(lines, l => l.Contains("NOT sent after 3 attempts"));
    }

    [Fact]
    public void Composer_failure_never_reaches_the_watcher()
    {
        var sink = new FakeSink();
        using var svc = new AlertService(new AlertComposer(TestData.Config), sink,
            _ => throw new InvalidOperationException("boom"), NullLog(), []);
        Assert.Null(svc.OnEvent(new WatchEvent(WatchEventKind.Death, "ตายแล้ว", DateTime.Now), DeadFrame(), HudScale.One));
    }

    // ------------------------------------------------------------- /ss

    sealed class FakeInteraction(string name, ulong? channel) : ISlashInteraction
    {
        public string CommandName => name;
        public ulong? ChannelId => channel;
        public readonly List<string> Calls = [];
        public byte[]? File;

        public Task RespondEphemeralAsync(string text) { Calls.Add("ephemeral:" + text); return Task.CompletedTask; }
        public Task DeferAsync() { Calls.Add("defer"); return Task.CompletedTask; }
        public Task FollowupAsync(string text) { Calls.Add("followup:" + text); return Task.CompletedTask; }
        public Task FollowupWithFileAsync(byte[] png, string fileName, string text)
        {
            File = png;
            Calls.Add($"file:{fileName.EndsWith(".png")}:{text}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Ss_replies_with_the_current_frame()
    {
        byte[] png = PngEncoder.Encode(DeadFrame(), new Rectangle(0, 0, 1024, 768));
        int snapshots = 0;
        var h = new SsCommandHandler(() => { snapshots++; return new SnapshotResult(GameState.Found, png); }, () => 42UL);
        var i = new FakeInteraction("ss", 42);

        Assert.True(await h.HandleAsync(i));

        Assert.Equal(1, snapshots);
        Assert.Equal(["defer", "file:True:ภาพจอเกมตอนนี้"], i.Calls);
        using var img = new Bitmap(new MemoryStream(i.File!));
        Assert.Equal(new Size(1024, 768), img.Size);
    }

    [Fact]
    public async Task Ss_without_a_game_says_game_window_not_found()
    {
        var h = new SsCommandHandler(() => new SnapshotResult(GameState.NotFound, null), () => 42UL);
        var i = new FakeInteraction("ss", 42);

        await h.HandleAsync(i);

        Assert.Equal(["defer", "followup:ไม่พบสัญญาณจากยาน (ไม่พบหน้าต่างเกม)"], i.Calls);
    }

    [Fact]
    public async Task Ss_outside_the_chosen_channel_posts_nothing_and_takes_no_capture()
    {
        int snapshots = 0;
        var h = new SsCommandHandler(() => { snapshots++; return new SnapshotResult(GameState.Found, [1]); }, () => 42UL);

        var other = new FakeInteraction("ss", 7);
        await h.HandleAsync(other);
        var noChannelChosen = new FakeInteraction("ss", 42);
        await new SsCommandHandler(() => { snapshots++; return new SnapshotResult(GameState.Found, [1]); }, () => null).HandleAsync(noChannelChosen);

        Assert.Equal(0, snapshots);
        Assert.Equal(["ephemeral:" + SsCommandHandler.WrongChannelText], other.Calls);
        Assert.Equal(["ephemeral:" + SsCommandHandler.WrongChannelText], noChannelChosen.Calls);
    }

    [Fact]
    public async Task Other_commands_are_ignored()
    {
        var h = new SsCommandHandler(() => throw new InvalidOperationException(), () => 42UL);
        var i = new FakeInteraction("other", 42);
        Assert.False(await h.HandleAsync(i));
        Assert.Empty(i.Calls);
    }

    [Fact]
    public void Invite_link_asks_only_for_view_send_attach_with_bot_and_commands_scopes()
    {
        Assert.Equal(1024UL + 2048UL + 32768UL, DiscordBot.InvitePermissions);
        Assert.Equal("https://discord.com/oauth2/authorize?client_id=123&permissions=35840&scope=bot%20applications.commands",
            DiscordBot.InviteUrl(123));
    }
}
