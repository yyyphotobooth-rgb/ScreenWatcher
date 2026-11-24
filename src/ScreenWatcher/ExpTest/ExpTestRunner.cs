using System.Globalization;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.ExpTest;

public enum ExpPhase { Idle, Starting, Running, Finishing }

/// <summary>One stable reading with the crops taken on the same poll.</summary>
public sealed record ExpReading(DateTime Time, int Level, int PctHundredths, BgraImage LevelCrop, MapLabel? Map)
{
    public string Text => $"Lv.{Level} {LevelLine.FormatPct(PctHundredths)}%";
}

/// <summary>Things that make a result less useful. The last three make it ใช้เทียบไม่ได้.</summary>
public sealed record ExpFlags(bool LevelUp, bool Died, bool GameDropped, bool MapChanged, bool Paused)
{
    public bool NotComparable => GameDropped || MapChanged || Paused;

    public IEnumerable<string> Words()
    {
        if (LevelUp) yield return "มีเลเวลอัพ";
        if (Died) yield return "ตายระหว่างทดสอบ";
        if (GameDropped) yield return "เกมหลุด";
        if (MapChanged) yield return "เปลี่ยนแมป";
        if (Paused) yield return "หยุดเฝ้าระหว่างทดสอบ";
    }
}

/// <summary>A finished test. End is null when no stable reading was found near the end (unusable:
/// no number is given).</summary>
public sealed record ExpResult(string Id, string Source, int Minutes, ExpReading Start, ExpReading? End, int? Kills, ExpFlags Flags,
    MapCompare? Map)
{
    public bool Usable => End is not null && !TooLittle;

    /// <summary>Ended early by "/exp stop" or the window's stop button (Minutes = the minutes measured).</summary>
    public bool Stopped { get; init; }

    /// <summary>The minutes the test was asked for (the Y of "ทดสอบ X จาก Y นาที").</summary>
    public int PlannedMinutes { get; init; }

    /// <summary>A stopped test with no end reading or no EXP change: no numbers are given.</summary>
    public bool TooLittle => Stopped && (End is null || GainHundredths == 0);
    public bool Comparable => Usable && !Flags.NotComparable;

    /// <summary>(level_end - level_start) * 100 + pct_end - pct_start, in hundredths of a percent.</summary>
    public int? GainHundredths => End is { } e ? Gain(Start.Level, Start.PctHundredths, e.Level, e.PctHundredths) : null;

    public static int Gain(int levelA, int pctA, int levelB, int pctB) => (levelB - levelA) * 10000 + pctB - pctA;

    /// <summary>Minutes between the two readings actually used.</summary>
    public double ElapsedMinutes => End is { } e ? (e.Time - Start.Time).TotalMinutes : 0;

    /// <summary>Gain per minute in percent (gain / minutes actually measured between the two readings).</summary>
    public double? RatePerMin => GainHundredths is { } g ? ExpMath.Rate(g, ElapsedMinutes) : null;

    /// <summary>Kills per minute over the measured minutes (null when kills or time are unknown).</summary>
    public double? KillsPerMin => Kills is { } k && ElapsedMinutes > 0 ? k / ElapsedMinutes : null;

    /// <summary>Gain per 10 minutes (hundredths). Kept only for the stored file; never shown or sorted on.</summary>
    public double? GainPer10Min => GainHundredths is { } g && ElapsedMinutes > 0 ? g * 10.0 / ElapsedMinutes : null;

    /// <summary>Minutes for a whole level / until the next one; null when not meaningful (see <see cref="ExpMath"/>).</summary>
    public int? MinutesPerLevel => RatePerMin is { } r && !Flags.LevelUp ? ExpMath.MinutesPerLevel(r) : null;
    public int? MinutesLeft => RatePerMin is { } r && End is { } e && !Flags.LevelUp ? ExpMath.MinutesLeft(r, e.PctHundredths) : null;

    public string Text => ExpTexts.Result(this);
}

