using System.Buffers;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using ScreenWatcher.Alerts;
using ScreenWatcher.Bot;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Diagnostics;

/// <summary>Result recorded with an alert when it fires (sent / failed follow in an "alert_result" record).</summary>
public static class AlertResults
{
    public const string Pending = "pending", Sent = "sent", Failed = "failed", RowDisabled = "row_disabled",
        SendingOff = "sending_off", Paused = "paused", QueueFull = "queue_full";
}

/// <summary>
/// Field-trial diagnostics: turns what the app sees and does into journal records, alert evidence
/// and /miss bundles. Never writes the bot token (the journal scrubs every line) and never writes
/// Discord message contents (alert texts are not journalled; only the /miss note is). Every method
/// is safe to call from any thread and never throws.
/// </summary>
public sealed class DiagnosticsHub : IDisposable
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly WatcherConfig _config;
    readonly Func<string, string> _reading;
    readonly Func<string, int?> _threshold;
    readonly Func<MissCapture> _captureMiss;
    readonly IReadOnlyList<string> _allRows;
    readonly RowStats _stats;
    readonly string[] _statRows;
    readonly ConditionalWeakTable<AlertMessage, string> _alertIds = new();
    readonly object _statusGate = new();
    GameStatus? _lastStatus;
    bool _scaleEverMeasured;
    int _alertSeq;

    /// <param name="reading">"ตอนนี้" text of any alert row id (HUD rows, death, game dropped).</param>
    /// <param name="threshold">The alert threshold of a row (null when it has none).</param>
    /// <param name="captureMiss">Takes the /miss capture (full frame + ring crops).</param>
    /// <param name="allRows">Every alert row id, in display order.</param>
    public DiagnosticsHub(WatcherConfig config, Journal journal, EvidenceStore evidence,
        Func<string, string> reading, Func<string, int?> threshold, Func<MissCapture> captureMiss, IReadOnlyList<string> allRows)
    {
        _config = config;
        Journal = journal;
        Evidence = evidence;
        _reading = reading;
        _threshold = threshold;
        _captureMiss = captureMiss;
        _allRows = allRows;
        _statRows = HudRowIds(config).ToArray();
        _stats = new RowStats(_statRows);
    }

    public Journal Journal { get; }
    public EvidenceStore Evidence { get; }

    /// <summary>Setting "เก็บภาพทุกครั้งที่เตือน" (default off).</summary>
    public volatile bool SaveEvidence;

    /// <summary>HUD row ids with a reader (the rows that get hourly statistics).</summary>
    public static IEnumerable<string> HudRowIds(WatcherConfig c) =>
        new[] { AlertIds.GunAmmoEmpty, AlertIds.MissileEmpty, AlertIds.FuelLow }
            .Concat(c.Items.Items.Select(i => i.Id))
            .Append(AlertIds.Kill300);

    /// <summary>Identifies this exact build: version, build-unique module id and the exe's write time.</summary>
    public static string BuildId
    {
        get
        {
            var asm = typeof(DiagnosticsHub).Assembly;
            string version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            string mvid = asm.ManifestModule.ModuleVersionId.ToString("N")[..12];
            string stamp = "?";
            try { stamp = File.GetLastWriteTime(Path.Combine(AppContext.BaseDirectory, "ScreenWatcher.dll")).ToString("yyyyMMdd-HHmmss", Inv); }
            catch (Exception) { }
            return $"{version}+{mvid}@{stamp}";
        }
    }

    // ------------------------------------------------------------------ app, game, discord, settings

    public void AppStarted(bool noSend, string collectMode, bool saveEvidence) =>
        Journal.Write("app_start", ("build", BuildId), ("no_send", noSend), ("collect_mode", collectMode), ("evidence", saveEvidence),
            ("os", Environment.OSVersion.VersionString));

    public void AppStopped()
    {
        lock (_stats) WriteHours(_stats.Flush(), partial: true);
        Journal.Write("app_stop", ("build", BuildId));
    }

    /// <summary>Game found / lost, client size, HUD scale measured / changed / ring not found.</summary>
    public void OnStatus(GameStatus s)
    {
        lock (_statusGate)
        {
            var prev = _lastStatus;
            _lastStatus = s;
            bool wasFound = prev?.State == GameState.Found;
            bool found = s.State == GameState.Found;
            bool sizeChanged = found && wasFound && (prev!.Width != s.Width || prev.Height != s.Height);

            if (prev?.State != s.State)
                Journal.Write("game", ("event", found ? "found" : wasFound ? "lost" : "state"), ("state", s.State.ToString()),
                    ("width", s.Width), ("height", s.Height), ("detail", s.Detail));
            else if (sizeChanged)
                Journal.Write("game", ("event", "resized"), ("state", s.State.ToString()), ("width", s.Width), ("height", s.Height));

            if (!found) return;
            bool prevMeasured = wasFound && prev!.ScaleMeasured;
            if (s.ScaleMeasured && (!prevMeasured || prev!.Scale != s.Scale))
            {
                Journal.Write("hud_scale", ("event", _scaleEverMeasured ? "changed" : "measured"), ("x", s.Scale.X), ("y", s.Scale.Y),
                    ("width", s.Width), ("height", s.Height));
                _scaleEverMeasured = true;
            }
            else if (!s.ScaleMeasured && (!wasFound || prevMeasured || sizeChanged))
                Journal.Write("hud_scale", ("event", "ring_not_found"), ("using_x", s.Scale.X), ("using_y", s.Scale.Y),
                    ("width", s.Width), ("height", s.Height));
        }
    }

    public void OnDiscordStatus(string state, string? reason) => Journal.Write("discord", ("state", state), ("reason", reason));

    public void OnDiscordProblem(string text) => Journal.Write("discord_error", ("text", Shorten(text, 300)));

    public void OnSetting(string name, object? value) => Journal.Write("setting", ("name", name), ("value", value));

    public void OnCommand(string name, string outcome) => Journal.Write("command", ("name", name), ("outcome", outcome));

    public void OnAutoCollect(string region, string? rule) =>
        Journal.Write("auto_collect", ("region", region), ("on", rule is not null), ("rule", rule));

    public void OnRearmed(string key)
    {
        int colon = key.IndexOf(':');
        string row = colon < 0 ? key : key[..colon];
        Journal.Write("rearm", ("row", row), ("kind", colon < 0 ? null : key[(colon + 1)..]), ("value", _reading(row)));
    }

    // ------------------------------------------------------------------ hourly row statistics

    /// <summary>Polling thread, after a poll that processed a frame.</summary>
    public void OnPolled(DateTime now)
    {
        try
        {
            lock (_stats)
            {
                if (_stats.BeginPoll(now) is { } done) WriteHours(done, partial: false);
                for (int i = 0; i < _statRows.Length; i++) _stats.Add(i, _reading(_statRows[i]));
            }
        }
        catch (Exception) { }
    }

    void WriteHours(List<RowHour> hours, bool partial)
    {
        foreach (var h in hours)
            Journal.Write("row_hour", ("hour", h.Hour.ToString("yyyy-MM-ddTHH:00", Inv)), ("row", h.Row), ("polls", h.Polls),
                ("reads", h.Reads), ("unknown", h.Unknown), ("min", h.Min), ("max", h.Max), ("partial", partial));
    }

    // ------------------------------------------------------------------ alerts

    /// <summary>
    /// Polling thread, when a detector fires (the frame is reused by the next poll: copied here).
    /// Writes the "alert" record (row, value, threshold, result) and, when <see cref="SaveEvidence"/>
    /// is on and there is a frame, the evidence images it links to. Returns the alert's journal id.
    /// </summary>
    public string OnAlert(WatchEvent e, Frame? frame, string result, AlertMessage? queued)
    {
        string id = "a" + Interlocked.Increment(ref _alertSeq).ToString(Inv);
        try
        {
            string row = AlertIds.For(e);
            string value = _reading(row);
            string? kind = e.Kind == WatchEventKind.Hud && _config.Items.Items.Any(i => i.Id == row)
                ? value == ItemDetector.GoneText ? "gone" : "low" : null;
            IReadOnlyList<string> files = SaveEvidence && frame is { Width: > 0 } ? SaveAlertEvidence(e, frame, row) : [];
            if (queued is not null) _alertIds.AddOrUpdate(queued, id);
            Journal.Write("alert", ("id", id), ("row", row), ("kind", kind), ("value", value), ("threshold", _threshold(row)),
                ("crop_value", e.Image?.Value), ("result", result), ("evidence", files));
            if (e.Unreadable is { } rows)
                Journal.Write("unreadable", ("event", "occurred"), ("alert_id", id), ("rows", rows.Select(r => r.Id).ToArray()),
                    ("seconds", rows.Select(r => r.Seconds).ToArray()), ("limit", _threshold(row)), ("result", result));
        }
        catch (Exception) { }
        return id;
    }

    /// <summary>Polling thread: a row reported by the "อ่านค่าไม่ได้" alert shows a value again.</summary>
    public void OnUnreadableRecovered(UnreadableRow r) =>
        Journal.Write("unreadable", ("event", "recovered"), ("rows", new[] { r.Id }), ("seconds", new[] { r.Seconds }), ("value", SafeReading(r.Id)));

    /// <summary>Worker thread: an alert was sent (true) or given up on (false).</summary>
    public void OnAlertCompleted(AlertMessage m, bool ok)
    {
        if (!_alertIds.TryGetValue(m, out var id)) return;
        Journal.Write("alert_result", ("id", id), ("row", m.AlertId), ("result", ok ? AlertResults.Sent : AlertResults.Failed));
    }

    IReadOnlyList<string> SaveAlertEvidence(WatchEvent e, Frame frame, string row)
    {
        string stem = $"{e.Time.ToString("HHmmss_fff", Inv)}_{row}";
        var images = new List<EvidenceImage> { FullImage(frame, stem + "_full.jpg") };
        var crop = Rectangle.Intersect(e.Crop, new Rectangle(0, 0, frame.Width, frame.Height));
        if (e.Kind == WatchEventKind.Hud && e.Image is { Width: > 0, Height: > 0 } kept)
        {
            // The crop that was sent (e.g. the kill counter's last readable panel), not the region now.
            int n = kept.Width * kept.Height * 4;
            var px = ArrayPool<byte>.Shared.Rent(n);
            Array.Copy(kept.Pixels, px, n);
            images.Add(new EvidenceImage(stem + "_crop.png", px, kept.Width, kept.Height, Jpeg: false));
        }
        else if (e.Kind == WatchEventKind.Hud && crop.Width > 0 && crop.Height > 0 && crop != new Rectangle(0, 0, frame.Width, frame.Height))
        {
            var px = ArrayPool<byte>.Shared.Rent(crop.Width * crop.Height * 4);
            frame.CopyRect(crop, px);
            images.Add(new EvidenceImage(stem + "_crop.png", px, crop.Width, crop.Height, Jpeg: false));
        }
        return Evidence.TryEnqueue(new EvidenceJob(e.Time, images));
    }

    static EvidenceImage FullImage(Frame frame, string name)
    {
        var px = ArrayPool<byte>.Shared.Rent(frame.Width * frame.Height * 4);
        frame.CopyRect(new Rectangle(0, 0, frame.Width, frame.Height), px);
        return new EvidenceImage(name, px, frame.Width, frame.Height, Jpeg: true);
    }

    // ------------------------------------------------------------------ /miss

    /// <summary>
    /// /miss: saves the ring's HUD crops and the current full frame (when the game can be seen) to
    /// the evidence folder, whatever the evidence setting, and writes a "miss" record with the note
    /// and every row's current reading.
    /// </summary>
    public MissResult RecordMiss(string? note)
    {
        MissCapture cap;
        try { cap = _captureMiss(); }
        catch (Exception) { cap = new MissCapture(GameState.Error, DateTime.Now, null, 0, 0, HudScale.One, []); }

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in _allRows) rows[r] = SafeReading(r);

        IReadOnlyList<string> files = [];
        if (cap.FramePixels is { } px)
        {
            string stem = cap.Time.ToString("HHmmss_fff", Inv) + "_miss";
            var images = new List<EvidenceImage> { new(stem + "_full.jpg", px, cap.Width, cap.Height, Jpeg: true) };
            foreach (var c in cap.Ring)
                images.Add(new EvidenceImage($"{stem}_{c.Region}_{c.Time.ToString("HHmmss_fff", Inv)}.png", c.Pixels, c.Width, c.Height, Jpeg: false));
            files = Evidence.TryEnqueue(new EvidenceJob(cap.Time, images));
        }
        else HudRing.Return(cap.Ring);

        string? kept = note is null ? null : Shorten(note.Replace('\r', ' ').Replace('\n', ' '), Math.Max(0, _config.Diagnostics.MissNoteMaxChars));
        Journal.Write("miss", ("note", kept), ("game", cap.State.ToString()), ("width", cap.Width), ("height", cap.Height),
            ("scale_x", cap.Scale.X), ("scale_y", cap.Scale.Y), ("rows", rows), ("files", files));
        return new MissResult(files.Count > 0, Math.Max(0, files.Count - 1));
    }

    string SafeReading(string row)
    {
        try { return _reading(row); } catch (Exception) { return "?"; }
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => Evidence.Dispose();
}
