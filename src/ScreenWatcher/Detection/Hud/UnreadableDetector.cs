using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Diagnostics;

namespace ScreenWatcher.Detection.Hud;

/// <summary>
/// Row "อ่านค่าไม่ได้เกิน N วินาที". Watches the other HUD rows' "ตอนนี้" readings (it never reads
/// the frame itself and changes nothing in the other rows; their own "?" stays silent as before).
/// A row counts as unreadable when it showed a value earlier in this game session and now shows
/// "?" for longer than N seconds while frames arrive. Item rows also count while the whole item bar
/// is not seen (a covered bar and a bar that is not drawn look the same).
/// Not counted: an item not on the bar or run out ("หมด"; the row must show a value again first),
/// a kill panel that is not shown ("—"), a row switched off, frames stopping (pause, minimised,
/// game lost: the run starts again), and the game window being lost (ends the session).
/// The first row over the limit opens a CombineSeconds window; every row crossing in it joins one
/// message, sent at the window's end with the full client frame. Fires once per row; a row re-arms
/// when it shows a value again. Runs on the polling thread after the other HUD detectors.
/// </summary>
public sealed class UnreadableDetector
{
    enum Sight { Value, Unreadable, NotShown, Hold }

    sealed class Row(string id, string name, bool item)
    {
        public readonly string Id = id;
        public readonly string Name = name;
        public readonly bool Item = item;
        public bool EverReadable, Fired, Pending;
        public DateTime? Since;
        public DateTime FiredRunStart;   // start of the "?" run that fired (recovery duration)
    }

    readonly UnreadableConfig _c;
    readonly HudReadings _readings;
    readonly HudOptions _options;
    readonly Func<int> _barHiddenPolls;
    readonly Func<string, bool>? _covered;
    readonly Row[] _rows;
    readonly List<Row> _pending = [];
    readonly List<Row> _crossed = [];
    DateTime _pendingUntil;
    string _shown = "";

    /// <param name="barHiddenPolls">Polls in a row the item bar was not seen (ItemDetector.BarHiddenPolls).</param>
    /// <param name="covered">Rows covered by a recognised panel right now (QuestPanelDetector.Covers):
    /// they are left out, that panel sends its own message.</param>
    public UnreadableDetector(WatcherConfig config, HudReadings readings, HudOptions options, Func<int> barHiddenPolls, Func<string, bool>? covered = null)
    {
        _covered = covered;
        _c = config.Unreadable;
        _readings = readings;
        _options = options;
        _barHiddenPolls = barHiddenPolls;
        var items = config.Items.Items.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rows = _c.Rows.Select(r => new Row(r[0], r[1], items.Contains(r[0]))).ToArray();
        Show(RowReading.None);
    }

    /// <summary>Raised on the polling thread when a row that fired shows a value again
    /// (Seconds = from the start of the "?" run that fired until now).</summary>
    public event Action<UnreadableRow>? Recovered;

    /// <summary>The limit in use (seconds; the user's value or the default, at least 1).</summary>
    public int LimitSeconds => Math.Max(1, _options.Threshold(AlertIds.Unreadable, _c.DefaultSeconds));

    /// <summary>Alert row ids that crossed the limit on the last <see cref="Process"/>.</summary>
    public IReadOnlyList<string> CrossedThisPoll => _crossedIds;
    readonly List<string> _crossedIds = [];

    /// <summary>True while the row is over the limit (fired) and still shows "?".</summary>
    public bool IsOverLimit(string id)
    {
        foreach (var r in _rows) if (r.Id == id) return r.Fired && r.Since is not null;
        return false;
    }