/// <summary>All Thai texts of the EXP test (Discord and the window).</summary>
public static class ExpTexts
{
    public static string Started(int minutes) => $"เริ่มทดสอบ EXP · {minutes} นาที";
    public const string Refused = "เริ่มทดสอบ EXP ไม่ได้ อ่านเลเวล/EXP ไม่ได้ (ต้องเห็นบรรทัด Lv. ใต้แถบไอเทม)";
    public const string AlreadyStarting = "กำลังเริ่มทดสอบ EXP อยู่ รออ่านค่า EXP";
    public static string Busy(TimeSpan left) => $"กำลังทดสอบ EXP อยู่ เหลืออีก {Clock(left)}";
    public const string Cancelled = "ยกเลิกทดสอบ EXP แล้ว";
    public const string NothingToCancel = "ไม่มีการทดสอบ EXP ที่กำลังทำอยู่";
    public const string NothingToStop = NothingToCancel;
    public static string Stopping(int minutes) => $"หยุดทดสอบ EXP แล้ว กำลังสรุปผลจาก {minutes} นาทีที่วัดได้";
    public const string TooLittle = "ข้อมูลน้อยเกินไป คำนวณไม่ได้";
    public static string EarlyStop(int measured, int planned) => $"หยุดก่อนกำหนด (ทดสอบ {measured} จาก {planned} นาที)";
    /// <summary>Stopped with under 1 minute measured (or still starting): no numbers.</summary>
    public static string StopTooShort(int planned) => TooLittle + "\n" + EarlyStop(0, planned);
    public const string StopAlreadyEnding = "ทดสอบ EXP ครบเวลาแล้ว กำลังสรุปผลอยู่";
    public const string ListEmpty = "ยังไม่มีผลทดสอบ EXP ที่ใช้เทียบได้";
    public const string ListCaption = "ผลเทียบกันได้เฉพาะที่เลเวลเดียวกัน และไม่ได้อยู่ในช่วงโบนัส EXP";
    public const string Unusable = "ผลใช้ไม่ได้ อ่านค่า EXP ตอนจบไม่ได้";
    public const string NotComparable = "ใช้เทียบไม่ได้";
    public const string KillsUnknown = "ไม่ทราบ";

    public static string Clock(TimeSpan t)
    {
        int s = Math.Max(0, (int)Math.Ceiling(t.TotalSeconds));
        return $"{s / 60}:{s % 60:D2}";
    }

    public static string SignedPct(double hundredths) =>
        (hundredths < 0 ? "-" : "+") + (Math.Abs(hundredths) / 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    public static string Kills(int? kills) => kills is { } k ? $"ยิงมอน {k} ตัว" : $"ยิงมอน {KillsUnknown}";

    public static string KillsPerMin(double q) => q.ToString("0.0", CultureInfo.InvariantCulture);

    public static string Result(ExpResult r)
    {
        if (r.TooLittle) return TooLittle + "\n" + EarlyStop(r.Minutes, r.PlannedMinutes);
        var lines = new List<string> { $"ทดสอบ EXP จบ · {r.Minutes} นาที" };
        if (r.Stopped) lines.Add(EarlyStop(r.Minutes, r.PlannedMinutes));
        if (r.End is { } e)
        {
            lines.Add($"ได้ {SignedPct(r.GainHundredths!.Value)} ({r.Start.Text} → {e.Text})");
            if (r.RatePerMin is { } rate)
            {
                lines.Add($"เฉลี่ย {ExpMath.RateText(rate)}% ต่อนาที");
                if (r.MinutesPerLevel is { } per) lines.Add($"เวลาต่อ 1 เลเวล (Lv.{e.Level}) : ราว {ExpMath.Duration(per)}");
                if (r.MinutesLeft is { } left) lines.Add($"เหลืออีกราว {ExpMath.Duration(left)} จะเลเวลอัพ");
            }
        }
        else lines.Add($"{Unusable} (เริ่ม {r.Start.Text})");
        lines.Add(r.Kills is { } k
            ? r.KillsPerMin is { } q ? $"ยิงมอน {k} ตัว · {KillsPerMin(q)} ตัวต่อนาที" : $"ยิงมอน {k} ตัว"
            : $"ยิงมอน {KillsUnknown}");
        var flags = r.Flags.Words().ToList();
        if (r.Usable && r.Flags.NotComparable) flags.Add(NotComparable);
        if (flags.Count > 0) lines.Add(string.Join(" · ", flags));
        return string.Join("\n", lines);
    }
}

/// <summary>Rate and time maths shared by the message, the images and the stored results.</summary>
public static class ExpMath
{
    /// <summary>Percent per minute; null when no time passed.</summary>
    public static double? Rate(double gainHundredths, double minutes) => minutes > 0 ? gainHundredths / 100.0 / minutes : null;

