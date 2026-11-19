using System.Buffers;
using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Diagnostics;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Collection;

/// <summary>
/// "Collect data" mode, polling side. Each poll it computes the HUD colour-key signature of every
/// region and compares it with the signature of that region's last SAVED crop
/// (<see cref="HudSignature"/>). When a
/// save is due (<see cref="SaveSchedule"/>), the crop is copied into a pooled buffer and handed to
/// the <see cref="CollectStore"/> writer thread; nothing is encoded or written on the poll.
/// Exists only while collection is on.
///
/// Mode "อัตโนมัติ" (<see cref="AutoCollectRules"/> given): a region is only looked at while a
/// rule is active for it (same change test, schedule and cap); when it becomes active, the ring's
/// crops of that region from PreSeconds before are saved first ("pre"), and no periodic full frames
/// are saved. The rule name goes into index.csv.
/// </summary>
public sealed class DataCollector : IDisposable
{
    sealed class RegionState(CollectRegion region, SaveSchedule schedule)
    {
        public readonly CollectRegion Region = region;
        public readonly SaveSchedule Schedule = schedule;
        public HudSignature Current = new(), Saved = new();
        public int Saves;
    }

    readonly CollectConfig _config;
    readonly CollectStore _store;
    readonly RegionState[] _regions;
    readonly SaveSchedule _fullFrame;
    readonly AutoCollectRules? _auto;
    readonly HudRing? _ring;
    readonly TimeSpan _pre;
    readonly int[] _autoIndex;
    readonly TimeSpan _unreadableRepeat;
    readonly List<(DateTime Due, string[] Rows)> _unreadableRepeats = [];
    volatile bool _disposed;
    long _pollTicks;
    int _polls;

    public DataCollector(CollectConfig config, CollectStore store, AutoCollectRules? auto = null, HudRing? ring = null, double preSeconds = 0,
        double unreadableRepeatSeconds = 30)
    {
        _unreadableRepeat = TimeSpan.FromSeconds(unreadableRepeatSeconds);
        _auto = auto;
        _ring = ring;
        _pre = TimeSpan.FromSeconds(preSeconds);
        _config = config;
        _store = store;
        var gap = TimeSpan.FromSeconds(config.MinSecondsBetweenSaves);
        var beat = TimeSpan.FromSeconds(config.HeartbeatSeconds);
        _regions = config.AllRegions.Select(r => new RegionState(new CollectRegion(r), new SaveSchedule(gap, beat))).ToArray();
        var period = TimeSpan.FromMinutes(config.FullFrameMinutes);
        _fullFrame = new SaveSchedule(period, period);
        _autoIndex = _regions.Select(r => auto?.IndexOf(r.Region.Name) ?? -1).ToArray();
    }

    public bool IsAuto => _auto is not null;

    /// <summary>Auto mode: a region started (rule) or stopped (null) being collected. Polling thread.</summary>
    public event Action<string, string?>? AutoChanged;

    public CollectStore Store => _store;

    /// <summary>Saves queued per region since this collector started (full frames under "full").</summary>
    public IReadOnlyDictionary<string, int> SavesPerRegion
    {
        get
        {
            var d = _regions.ToDictionary(r => r.Region.Name, r => r.Saves);
            d["full"] = _fullSaves;
            return d;
        }
    }
    int _fullSaves;

    /// <summary>Mean time spent in <see cref="OnFrame"/> (polling thread), in milliseconds.</summary>
    public double MeanPollMilliseconds => _polls == 0 ? 0 : TimeSpan.FromTicks(_pollTicks).TotalMilliseconds / _polls;

    /// <summary>Called on the polling thread with every good frame.</summary>
    public void OnFrame(Frame frame, HudScale scale, DateTime now) =>
        OnFrame(frame, scale, now, new AutoInputs(now, scale, false, _ => RowReading.None, _ => 0));

