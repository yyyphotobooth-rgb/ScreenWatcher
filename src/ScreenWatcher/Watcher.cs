using ScreenWatcher.Capture;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Diagnostics;
using ScreenWatcher.ExpTest;
using ScreenWatcher.Imaging;

namespace ScreenWatcher;

public enum GameState { Checking, NotFound, Found, Minimized, CaptureFailed, Blank, Error }

/// <summary>What the watcher last saw of the game (the UI words it in Thai).</summary>
public sealed record GameStatus(GameState State, int Width = 0, int Height = 0, HudScale Scale = default, bool ScaleMeasured = false, string? Detail = null);

/// <summary>Result of an on-demand full-frame snapshot (Discord /ss).</summary>
public sealed record SnapshotResult(GameState State, byte[]? Png);

/// <summary>What /miss keeps: the full client frame now (BGRA rented from ArrayPool, null when the
/// game cannot be seen) and the ring's HUD crops from the last seconds. The receiver returns the
/// buffers (an EvidenceJob does so itself).</summary>
public sealed record MissCapture(GameState State, DateTime Time, byte[]? FramePixels, int Width, int Height, HudScale Scale, IReadOnlyList<RingCrop> Ring);

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
    readonly UnreadableDetector _unreadable;
    readonly QuestPanelDetector _quest;
    readonly InventoryDetector _inventory;
    readonly LevelDetector _level;
    readonly ExpStallDetector _stall;
    readonly KillDetector _kill;
    readonly List<WatchEvent> _hudEvents = [];
    readonly IArmedState[] _armables;
    readonly Dictionary<string, bool> _armed = new(StringComparer.Ordinal), _armedBefore = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _defaultThresholds = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<string, string> _readingText;
    readonly Func<string, int> _threshold;
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
        var glyphs = GlyphSet.ParseAll(AppFiles.ReadText(GlyphSet.FileName));
        var items = new ItemDetector(config, IconSet.Parse(AppFiles.ReadText(IconSet.FileName)), Readings, Options);
        _inventory = new InventoryDetector(config, Readings);
        _level = new LevelDetector(config, Readings, Options);
        _level.Ignored += (from, to, why) => { try { LevelIgnored?.Invoke(from, to, why); } catch (Exception) { } };
        _kill = new KillDetector(config, glyphs, Readings);
        _stall = new ExpStallDetector(config, Readings, Options, () => _level.LastLine);
        // _stall after _level: it uses the line the level row read on this poll.
        _hudDetectors = [new AmmoDetector(config, glyphs, Readings), new FuelDetector(config, Readings, Options),
            _kill, BuffDetector.ExpCard(config, Readings), BuffDetector.Handshake(config, Readings), new WarPillarDetector(config, Readings, Options), items, _inventory, _level, _stall];
        ExpTest = new ExpTestRunner(config, _level.Reader);
        _unreadable = new UnreadableDetector(config, Readings, Options, () => items.BarHiddenPolls, id => _quest!.Covers(id));
        _quest = new QuestPanelDetector(config, Readings, Options, () => _unreadable.LimitSeconds,
            quietUntil: () => _level.LastAlertAt?.AddSeconds(config.Level.QuestQuietSeconds));
        _unreadable.Recovered += r => { try { UnreadableRecovered?.Invoke(r); } catch (Exception) { } };
        _armables = [.. _hudDetectors.OfType<IArmedState>()];
        Ring = new HudRing(config.Collect.Regions.Select(r => new CollectRegion(r)),
            Math.Max(1, (int)Math.Ceiling(config.Diagnostics.RingSeconds * 1000.0 / Math.Max(1, config.PollIntervalMs))));
        _defaultThresholds[AlertIds.FuelLow] = config.Fuel.DefaultThresholdPercent;
        _defaultThresholds[AlertIds.Unreadable] = config.Unreadable.DefaultSeconds;
        _defaultThresholds[AlertIds.ExpStall] = config.ExpStall.DefaultMinutes;
        _defaultThresholds[AlertIds.WarPillar] = config.WarPillar.DefaultMinutes;
        foreach (var item in config.Items.Items) _defaultThresholds[item.Id] = item.DefaultThreshold;
        _readingText = id => Readings.Get(id).Text;
        _threshold = id => Options.Threshold(id, _defaultThresholds.GetValueOrDefault(id));
        CopyArmed(_armedBefore);
        ResetHud();
    }

    /// <summary>"ทดสอบ EXP": fed once per poll (idle: one flag check).</summary>
    public ExpTestRunner ExpTest { get; }

    /// <summary>The last few seconds of HUD region crops (always on; /miss and auto collection).</summary>
    public HudRing Ring { get; }

    /// <summary>Raised on the polling thread after every poll that processed a frame (rows are current).</summary>
    public event Action<DateTime>? Polled;

    /// <summary>Raised on the polling thread when an alert re-arms (fired earlier, now ready to fire
    /// again). Key = alert row id, or "id:gone" for an item row's "หมด" alert.</summary>
    public event Action<string>? Rearmed;

    /// <summary>Raised on the polling thread when a row reported by the "อ่านค่าไม่ได้" alert shows
    /// a value again.</summary>
    public event Action<UnreadableRow>? UnreadableRecovered;

    /// <summary>"ช่องเก็บของเต็ม": un-latch (row button or Discord /reset; any thread, applied on the
    /// next poll). Returns whether it was latched.</summary>
    public bool ResetInventoryFull()
    {
        bool was = _inventory.Latched;
        _inventory.RequestReset();
        return was;
    }

    /// <summary>Polling thread: a stable level reading was ignored (from, to, "lower" / "jump").</summary>
    public event Action<int, int, string>? LevelIgnored;

    /// <summary>The threshold a row uses now (user's value or the row's default).</summary>
    public int ThresholdOf(string alertId) => _threshold(alertId);

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
        if (_paused) { ExpTest.OnPaused(DateTime.Now); return; }
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            var now = DateTime.Now;
            _frameIsCurrent = false;
            IntPtr hwnd = _finder.Find();
            Raise(_drop.Update(hwnd != IntPtr.Zero, now));

            if (hwnd == IntPtr.Zero) { _unreadable.EndSession(); _level.EndSession(); _stall.EndSession(); NoFrame(new(GameState.NotFound)); return; }

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
            _quest.Process(_frame, scale, now, _hudEvents);
            _unreadable.Process(now, _frame.Width, _frame.Height, _hudEvents);
            foreach (var e in _hudEvents) Raise(e);
            ExpTest.OnFrame(_frame, scale, now, _level.LastLevel, _kill.LastObservation, dead: !_death.Armed);
            Ring.Push(_frame, scale, now);
            _collector?.OnFrame(_frame, scale, now, new AutoInputs(now, scale, _scale.Current, _readingText, _threshold, _unreadable));
            CheckRearmed();
            Polled?.Invoke(now);
        }
        catch (Exception ex)
        {
            NoFrame(new(GameState.Error, Detail: ex.Message));
        }
        finally { Monitor.Exit(_gate); }
    }

    void CopyArmed(Dictionary<string, bool> into)
    {
        into[AlertIds.Death] = _death.Armed;
        into[AlertIds.GameDropped] = !_drop.Fired;
        foreach (var a in _armables) a.CopyArmed(into);
    }

    void CheckRearmed()
    {
        CopyArmed(_armed);
        foreach (var (id, armed) in _armed)
        {
            if (armed && _armedBefore.TryGetValue(id, out bool before) && !before)
                try { Rearmed?.Invoke(id); } catch (Exception) { }
            _armedBefore[id] = armed;
        }
    }

    /// <summary>
    /// For /miss: captures the whole client area now (works while paused) and copies the ring's
    /// crops from the last RingSeconds. Read-only, like polling. When the game cannot be seen,
    /// nothing is kept (State says why).
    /// </summary>
    public MissCapture CaptureForMiss()
    {
        var now = DateTime.Now;
        if (!Monitor.TryEnter(_gate, TimeSpan.FromSeconds(5))) return new(GameState.Error, now, null, 0, 0, _lastScale, []);
        try
        {
            IntPtr hwnd = _finder.Find();
            GameState state = GameState.Found;
            if (hwnd == IntPtr.Zero) state = GameState.NotFound;
            else switch (_capture.Capture(hwnd, _frame))
            {
                case CaptureResult.Minimized: state = GameState.Minimized; break;
                case CaptureResult.Failed: state = GameState.CaptureFailed; break;
                default: if (_frame.LooksBlank()) state = GameState.Blank; break;
            }
            if (state != GameState.Found) return new(state, now, null, 0, 0, _lastScale, []);
            var px = System.Buffers.ArrayPool<byte>.Shared.Rent(_frame.Width * _frame.Height * 4);
            _frame.CopyRect(new System.Drawing.Rectangle(0, 0, _frame.Width, _frame.Height), px);
            var ring = Ring.Snapshot(now - TimeSpan.FromSeconds(_config.Diagnostics.RingSeconds), now.AddSeconds(1));
            return new(GameState.Found, now, px, _frame.Width, _frame.Height, _lastScale, ring);
        }
        catch (Exception)
        {
            return new(GameState.Error, now, null, 0, 0, _lastScale, []);
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
        ExpTest.OnNoFrame(DateTime.Now, gameMissing: status.State == GameState.NotFound);
        foreach (var d in _frameDetectors) d.ResetTiming();
        ResetHud();
        SetStatus(status);
        CheckRearmed();
    }

    void ResetHud()
    {
        foreach (var d in _hudDetectors) d.Reset();
        _unreadable.Interrupt();
        _quest?.Interrupt();
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