    static int Round(double minutes) => (int)Math.Round(minutes, MidpointRounding.AwayFromZero);

    /// <summary>100 / R rounded to minutes; null when R is zero or negative.</summary>
    public static int? MinutesPerLevel(double rate) => rate > 0 ? Round(100.0 / rate) : null;

    /// <summary>(100 - P2) / R rounded to minutes; null when R is zero or negative.</summary>
    public static int? MinutesLeft(double rate, int endPctHundredths) => rate > 0 ? Round(Math.Max(0, 100.0 - endPctHundredths / 100.0) / rate) : null;

    /// <summary>"H ชม. M นาที", or "M นาที" under one hour.</summary>
    public static string Duration(int minutes) => minutes >= 60 ? $"{minutes / 60} ชม. {minutes % 60} นาที" : $"{minutes} นาที";

    /// <summary>2 decimals, 3 when below 0.10.</summary>
    public static string RateText(double rate) => rate.ToString(Math.Abs(rate) < 0.10 ? "0.000" : "0.00", CultureInfo.InvariantCulture);
}

/// <summary>What happened to a stop request (<see cref="ExpTestRunner.Stop"/>).</summary>
public enum ExpStopStatus { NotRunning, TooShort, Stopping, AlreadyEnding }

/// <summary>What happened to a start request.</summary>
public enum ExpStartStatus { Started, Refused, Busy, AlreadyStarting }

public sealed record ExpStartOutcome(ExpStartStatus Status, string Text, ExpReading? Start = null);

/// <summary>
/// The EXP test state machine. Fed once per poll by the watcher (polling thread); start / cancel
/// come from the UI or Discord threads. Only one test at a time.
/// - Start: waits up to ReadWaitSeconds for a stable level + percentage reading (same level as the
///   previous poll, percentage 0..StableMaxStep higher); the level-line and map-label crops are
///   copied on that poll. None in time: refused.
/// - Running until start + minutes: kills (the counter's positive steps, restart after Target
///   counted), death, game gone, watcher paused.
/// - End: the stable reading nearest the end time within ReadWaitSeconds (before or after); none:
///   the result is unusable (no number).
/// The percentage is read (and crops are taken) only while a test is starting / running.
/// </summary>
public sealed class ExpTestRunner
{
    readonly ExpTestConfig _c;
    readonly KillConfig _kill;
    readonly LevelReader? _levelReader;
    readonly MapLabelReader _mapReader;
    readonly Collection.CollectRegion _levelRegion;
    readonly object _gate = new();

    ExpPhase _phase;
    string _source = "";
    int _minutes;
    DateTime _startDeadline, _endAt;
    TaskCompletionSource<ExpStartOutcome>? _startTcs;
    ExpReading? _start, _candidate;
    LevelLine? _prev;
    // Kills
    int? _killRaw, _killLast;
    int _killSum, _killPolls, _killVisible;
    bool _died, _dropped, _paused, _stopped;

    /// <summary>Raised (polling thread, outside the lock) when a test started / was refused / finished.</summary>
    public event Action<ExpStartOutcome, string>? StartDecided;
    public event Action<ExpResult>? Finished;

