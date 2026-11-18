using ScreenWatcher.Capture;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Imaging;

namespace ScreenWatcher;

/// <summary>
/// Polls about once per second on a thread-pool timer: find the game window, feed the drop
/// detector, capture the client area, run the frame detectors. Events are raised on the timer
/// thread; the UI marshals them.
/// </summary>
public sealed class Watcher : IDisposable
{
    readonly WatcherConfig _config;
    readonly GameWindowFinder _finder;
    readonly WgcCapture _capture = new();
    readonly Frame _frame = new();
    readonly GameDropDetector _drop;
    readonly HudScaleTracker _scale;
    readonly IReadOnlyList<IFrameDetector> _frameDetectors;
    System.Threading.Timer? _timer;
    volatile bool _paused;
    volatile DataCollector? _collector;
    bool _haveFrame, _frameIsCurrent;
    HudScale _lastScale = HudScale.One;
    int _busy;
    string _status = "";

    public event Action<string>? StatusChanged;
    public event Action<WatchEvent>? EventRaised;

    public Watcher(WatcherConfig config)
    {
        _config = config;
        _finder = new GameWindowFinder(config.Window);
        _drop = new GameDropDetector(config.Drop);
        _scale = new HudScaleTracker(config.HudScale);
        _frameDetectors = [new DeathDetector(config.Death)];
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value) foreach (var d in _frameDetectors) d.ResetTiming();
        }
    }

    /// <summary>"Collect data" mode: null = off (costs one null check per poll). Set from any
    /// thread; returns the previous collector, which the caller disposes.</summary>
    public DataCollector? SwapCollector(DataCollector? collector) => Interlocked.Exchange(ref _collector, collector);

    public DataCollector? CurrentCollector => _collector;

    public void Start() => _timer ??= new System.Threading.Timer(_ => Tick(), null, 0, _config.PollIntervalMs);

    void Tick()
    {
        if (_paused || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = DateTime.Now;
            _frameIsCurrent = false;
            IntPtr hwnd = _finder.Find();
            Raise(_drop.Update(hwnd != IntPtr.Zero, now));

            if (hwnd == IntPtr.Zero) { NoFrame("Game: not found"); return; }

            switch (_capture.Capture(hwnd, _frame))
            {
                case CaptureResult.Minimized: NoFrame("Game: found (minimised - cannot see it)"); return;
                case CaptureResult.Failed: NoFrame($"Game: found (capture failed: {_capture.LastError})"); return;
            }
            if (_frame.LooksBlank()) { _haveFrame = false; NoFrame("Game: found (capture is black)"); return; }

            _haveFrame = _frameIsCurrent = true;
            var scale = _lastScale = _scale.Update(_frame);
            SetStatus($"Game: found ({_frame.Width}x{_frame.Height}), {_scale.Describe()}");
            foreach (var d in _frameDetectors) Raise(d.Process(_frame, scale, now));
            _collector?.OnFrame(_frame, scale, now);
        }
        catch (Exception ex)
        {
            NoFrame("Error: " + ex.Message);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    void NoFrame(string status)
    {
        foreach (var d in _frameDetectors) d.ResetTiming();
        SetStatus(status);
    }

    void SetStatus(string s)
    {
        if (s == _status) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    void Raise(WatchEvent? e)
    {
        if (e is null) return;
        EventRaised?.Invoke(e);
        // Death: the frame it was seen on. Game dropped: the last frame captured before (if any).
        _collector?.OnEvent(e, _haveFrame ? _frame : null, _lastScale, frameIsLastSeen: !_frameIsCurrent);
    }

    public void Dispose()
    {
        using var done = new ManualResetEvent(false);
        if (_timer is not null && _timer.Dispose(done)) done.WaitOne(TimeSpan.FromSeconds(3));
        _timer = null;
        SwapCollector(null)?.Dispose();
        _capture.Dispose();
    }
}
