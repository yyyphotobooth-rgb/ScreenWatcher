using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>
/// Row "EXP ไม่ขยับเกิน N นาที". Works on the level line the level row read on this poll (level +
/// percentage; the percentage is accepted only when its level digits agree with the level read).
/// - A reading is the pair (level, percentage); it is stable after StablePolls equal polls.
/// - Clock: starts at the last stable reading where level or percentage changed (the first stable
///   reading after the app starts or the game window is found again is the baseline: fresh clock,
///   no alert). Only the time between two polls that both read the percentage counts (game found,
///   watcher not paused, percentage readable; a gap longer than MaxPollGapSeconds is not counted).
///   Unreadable time does not count and does not reset the clock.
/// - Counted time over N minutes: one alert with the full client frame. Re-armed as soon as a
///   stable reading differs from the baseline (that also restarts the clock).
/// "ตอนนี้": "ปกติ" (under one minute), "นิ่ง M นาที", "?" (percentage not read for a few polls), "—".
/// Not part of the "อ่านค่าไม่ได้" rule (the level row is).
/// </summary>
public sealed class ExpStallDetector : IHudDetector, IArmedState
{
    readonly ExpStallConfig _c;
    readonly HudReadings _readings;
    readonly HudOptions _options;
    readonly Func<LevelLine?> _line;
    readonly StableValue<(int Level, int Pct)> _stable;
    (int Level, int Pct)? _baseline;
    DateTime? _lastReadableAt;
    double _countedSeconds;
    bool _fired, _seen;

    /// <param name="line">The level line read on this poll (LevelDetector.LastLine; runs after it).</param>
    public ExpStallDetector(WatcherConfig config, HudReadings readings, HudOptions options, Func<LevelLine?> line)
    {
        _c = config.ExpStall;
        _readings = readings;
        _options = options;
        _line = line;
        _stable = new StableValue<(int, int)>(Math.Max(1, _c.StablePolls));
        _readings.Set(AlertIds.ExpStall, RowReading.None);
    }

    /// <summary>The limit in use (minutes; the user's value or the default, within MinMinutes..MaxMinutes).</summary>
    public int LimitMinutes => Math.Clamp(_options.Threshold(AlertIds.ExpStall, _c.DefaultMinutes), _c.MinMinutes, _c.MaxMinutes);

    /// <summary>Time counted since the last change (seconds; tests and logs).</summary>
    public double CountedSeconds => _countedSeconds;

    /// <summary>"ตอนนี้" for a counted time.</summary>
    public static string StallText(double seconds) => seconds < 60 ? "ปกติ" : $"นิ่ง {(int)(seconds / 60)} นาที";

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events) =>
        Update(_line(), now, frame.Width, frame.Height, events);

    /// <summary>One poll with a frame (null or no percentage = unreadable); tests call it directly.</summary>
    public void Update(LevelLine? line, DateTime now, int width, int height, List<WatchEvent> events)
    {
        (int, int)? reading = line is { PctHundredths: { } p } l ? (l.Level, p) : null;
        if (reading is not null)
        {
            _seen = true;
            if (_baseline is not null && _lastReadableAt is { } prev)
            {
                double gap = (now - prev).TotalSeconds;
                if (gap > 0 && gap <= _c.MaxPollGapSeconds) _countedSeconds += gap;
            }
            _lastReadableAt = now;
        }
        else _lastReadableAt = null;   // the next readable poll starts counting again from itself

        if (_stable.Push(reading) && _stable.Value is { } v && v != _baseline)
        {
            // First stable reading of the session, or a change: the clock starts here (re-armed).
            _baseline = v;
            _countedSeconds = 0;
            _fired = false;
        }

        int limit = LimitMinutes;
        if (reading is not null && _baseline is not null && !_fired && _countedSeconds > limit * 60.0)
        {
            _fired = true;
            events.Add(new WatchEvent(WatchEventKind.Hud, string.Format(_c.Message, limit), now, AlertIds.ExpStall, new Rectangle(0, 0, width, height)));
        }

        string text = _stable.Latest is not null ? StallText(_countedSeconds) : _seen ? RowReading.Unknown : RowReading.None;
        _readings.Set(AlertIds.ExpStall, text, _baseline is not null && _countedSeconds > limit * 60.0);
    }

    public void CopyArmed(IDictionary<string, bool> into) => into[AlertIds.ExpStall] = !_fired;

    /// <summary>Frames stopped (paused, minimised, blank): nothing counts until frames come back;
    /// the clock and the baseline stay.</summary>
    public void Reset()
    {
        _stable.Reset();
        _lastReadableAt = null;
        _readings.Set(AlertIds.ExpStall, RowReading.None);
    }

    /// <summary>The game window is gone: the next stable reading starts a fresh clock.</summary>
    public void EndSession()
    {
        Reset();
        _baseline = null;
        _countedSeconds = 0;
        _fired = _seen = false;
    }
}