    public ExpTestRunner(WatcherConfig config, LevelReader? levelReader)
    {
        _c = config.ExpTest;
        _kill = config.Kill;
        _levelReader = levelReader;
        _mapReader = new MapLabelReader(_c);
        _levelRegion = new Collection.CollectRegion(config.Collect.AllRegions.First(r => r.Name == "level"));
    }

    public MapLabelReader MapReader => _mapReader;

    public ExpPhase Phase { get { lock (_gate) return _phase; } }

    /// <summary>Time left in the running test (null when none is running).</summary>
    public TimeSpan? TimeLeft(DateTime now)
    {
        lock (_gate) return _phase is ExpPhase.Running or ExpPhase.Finishing ? (_endAt > now ? _endAt - now : TimeSpan.Zero) : null;
    }

    public int Minutes { get { lock (_gate) return _minutes; } }

    /// <summary>Asks for a test of <paramref name="minutes"/>. The task completes when it started or was
    /// refused (within ReadWaitSeconds of polls), or at once when one is already starting / running.</summary>
    public Task<ExpStartOutcome> RequestStart(int minutes, string source, DateTime now)
    {
        lock (_gate)
        {
            if (_phase == ExpPhase.Starting) return Task.FromResult(new ExpStartOutcome(ExpStartStatus.AlreadyStarting, ExpTexts.AlreadyStarting));
            if (_phase != ExpPhase.Idle)
                return Task.FromResult(new ExpStartOutcome(ExpStartStatus.Busy, ExpTexts.Busy(_endAt > now ? _endAt - now : TimeSpan.Zero)));
            SetPhase(ExpPhase.Starting);
            _source = source;
            _minutes = Math.Clamp(minutes, _c.MinMinutes, _c.MaxMinutes);
            _startDeadline = now.AddSeconds(_c.ReadWaitSeconds);
            _startTcs = new TaskCompletionSource<ExpStartOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _startTcs.Task;
        }
    }

    /// <summary>Cancels a starting or running test. False when there was none.</summary>
    public bool Cancel()
    {
        TaskCompletionSource<ExpStartOutcome>? tcs;
        lock (_gate)
        {
            if (_phase == ExpPhase.Idle) return false;
            tcs = _phase == ExpPhase.Starting ? _startTcs : null;
            Clear();
        }
        tcs?.TrySetResult(new ExpStartOutcome(ExpStartStatus.Refused, ExpTexts.Cancelled));
        return true;
    }

    /// <summary>
    /// Ends a running test now ("/exp stop"): the end time becomes <paramref name="now"/> and the
    /// result is made exactly as at a normal end (the stable reading nearest that time, then
    /// <see cref="Finished"/>), with the minutes actually measured. Under MinStopMinutes measured
    /// (or still starting): dropped like a cancel. Minutes: the measured minutes (Stopping).
    /// </summary>
    public (ExpStopStatus Status, int Minutes, int Planned) Stop(DateTime now)
    {
        TaskCompletionSource<ExpStartOutcome>? tcs = null;
        (ExpStopStatus, int, int) r;
        int planned;
        lock (_gate)
        {
            switch (_phase)
            {
                case ExpPhase.Idle: return (ExpStopStatus.NotRunning, 0, 0);
                case ExpPhase.Finishing: return (ExpStopStatus.AlreadyEnding, _minutes, _minutes);
                case ExpPhase.Running when now - _start!.Time >= TimeSpan.FromMinutes(MinStopMinutes):
                    _stopped = true;
                    _endAt = now;
                    SetPhase(ExpPhase.Finishing);
                    r = (ExpStopStatus.Stopping, MeasuredMinutes(now - _start.Time), _minutes);
                    break;
                default:
                    tcs = _phase == ExpPhase.Starting ? _startTcs : null;
                    planned = _minutes;
                    Clear();
                    r = (ExpStopStatus.TooShort, 0, planned);
                    break;
            }
        }
        tcs?.TrySetResult(new ExpStartOutcome(ExpStartStatus.Refused, ExpTexts.StopTooShort(r.Item3)));
        return r;
    }

    /// <summary>A stopped test shorter than this is dropped.</summary>
    public const int MinStopMinutes = 1;

