using System.Collections.Concurrent;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>A HUD value reader with alert logic (ammo, fuel, kill counter, items). Runs on the
/// polling thread once per poll.</summary>
public interface IHudDetector
{
    /// <summary>Reads the frame, updates its rows in the shared <see cref="HudReadings"/> and adds
    /// any alert that fires to <paramref name="events"/>.</summary>
    void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events);

    /// <summary>Frames stopped arriving (paused, minimised, game gone): forget half-confirmed
    /// readings and blank the rows. Fired/armed state is kept.</summary>
    void Reset();
}

/// <summary>Read-only view of a detector's alert arming (journal "rearm" records). Key = alert row
/// id (item rows also "id:gone"); true = ready to fire.</summary>
public interface IArmedState
{
    void CopyArmed(IDictionary<string, bool> into);
}

/// <summary>What a row shows under "ตอนนี้". Reached = at/under its alert threshold.
/// TwoFound = two icons of this potion type are on the bar (the row shows its left/right chooser).</summary>
public readonly record struct RowReading(string Text, bool Reached, bool TwoFound = false)
{
    public const string None = "—";
    public const string Unknown = "?";
}

/// <summary>Latest reading per alert row id. Written by the polling thread, read by the UI.</summary>
public sealed class HudReadings
{
    readonly ConcurrentDictionary<string, RowReading> _rows = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string id, string text, bool reached = false, bool twoFound = false) => _rows[id] = new RowReading(text, reached, twoFound);
    public RowReading Get(string id) => _rows.TryGetValue(id, out var r) ? r : new RowReading(RowReading.None, false);
}

/// <summary>The user choices the detectors need (thresholds, potion side). Set from the UI
/// thread, read on the polling thread.</summary>
public sealed class HudOptions
{
    readonly ConcurrentDictionary<string, int> _thresholds = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, bool> _right = new(StringComparer.OrdinalIgnoreCase);

    public void SetThreshold(string id, int value) => _thresholds[id] = value;
    public int Threshold(string id, int fallback) => _thresholds.TryGetValue(id, out int v) ? v : fallback;

    /// <summary>Two icons of one potion type on the bar: watch the right one (default: left).</summary>
    public void SetWatchRight(string id, bool right) => _right[id] = right;
    public bool WatchRight(string id) => _right.TryGetValue(id, out bool r) && r;
}

/// <summary>
/// "Act only on a stable reading": a value becomes current after it was read on <c>polls</c>
/// consecutive polls. An unreadable poll breaks the run; after <see cref="UnknownAfter"/>
/// unreadable polls in a row the value is unknown ("?").
/// </summary>
public sealed class StableValue<T>(int polls) where T : struct, IEquatable<T>
{
    public const int UnknownAfter = 3;
    T _candidate;
    int _run, _unreadable;

    /// <summary>The last confirmed value; null while unknown.</summary>
    public T? Value { get; private set; }

    /// <summary>The most recent readable reading, confirmed or not (what "ตอนนี้" shows: a counter
    /// that changes every second is never equal twice in a row); null while unknown.</summary>
    public T? Latest { get; private set; }

    /// <summary>Feeds one poll (null = unreadable). Returns true when <see cref="Value"/> was
    /// confirmed (again) by this poll.</summary>
    public bool Push(T? reading)
    {
        if (reading is not { } v)
        {
            _run = 0;
            if (++_unreadable >= UnknownAfter) Value = Latest = null;
            return false;
        }
        _unreadable = 0;
        Latest = v;
        if (_run > 0 && v.Equals(_candidate)) _run++;
        else { _candidate = v; _run = 1; }
        if (_run < polls) return false;
        Value = v;
        return true;
    }

    /// <summary>Forget everything (frames stopped).</summary>
    public void Reset()
    {
        _run = _unreadable = 0;
        Value = Latest = null;
    }
}

static class HudCrop
{
    public static CollectRegion Region(WatcherConfig config, string name) =>
        new(config.Collect.Regions.FirstOrDefault(r => r.Name == name)
            ?? throw new InvalidDataException($"cropRegion: no collect region named '{name}'"));
}
