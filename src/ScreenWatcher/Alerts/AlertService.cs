using System.Drawing;
using System.Threading.Channels;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Imaging;
using ScreenWatcher.Logging;

namespace ScreenWatcher.Alerts;

/// <summary>One alert ready to send: Thai text plus an optional PNG.</summary>
public sealed record AlertMessage(string AlertId, string Text, byte[]? Png, string? FileName, DateTime Time);

/// <summary>Where alerts go (the Discord bot; a fake in tests). Throws when it cannot send.</summary>
public interface IAlertSink
{
    Task SendAsync(AlertMessage message, CancellationToken ct);
}

/// <summary>
/// Turns a detector event into an <see cref="AlertMessage"/>. Death: "ตายแล้ว" + ONE image that
/// holds the gauge crop and the centre dialog crop side by side, cut from the frame the death was
/// seen on (regions from detectors.json collect.regions, named in alerts.deathCropRegions).
/// Game dropped: "เกมส์หลุด", text only (there is no window to crop).
/// </summary>
public sealed class AlertComposer
{
    readonly CollectRegion[] _deathRegions;

    public AlertComposer(WatcherConfig config)
    {
        _deathRegions = config.Alerts.DeathCropRegions
            .Select(name => config.Collect.Regions.FirstOrDefault(r => r.Name == name)
                ?? throw new InvalidDataException($"alerts.deathCropRegions: no collect region named '{name}'"))
            .Select(r => new CollectRegion(r))
            .ToArray();
    }

    /// <summary>Crop rectangles for a death alert at this client size and scale (empty ones dropped).</summary>
    public IReadOnlyList<Rectangle> DeathRects(int width, int height, HudScale scale) =>
        _deathRegions.Select(r => r.Locate(width, height, scale)).Where(r => !r.IsEmpty).ToList();

    /// <summary>Must run on the polling thread (the frame buffer is reused by the next poll).</summary>
    public AlertMessage Compose(WatchEvent e, Frame? frame, HudScale scale)
    {
        string id = AlertIds.For(e);
        if (e.Kind == WatchEventKind.Death && frame is { Width: > 0 })
        {
            var rects = DeathRects(frame.Width, frame.Height, scale);
            if (rects.Count > 0)
                return new AlertMessage(id, e.Message, PngEncoder.Compose(frame, rects), $"death_{e.Time:yyyyMMdd_HHmmss}.png", e.Time);
        }
        if (e.Kind == WatchEventKind.Hud && frame is { Width: > 0 })
        {
            // Only the HUD part concerned, from the frame the alert fired on.
            var crop = Rectangle.Intersect(e.Crop, new Rectangle(0, 0, frame.Width, frame.Height));
            if (crop.Width > 0 && crop.Height > 0)
                return new AlertMessage(id, e.Message, PngEncoder.Encode(frame, crop), $"{id}_{e.Time:yyyyMMdd_HHmmss}.png", e.Time);
        }
        return new AlertMessage(id, e.Message, null, null, e.Time);
    }
}

/// <summary>
/// Event → (enabled?) → compose → queue → send with retries. Sending runs on its own task; a
/// failure is retried after each delay in <c>alerts.retryDelaysSeconds</c>, then logged and
/// dropped. Nothing here ever throws into the watcher.
/// </summary>
public sealed class AlertService : IDisposable
{
    readonly AlertComposer _composer;
    readonly IAlertSink _sink;
    readonly Func<string, bool> _isEnabled;
    readonly AppLog _log;
    readonly IReadOnlyList<TimeSpan> _retryDelays;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly Channel<AlertMessage> _queue;
    readonly CancellationTokenSource _stop = new();
    readonly Task _worker;

    /// <summary>Raised after an alert was sent (true) or given up on (false). Worker thread.</summary>
    public event Action<AlertMessage, bool>? Completed;

    public AlertService(AlertComposer composer, IAlertSink sink, Func<string, bool> isEnabled, AppLog log,
        IReadOnlyList<TimeSpan> retryDelays, int queueLimit = 8, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _composer = composer;
        _sink = sink;
        _isEnabled = isEnabled;
        _log = log;
        _retryDelays = retryDelays;
        _delay = delay ?? Task.Delay;
        _queue = Channel.CreateBounded<AlertMessage>(new BoundedChannelOptions(queueLimit)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Called on the polling thread. Returns the queued message, or null when the alert
    /// is switched off or could not be queued.</summary>
    public AlertMessage? OnEvent(WatchEvent e, Frame? frame, HudScale scale)
    {
        try
        {
            if (!_isEnabled(AlertIds.For(e))) return null;
            var m = _composer.Compose(e, frame, scale);
            if (_queue.Writer.TryWrite(m)) return m;
            _log.Write($"Alert dropped (queue full): {e.Kind}");
        }
        catch (Exception ex) { _log.Write($"Alert not queued: {e.Kind}", ex); }
        return null;
    }

    /// <summary>Queues an already-built message (e.g. "Test send").</summary>
    public bool Enqueue(AlertMessage m) => _queue.Writer.TryWrite(m);

    async Task RunAsync()
    {
        try
        {
            await foreach (var m in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                bool ok = await SendWithRetryAsync(m, _stop.Token);
                try { Completed?.Invoke(m, ok); } catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>First try plus one retry per configured delay. True when sent.</summary>
    public async Task<bool> SendWithRetryAsync(AlertMessage m, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await _sink.SendAsync(m, ct);
                if (attempt > 0) _log.Write($"Alert {m.AlertId} sent on attempt {attempt + 1}");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (Exception ex)
            {
                if (attempt >= _retryDelays.Count)
                {
                    _log.Write($"Alert {m.AlertId} NOT sent after {attempt + 1} attempts", ex);
                    return false;
                }
                _log.Write($"Alert {m.AlertId} attempt {attempt + 1} failed, retrying in {_retryDelays[attempt].TotalSeconds:F0} s", ex);
                try { await _delay(_retryDelays[attempt], ct); }
                catch (OperationCanceledException) { return false; }
            }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _stop.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
