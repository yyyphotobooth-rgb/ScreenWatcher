using ScreenWatcher.Capture;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;

namespace ScreenWatcher;

public enum GameState { Checking, NotFound, Found, Minimized, CaptureFailed, Blank, Error }

/// <summary>What the watcher last saw of the game (the UI words it in Thai).</summary>
public sealed record GameStatus(GameState State, int Width = 0, int Height = 0, HudScale Scale = default, bool ScaleMeasured = false, string? Detail = null);

/// <summary>Result of an on-demand full-frame snapshot (Discord /ss).</summary>
public sealed record SnapshotResult(GameState State, byte[]? Png);

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
    readonly DeathDetector _death;
    readonly HudScaleTracker _scale;
    readonly IReadOnlyList<IFrameDetector> _frameDetectors;
    readonly IReadOnlyList<IHudDetector> _hudDetectors;
    readonly List<WatchEvent> _hudEvents = [];
    // Held while the game is being found/captured (poll or snapshot), so the reused frame
    // buffer and the capture session are never used by two threads at once.
    readonly object _gate = new();
    System.Threading.Timer? _timer;
    volatile bool _paused;
    volatile DataCollector? _collector;
    bool _haveFrame, _frameIsCurrent;
    HudScale _lastScale = HudScale.One;
    GameStatus _status = new(GameState.Checking);

    public event Action<GameStatus>? StatusChanged;

    /// <summary>
    /// Raised on the polling thread when a detector fires. <c>frame</c> is the frame the event
    /// was seen on (death) or null (game dropped: there is no window). The frame buffer is reused
    /// by the next poll, so a handler must copy what it needs before returning.
    /// </summary>
    public event Action<WatchEvent, Frame?, HudScale>? EventRaised;

    public Watcher(WatcherConfig config)
    {
        _config = config;
        _finder = new GameWindowFinder(config.Window);
        _drop = new GameDropDetector(config.Drop);
        _death = new DeathDetector(config.Death);
        _scale = new HudScaleTracker(config.HudScale);
        _frameDetectors = [_death];
        var glyphs = GlyphSet.LoadAll(Path.Combine(AppContext.BaseDirectory, GlyphSet.FileName));
        _hudDetectors = [new AmmoDetector(config, glyphs, Readings), new FuelDetector(config, Readings, Options),
            new KillDetector(config, glyphs, Readings)];
        ResetHud();
    }

    /// <summary>Latest value of every HUD row (the "ตอนนี้" column).</summary>
    public HudReadings Readings { get; } = new();

    /// <summary>Row thresholds and choices; the UI sets them, detectors read them each poll.</summary>
    public HudOptions Options { get; } = new();

    public GameStatus Status => _status;

    /// <summary>True from a detected death until the HP fill is seen again.</summary>
    public bool DeathActive => !_death.Armed;

    /// <summary>True from a "game dropped" event until the window is seen again.</summary>
    public bool DropActive => _drop.Fired;

    /// <summary>True once the game window has been seen in this session.</summary>
    public bool GameEverSeen => _drop.Seen;

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value) foreach (var d in _frameDetectors) d.ResetTiming();
            // Rows go blank while paused; under the gate so a poll in flight cannot refill them.
            else lock (_gate) ResetHud();
        }
    }

    /// <summary>"Collect data" mode: null = off (costs one null check per poll). Set from any
    /// thread; returns the previous collector, which the caller disposes.</summary>
    public DataCollector? SwapCollector(DataCollector? collector) => Interlocked.Exchange(ref _collector, collector);

    public DataCollector? CurrentCollector => _collector;

    public void Start() => _timer ??= new System.Threading.Timer(_ => Tick(), null, 0, _config.PollIntervalMs);

    void Tick()
    {
        if (_paused || !Monitor.TryEnter(_gate)) return;
        try
        {
            var now = DateTime.Now;
            _frameIsCurrent = false;
            IntPtr hwnd = _finder.Find();
            Raise(_drop.Update(hwnd != IntPtr.Zero, now));

            if (hwnd == IntPtr.Zero) { NoFrame(new(GameState.NotFound)); return; }

            switch (_capture.Capture(hwnd, _frame))
            {
                case CaptureResult.Minimized: NoFrame(new(GameState.Minimized)); return;
                case CaptureResult.Failed: NoFrame(new(GameState.CaptureFailed, Detail: _capture.LastError)); return;
            }
            if (_frame.LooksBlank()) { _haveFrame = false; NoFrame(new(GameState.Blank)); return; }

            _haveFrame = _frameIsCurrent = true;
            var scale = _lastScale = _scale.Update(_frame);
            SetStatus(new(GameState.Found, _frame.Width, _frame.Height, scale, _scale.Current));
            foreach (var d in _frameDetectors) Raise(d.Process(_frame, scale, now));
            _hudEvents.Clear();
            foreach (var d in _hudDetectors) d.Process(_frame, scale, now, _hudEvents);
            foreach (var e in _hudEvents) Raise(e);
            _collector?.OnFrame(_frame, scale, now);
        }
        catch (Exception ex)
        {
            NoFrame(new(GameState.Error, Detail: ex.Message));
        }
        finally { Monitor.Exit(_gate); }
    }

    /// <summary>
    /// Captures the game's whole client area now (works while paused) and returns it as PNG.
    /// Png is null when the game is not found or cannot be seen. Read-only, like polling.
    /// </summary>
    public SnapshotResult Snapshot()
    {
        if (!Monitor.TryEnter(_gate, TimeSpan.FromSeconds(5))) return new(GameState.Error, null);
        try
        {
            IntPtr hwnd = _finder.Find();
            if (hwnd == IntPtr.Zero) return new(GameState.NotFound, null);
            switch (_capture.Capture(hwnd, _frame))
            {
                case CaptureResult.Minimized: return new(GameState.Minimized, null);
                case CaptureResult.Failed: return new(GameState.CaptureFailed, null);
            }
            if (_frame.LooksBlank()) return new(GameState.Blank, null);
            return new(GameState.Found, PngEncoder.Encode(_frame, new System.Drawing.Rectangle(0, 0, _frame.Width, _frame.Height)));
        }
        catch (Exception)
        {
            return new(GameState.Error, null);
        }
        finally { Monitor.Exit(_gate); }
    }

    void NoFrame(GameStatus status)
    {
        foreach (var d in _frameDetectors) d.ResetTiming();
        ResetHud();
        SetStatus(status);
    }

    void ResetHud()
    {
        foreach (var d in _hudDetectors) d.Reset();
    }

    void SetStatus(GameStatus s)
    {
        if (s == _status) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    void Raise(WatchEvent? e)
    {
        if (e is null) return;
        try { EventRaised?.Invoke(e, _frameIsCurrent ? _frame : null, _lastScale); }
        catch (Exception) { /* an alert handler must never stop the watcher */ }
        // Death: the frame it was seen on. Game dropped: the last frame captured before (if any).
        _collector?.OnEvent(e, _haveFrame ? _frame : null, _lastScale, frameIsLastSeen: !_frameIsCurrent);
    }

    public void Dispose()
    {
        using var done = new ManualResetEvent(false);
        if (_timer is not null && _timer.Dispose(done)) done.WaitOne(TimeSpan.FromSeconds(3));
        _timer = null;
        SwapCollector(null)?.Dispose();
        lock (_gate) _capture.Dispose();
    }
}