    static int MeasuredMinutes(TimeSpan t) => Math.Max(MinStopMinutes, (int)Math.Round(t.TotalMinutes, MidpointRounding.AwayFromZero));

    /// <summary>Polling thread, after the HUD readers ran on this frame.</summary>
    public void OnFrame(Frame frame, HudScale scale, DateTime now, int? level, KillObservation kill, bool dead)
    {
        if (Volatile.Read(ref _phaseInt) == 0) { _prev = null; return; }   // idle: nothing read, nothing kept
        LevelLine? line = level is { } l && _levelReader is not null ? new LevelLine(l, _levelReader.ReadPercent(frame, scale, l)) : null;
        OnPoll(now, line, kill, dead, () => (BgraImage.Copy(frame, _levelRegion.Locate(frame.Width, frame.Height, scale)), _mapReader.Capture(frame, scale)));
    }

    int _phaseInt;   // mirrors _phase for the lock-free idle check

    /// <summary>One poll with a frame (logic; tests call it directly). <paramref name="capture"/> copies
    /// the level-line crop and the map label from the current frame.</summary>
    public void OnPoll(DateTime now, LevelLine? line, KillObservation kill, bool dead, Func<(BgraImage Level, MapLabel? Map)> capture)
    {
        bool stable = line is { PctHundredths: { } p } cur && _prev is { PctHundredths: { } q } prev && cur.Level == prev.Level
            && p >= q && p - q <= _c.StableMaxStepHundredths;
        _prev = line;
        ExpReading Reading() { var (lc, map) = capture(); return new ExpReading(now, line!.Value.Level, line.Value.PctHundredths!.Value, lc, map); }

        (ExpStartOutcome, string)? decided = null;
        ExpResult? finished = null;
        TaskCompletionSource<ExpStartOutcome>? tcs = null;
        lock (_gate)
        {
            switch (_phase)
            {
                case ExpPhase.Starting:
                    if (stable)
                    {
                        _start = Reading();
                        _endAt = now.AddMinutes(_minutes);
                        SetPhase(ExpPhase.Running);
                        CountKill(kill);   // the counter at the start is the kills' baseline
                        tcs = _startTcs;
                        decided = (new ExpStartOutcome(ExpStartStatus.Started, ExpTexts.Started(_minutes), _start), _source);
                    }
                    else if (now > _startDeadline) (tcs, decided) = Refuse();
                    break;
                case ExpPhase.Running:
                case ExpPhase.Finishing:
                    if (now < _endAt)
                    {
                        CountKill(kill);
                        if (dead) _died = true;
                        if (stable && now >= _endAt.AddSeconds(-_c.ReadWaitSeconds)) _candidate = Reading();
                        break;
                    }
                    SetPhase(ExpPhase.Finishing);
                    if (stable)
                    {
                        // Nearest to the end time: this one or the last one before the end.
                        var after = Reading();
                        var end = _candidate is { } c && _endAt - c.Time <= after.Time - _endAt ? c : after;
                        finished = Finish(end);
                    }
                    else finished = MaybeFinishWithout(now);
                    break;
            }
        }
        Raise(tcs, decided, finished);
    }

    /// <summary>A poll without a frame (game not found, minimised, capture failed, blank).</summary>
    public void OnNoFrame(DateTime now, bool gameMissing) => Tick(now, gameMissing, paused: false);

    /// <summary>A poll skipped because watching is paused.</summary>
    public void OnPaused(DateTime now) => Tick(now, gameMissing: false, paused: true);