    /// <summary>One poll with a fresh frame, after the other HUD detectors updated their rows.
    /// Adds the alert to <paramref name="events"/> when a combined message is due.</summary>
    public void Process(DateTime now, int frameWidth, int frameHeight, List<WatchEvent> events)
    {
        int limit = LimitSeconds;
        _crossed.Clear();
        _crossedIds.Clear();
        // A window that ended before this poll (late poll): send it first, so a row crossing now
        // starts its own message.
        if (_pending.Count > 0 && now > _pendingUntil) Flush(now, frameWidth, frameHeight, events);

        foreach (var r in _rows)
        {
            if (!_options.IsEnabled(r.Id))
            {
                if (r.Pending) { r.Pending = false; _pending.Remove(r); }
                r.Since = null;
                r.Fired = false;
                continue;
            }
            switch (See(r))
            {
                case Sight.Value:
                    if (r.Pending)
                    {
                        // Readable again before its message went out: drop it from the message
                        // (field trial 2026-11-19 09:56:47 sent "นับมอน" 3 s after it recovered).
                        r.Pending = false;
                        _pending.Remove(r);
                        r.Fired = false;
                    }
                    else if (r.Fired)
                    {
                        double secs = (now - r.FiredRunStart).TotalSeconds;
                        r.Fired = false;
                        try { Recovered?.Invoke(new UnreadableRow(r.Id, r.Name, Math.Round(secs, 1))); } catch (Exception) { }
                    }
                    r.EverReadable = true;
                    r.Since = null;
                    break;
                case Sight.Unreadable:
                    if (!r.EverReadable) break;
                    r.Since ??= now;
                    if (!r.Fired && (now - r.Since.Value).TotalSeconds > limit)
                    {
                        r.Fired = true;
                        r.FiredRunStart = r.Since.Value;
                        _crossed.Add(r);
                        _crossedIds.Add(r.Id);
                    }
                    break;
                case Sight.NotShown:
                    r.Since = null;
                    // An item that left the bar must be seen with a value again before it counts.
                    if (r.Item) r.EverReadable = false;
                    break;
            }
        }

        foreach (var r in _crossed)
        {
            if (_pending.Count == 0) _pendingUntil = now + TimeSpan.FromSeconds(_c.CombineSeconds);
            if (!r.Pending) { r.Pending = true; _pending.Add(r); }
        }
        if (_pending.Count > 0 && now >= _pendingUntil) Flush(now, frameWidth, frameHeight, events);
        UpdateShown();
    }

    Sight See(Row r)
    {
        if (_covered?.Invoke(r.Id) == true) return Sight.NotShown;   // the level-up panel's own message covers it
        string text = _readings.Get(r.Id).Text;
        if (text == RowReading.Unknown) return Sight.Unreadable;
        if (text == ItemDetector.GoneText) return Sight.NotShown;
        if (RowStats.TryValue(text, out _)) return Sight.Value;
        if (r.Id == AlertIds.InventoryFull && text != RowReading.None) return Sight.Value;   // ปกติ / เต็ม: the box was read
        if (r.Item && text == RowReading.None)
        {
            int hidden = _barHiddenPolls();
            if (hidden >= StableValue<int>.UnknownAfter) return Sight.Unreadable;   // whole bar not seen
            if (hidden == 0) return Sight.NotShown;                                 // bar seen, item not on it
            return Sight.Hold;                                                      // bar just went: wait
        }
        return text == RowReading.None ? Sight.NotShown : Sight.Hold;
    }

    void Flush(DateTime now, int w, int h, List<WatchEvent> events)
    {
        _pending.Sort((a, b) => Array.IndexOf(_rows, a).CompareTo(Array.IndexOf(_rows, b)));
        var rows = new List<UnreadableRow>(_pending.Count);
        foreach (var r in _pending)
        {
            r.Pending = false;
            double secs = r.Since is { } s ? (now - s).TotalSeconds : 0;
            rows.Add(new UnreadableRow(r.Id, r.Name, Math.Round(secs, 1)));
        }
        _pending.Clear();
        string names = string.Join(", ", rows.Select(x => x.Name));
        events.Add(new WatchEvent(WatchEventKind.Hud, string.Format(_c.Message, names), now, AlertIds.Unreadable,
            new Rectangle(0, 0, w, h), Unreadable: rows));
    }

    /// <summary>Frames stopped arriving (pause, minimised, capture failed, game lost): every run
    /// starts again, a message not sent yet is dropped (its rows may fire again later).</summary>
    public void Interrupt()
    {
        foreach (var r in _rows)
        {
            // A row already reported keeps its run (it re-arms only on a value); the others start again.
            if (r.Pending) { r.Pending = false; r.Fired = false; }
            if (!r.Fired) r.Since = null;
        }
        _pending.Clear();
        _crossedIds.Clear();
        Show(RowReading.None);
    }

    /// <summary>The game window is gone: a new game session starts when it is back (rows must
    /// show a value again before they count). Only the existing "เกมส์หลุด" alert is sent.</summary>
    public void EndSession()
    {
        Interrupt();
        foreach (var r in _rows) r.EverReadable = r.Fired = false;
    }

    void UpdateShown()
    {
        string? text = null;
        foreach (var r in _rows)
            if (r.Fired && r.Since is not null) text = text is null ? r.Name : text + ", " + r.Name;
        Show(text ?? "ปกติ");
    }

    void Show(string text)
    {
        if (text == _shown) return;
        _shown = text;
        _readings.Set(AlertIds.Unreadable, text);
    }
}