    /// <summary>Called on the polling thread with every good frame (after the HUD readers ran).</summary>
    public void OnFrame(Frame frame, HudScale scale, DateTime now, in AutoInputs inputs)
    {
        if (_disposed) return;
        long t0 = DateTime.UtcNow.Ticks;
        _auto?.Evaluate(inputs);
        if (_auto is not null && inputs.Unreadable is { } unreadable) UnreadableFrames(frame, scale, now, unreadable);
        for (int i = 0; i < _regions.Length; i++)
        {
            var s = _regions[i];
            string? rule = null;
            if (_auto is not null)
            {
                int a = _autoIndex[i];
                if (a < 0) continue;
                if (_auto.Stopped(a)) AutoChanged?.Invoke(s.Region.Name, null);
                if (!_auto.IsActive(a)) continue;
                rule = _auto.RuleOf(a);
                if (_auto.Started(a))
                {
                    AutoChanged?.Invoke(s.Region.Name, rule);
                    SavePre(s.Region.Name, now, rule);
                }
            }
            var rect = s.Region.Locate(frame.Width, frame.Height, scale);
            if (rect.IsEmpty) continue;
            s.Current.Compute(frame, rect, s.Region.Keys);
            bool changed = HudSignature.Changed(s.Current, s.Saved, _config.Change, scale, s.Region.MinChangedPixels);
            if (s.Schedule.Decide(changed, now) is { } reason && Queue(frame, rect, now, s.Region.Name, reason, "", scale, rule))
            {
                s.Schedule.MarkSaved(now);
                s.Saves++;
                (s.Saved, s.Current) = (s.Current, s.Saved);
            }
        }
        if (_auto is null && _fullFrame.Decide(changed: false, now) is not null)
        {
            if (QueueFull(frame, now, SaveReason.Periodic, "", scale)) _fullFrame.MarkSaved(now);
        }
        _pollTicks += DateTime.UtcNow.Ticks - t0;
        _polls++;
    }

    /// <summary>
    /// Called on the polling thread when a detector fires. <paramref name="frame"/> is the frame
    /// the event was seen on (death) or the last frame captured before the window went away
    /// (game dropped); null when there is none.
    /// </summary>
    public void OnEvent(WatchEvent e, Frame? frame, HudScale scale, bool frameIsLastSeen)
    {
        if (_disposed) return;
        _auto?.NoteAlert(e);
        if (frame is null || frame.Width == 0 || e.Kind == WatchEventKind.Hud) return;
        string suffix = "_" + (e.Kind == WatchEventKind.Death ? "death" : "dropped") + (frameIsLastSeen ? "_lastseen" : "");
        QueueFull(frame, e.Time, SaveReason.Event, suffix, scale);
    }

    /// <summary>
    /// Auto mode, rule g: when rows cross the "อ่านค่าไม่ได้" limit, the full client frame once then
    /// and once more UnreadableFullFrameRepeatSeconds later if one of them is still unreadable (to
    /// learn what covers the HUD). Allocates only when a row crosses.
    /// </summary>
    void UnreadableFrames(Frame frame, HudScale scale, DateTime now, UnreadableDetector u)
    {
        if (u.CrossedThisPoll.Count > 0)
        {
            QueueFull(frame, now, SaveReason.Event, "_unreadable", scale, AutoCollectRules.Unknown);
            _unreadableRepeats.Add((now + _unreadableRepeat, u.CrossedThisPoll.ToArray()));
        }
        for (int i = _unreadableRepeats.Count - 1; i >= 0; i--)
        {
            var (due, rows) = _unreadableRepeats[i];
            if (now < due) continue;
            _unreadableRepeats.RemoveAt(i);
            foreach (var id in rows)
                if (u.IsOverLimit(id))
                {
                    QueueFull(frame, now, SaveReason.Event, "_unreadable_later", scale, AutoCollectRules.Unknown);
                    break;
                }
        }
    }

    bool QueueFull(Frame frame, DateTime now, SaveReason reason, string suffix, HudScale scale, string? rule = null)
    {
        bool ok = Queue(frame, new Rectangle(0, 0, frame.Width, frame.Height), now, "full", reason, suffix, scale, rule);
        if (ok) _fullSaves++;
        return ok;
    }

    bool Queue(Frame frame, Rectangle rect, DateTime now, string region, SaveReason reason, string suffix, HudScale scale, string? rule = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(rect.Width * rect.Height * 4);
        frame.CopyRect(rect, buffer);
        return _store.TryEnqueue(new SaveJob(buffer, rect.Width, rect.Height, now, region, reason, suffix, frame.Width, frame.Height, scale, rule ?? EventRule));
    }

    /// <summary>Full frames on death / game dropped: in auto mode they count as rule "alert".</summary>
    string? EventRule => _auto is null ? null : AutoCollectRules.Alert;

    /// <summary>The ring's crops of one region from PreSeconds before <paramref name="now"/>,
    /// at most one per MinSecondsBetweenSaves (the existing rate limit).</summary>
    void SavePre(string region, DateTime now, string? rule)
    {
        if (_ring is null || _pre <= TimeSpan.Zero) return;
        var crops = _ring.Snapshot(now - _pre, now, region);
        var gap = TimeSpan.FromSeconds(_config.MinSecondsBetweenSaves);
        DateTime? last = null;
        foreach (var c in crops)
        {
            if (last is { } l && c.Time - l < gap) { ArrayPool<byte>.Shared.Return(c.Pixels); continue; }
            if (_store.TryEnqueue(new SaveJob(c.Pixels, c.Width, c.Height, c.Time, c.Region, SaveReason.Pre, "", c.ClientWidth, c.ClientHeight, c.Scale, rule)))
                last = c.Time;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
    }
}