    void Tick(DateTime now, bool gameMissing, bool paused)
    {
        if (Volatile.Read(ref _phaseInt) == 0) { _prev = null; return; }
        _prev = null;
        (ExpStartOutcome, string)? decided = null;
        ExpResult? finished = null;
        TaskCompletionSource<ExpStartOutcome>? tcs = null;
        lock (_gate)
        {
            switch (_phase)
            {
                case ExpPhase.Starting:
                    if (now > _startDeadline) (tcs, decided) = Refuse();
                    break;
                case ExpPhase.Running:
                case ExpPhase.Finishing:
                    if (now < _endAt)
                    {
                        CountKill(KillObservation.Unreadable);
                        if (gameMissing) _dropped = true;
                        if (paused) _paused = true;
                        break;
                    }
                    SetPhase(ExpPhase.Finishing);
                    finished = MaybeFinishWithout(now);
                    break;
            }
        }
        Raise(tcs, decided, finished);
    }

    void Raise(TaskCompletionSource<ExpStartOutcome>? tcs, (ExpStartOutcome Outcome, string Source)? decided, ExpResult? finished)
    {
        if (decided is { } d)
        {
            try { StartDecided?.Invoke(d.Outcome, d.Source); } catch (Exception) { }
            tcs?.TrySetResult(d.Outcome);
        }
        if (finished is not null) try { Finished?.Invoke(finished); } catch (Exception) { }
    }

    (TaskCompletionSource<ExpStartOutcome>?, (ExpStartOutcome, string)?) Refuse()
    {
        var tcs = _startTcs;
        var d = (new ExpStartOutcome(ExpStartStatus.Refused, ExpTexts.Refused), _source);
        Clear();
        return (tcs, d);
    }

    /// <summary>No stable reading on this poll: finish with the candidate before the end once nothing
    /// later can be nearer, or as unusable after ReadWaitSeconds.</summary>
    ExpResult? MaybeFinishWithout(DateTime now)
    {
        var since = now - _endAt;
        if (_candidate is { } c && since >= _endAt - c.Time) return Finish(c);
        if (since.TotalSeconds > _c.ReadWaitSeconds) return Finish(_candidate);
        return null;
    }

    ExpResult Finish(ExpReading? end)
    {
        var start = _start!;
        MapCompare? map = end?.Map is { } em && start.Map is { } sm ? _mapReader.Compare(sm, em) : null;
        int? kills = _killPolls > 0 && _killVisible >= _c.KillVisibleMinFraction * _killPolls ? _killSum : null;
        var flags = new ExpFlags(end is not null && end.Level > start.Level, _died, _dropped, map is { Comparable: true, Why: "name" }, _paused);   // only the map NAME decides; zone moves inside a map are normal
        // Stopped early: the minutes actually measured (between the two readings, or up to the stop).
        int minutes = _stopped ? MeasuredMinutes((end?.Time ?? _endAt) - start.Time) : _minutes;
        var r = new ExpResult(start.Time.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture), _source, minutes, start, end, kills, flags, map) { Stopped = _stopped, PlannedMinutes = _minutes };
        Clear();
        return r;
    }

    /// <summary>Kill counter: a number is taken when the previous poll's number leads up to it (as the
    /// kill row does); the sum adds every rise, and a fall from NearTarget or more is the restart
    /// after Target (the kills up to Target plus the new number).</summary>
    void CountKill(KillObservation o)
    {
        _killPolls++;
        if (o.Value is not { } v) { _killRaw = null; return; }
        _killVisible++;
        if (_killRaw is { } p && v >= p && v - p <= _kill.MaxStepPerPoll)
        {
            _killLast ??= p;   // first confirmed number: count from the reading that led to it
            if (_killLast is { } c)
            {
                if (v >= c) _killSum += v - c;
                else if (c >= _kill.NearTarget) _killSum += Math.Max(0, _kill.Target - c) + v;
                else _killSum += v;
            }
            _killLast = v;
        }
        _killRaw = v;
    }

    void SetPhase(ExpPhase p)
    {
        _phase = p;
        Volatile.Write(ref _phaseInt, p == ExpPhase.Idle ? 0 : 1);
    }

    void Clear()
    {
        SetPhase(ExpPhase.Idle);
        _startTcs = null;
        _start = _candidate = null;
        _killRaw = _killLast = null;
        _killSum = _killPolls = _killVisible = 0;
        _died = _dropped = _paused = _stopped = false;
    }
}
