using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>
/// "เลเวลอัพ: หน้าเควสบังข้อมูลอยู่": the level-up quest panel (QuestPanelFinder) covers the HUD
/// rows in QuestPanelConfig.CoveredRows. The finder only runs while one of those rows shows "?"
/// (a covered row that reads a value means the panel is not over it), so it costs nothing in
/// normal play. When the panel has been seen for longer than the "อ่านค่าไม่ได้" limit, one message
/// with the full client frame is sent under that row (its checkbox switches it on and off), once
/// per appearance; it re-arms after GoneStablePolls polls without the panel. While the panel is
/// seen, the covered rows are left out of the generic "อ่านค่าไม่ได้" message (one message, not two).
/// </summary>
public sealed class QuestPanelDetector
{
    readonly QuestPanelConfig _c;
    readonly QuestPanelFinder _finder;
    readonly HudReadings _readings;
    readonly HudOptions _options;
    readonly Func<int> _limitSeconds;
    readonly HashSet<string> _covered;
    readonly Dictionary<string, string> _names;
    DateTime? _since;
    int _goneRun;
    bool _fired;

    /// <param name="limitSeconds">The "อ่านค่าไม่ได้" row's limit (UnreadableDetector.LimitSeconds).</param>
    public QuestPanelDetector(WatcherConfig config, HudReadings readings, HudOptions options, Func<int> limitSeconds, QuestPanelFinder? finder = null)
    {
        _c = config.QuestPanel;
        _finder = finder ?? new QuestPanelFinder(_c);
        _readings = readings;
        _options = options;
        _limitSeconds = limitSeconds;
        _covered = new HashSet<string>(_c.CoveredRows, StringComparer.Ordinal);
        _names = config.Unreadable.Rows.ToDictionary(r => r[0], r => r[1], StringComparer.Ordinal);
    }

    /// <summary>The panel was seen on the last poll.</summary>
    public bool Present => _since is not null;

    /// <summary>True when <paramref name="rowId"/> is covered by the panel seen now: the generic
    /// "อ่านค่าไม่ได้" rule leaves it out.</summary>
    public bool Covers(string rowId) => _since is not null && _covered.Contains(rowId);

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events)
    {
        bool anyUnknown = false, anyValue = false;
        foreach (var id in _c.CoveredRows)
        {
            string t = _readings.Get(id).Text;
            anyUnknown |= t == RowReading.Unknown;
            anyValue |= Diagnostics.RowStats.TryValue(t, out _);
        }
        if (anyUnknown)
        {
            if (_finder.IsPresent(frame, scale)) Seen(now, frame.Width, frame.Height, events);
            else NotSeen();
        }
        else if (anyValue) NotSeen();   // a covered row reads a value: the panel is not over it
        // else: the rows show nothing yet (just resumed): no news either way.
    }

    /// <summary>One poll's result (also called directly by tests).</summary>
    public void Seen(DateTime now, int width, int height, List<WatchEvent> events)
    {
        _goneRun = 0;
        _since ??= now;
        if (_fired || (now - _since.Value).TotalSeconds <= _limitSeconds()) return;
        _fired = true;
        if (!_options.IsEnabled(AlertIds.Unreadable)) return;   // row off: nothing is sent, still "fired" for this appearance
        double secs = Math.Round((now - _since.Value).TotalSeconds, 1);
        var rows = _c.CoveredRows.Select(id => new UnreadableRow(id, _names.GetValueOrDefault(id, id), secs)).ToList();
        events.Add(new WatchEvent(WatchEventKind.Hud, _c.Message, now, AlertIds.Unreadable, new Rectangle(0, 0, width, height), Unreadable: rows));
    }

    public void NotSeen()
    {
        _since = null;
        if (++_goneRun >= _c.GoneStablePolls) _fired = false;
    }

    /// <summary>Frames stopped (pause, minimised, capture failed): the presence run starts again;
    /// an appearance already reported stays reported until the panel is seen gone.</summary>
    public void Interrupt() => _since = null;
}
