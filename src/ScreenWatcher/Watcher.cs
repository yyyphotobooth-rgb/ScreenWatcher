using ScreenWatcher.Capture;
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
    readonly PrintWindowCapture _capture = new();
    readonly Frame _frame = new();
    readonly GameDropDetector _drop;
    readonly IReadOnlyList<IFrameDetector> _frameDetectors;
    System.Threading.Timer? _timer;
    volatile bool _paused;
    int _busy;
    string _status = "";

    public event Action<string>? StatusChanged;
    public event Action<WatchEvent>? EventRaised;

    public Watcher(WatcherConfig config)
    {
        _config = config;
        _finder = new GameWindowFinder(config.Window);
        _drop = new GameDropDetector(config.Drop);
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

    public void Start() => _timer ??= new System.Threading.Timer(_ => Tick(), null, 0, _config.PollIntervalMs);

    void Tick()
    {
        if (_paused || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = DateTime.Now;
            IntPtr hwnd = _finder.Find();
            Raise(_drop.Update(hwnd != IntPtr.Zero, now));

            if (hwnd == IntPtr.Zero) { NoFrame("Game: not found"); return; }

            switch (_capture.Capture(hwnd, _frame))
            {
                case CaptureResult.Minimized: NoFrame("Game: found (minimised - cannot see it)"); return;
                case CaptureResult.Failed: NoFrame("Game: found (capture failed)"); return;
            }
            if (_frame.LooksBlank()) { NoFrame("Game: found (capture is black)"); return; }

            SetStatus($"Game: found ({_frame.Width}x{_frame.Height})");
            foreach (var d in _frameDetectors) Raise(d.Process(_frame, now));
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
        if (e is not null) EventRaised?.Invoke(e);
    }

    public void Dispose()
    {
        using var done = new ManualResetEvent(false);
        if (_timer is not null && _timer.Dispose(done)) done.WaitOne(TimeSpan.FromSeconds(3));
        _timer = null;
        _capture.Dispose();
    }
}
