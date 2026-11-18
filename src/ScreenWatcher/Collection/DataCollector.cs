using System.Buffers;
using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Collection;

/// <summary>
/// "Collect data" mode, polling side. Each poll it computes the HUD colour-key signature of every
/// region and compares it with the signature of that region's last SAVED crop
/// (<see cref="HudSignature"/>). When a
/// save is due (<see cref="SaveSchedule"/>), the crop is copied into a pooled buffer and handed to
/// the <see cref="CollectStore"/> writer thread; nothing is encoded or written on the poll.
/// Exists only while collection is on.
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
    volatile bool _disposed;
    long _pollTicks;
    int _polls;

    public DataCollector(CollectConfig config, CollectStore store)
    {
        _config = config;
        _store = store;
        var gap = TimeSpan.FromSeconds(config.MinSecondsBetweenSaves);
        var beat = TimeSpan.FromSeconds(config.HeartbeatSeconds);
        _regions = config.Regions.Select(r => new RegionState(new CollectRegion(r), new SaveSchedule(gap, beat))).ToArray();
        var period = TimeSpan.FromMinutes(config.FullFrameMinutes);
        _fullFrame = new SaveSchedule(period, period);
    }

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
    public void OnFrame(Frame frame, HudScale scale, DateTime now)
    {
        if (_disposed) return;
        long t0 = DateTime.UtcNow.Ticks;
        foreach (var s in _regions)
        {
            var rect = s.Region.Locate(frame.Width, frame.Height, scale);
            if (rect.IsEmpty) continue;
            s.Current.Compute(frame, rect, s.Region.Keys);
            bool changed = HudSignature.Changed(s.Current, s.Saved, _config.Change, scale, s.Region.MinChangedPixels);
            if (s.Schedule.Decide(changed, now) is { } reason && Queue(frame, rect, now, s.Region.Name, reason, "", scale))
            {
                s.Schedule.MarkSaved(now);
                s.Saves++;
                (s.Saved, s.Current) = (s.Current, s.Saved);
            }
        }
        if (_fullFrame.Decide(changed: false, now) is not null)
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
        if (_disposed || frame is null || frame.Width == 0 || e.Kind == WatchEventKind.Hud) return;
        string suffix = "_" + (e.Kind == WatchEventKind.Death ? "death" : "dropped") + (frameIsLastSeen ? "_lastseen" : "");
        QueueFull(frame, e.Time, SaveReason.Event, suffix, scale);
    }

    bool QueueFull(Frame frame, DateTime now, SaveReason reason, string suffix, HudScale scale)
    {
        bool ok = Queue(frame, new Rectangle(0, 0, frame.Width, frame.Height), now, "full", reason, suffix, scale);
        if (ok) _fullSaves++;
        return ok;
    }

    bool Queue(Frame frame, Rectangle rect, DateTime now, string region, SaveReason reason, string suffix, HudScale scale)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(rect.Width * rect.Height * 4);
        frame.CopyRect(rect, buffer);
        return _store.TryEnqueue(new SaveJob(buffer, rect.Width, rect.Height, now, region, reason, suffix, frame.Width, frame.Height, scale));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
    }
}
