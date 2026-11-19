using System.Globalization;

namespace ScreenWatcher.Diagnostics;

/// <summary>One row's figures for one clock hour.</summary>
public sealed record RowHour(DateTime Hour, string Row, int Polls, int Reads, int Unknown, int? Min, int? Max);

/// <summary>
/// Per HUD row, per clock hour: polls (with a captured frame), successful reads (a number, a fuel
/// "~NN%" / "&gt;NN%", or the item row's "หมด" counted as 0), "?" count, and min / max value.
/// Fed once per poll on the polling thread; no allocation per poll.
/// </summary>
public sealed class RowStats
{
    sealed class Acc
    {
        public int Polls, Reads, Unknown;
        public int Min = int.MaxValue, Max = int.MinValue;
        public void Clear() { Polls = Reads = Unknown = 0; Min = int.MaxValue; Max = int.MinValue; }
    }

    readonly string[] _rows;
    readonly Acc[] _acc;
    DateTime? _hour;

    public RowStats(IEnumerable<string> rowIds)
    {
        _rows = rowIds.ToArray();
        _acc = _rows.Select(_ => new Acc()).ToArray();
    }

    public IReadOnlyList<string> Rows => _rows;

    static DateTime HourOf(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);

    /// <summary>Starts a poll at <paramref name="now"/>. When the clock hour changed, the finished
    /// hour's rows are returned (and the counters cleared); otherwise null.</summary>
    public List<RowHour>? BeginPoll(DateTime now)
    {
        var h = HourOf(now);
        List<RowHour>? done = null;
        if (_hour is { } cur && cur != h) done = Flush();
        _hour = h;
        return done;
    }

    /// <summary>Adds one row's reading for the current poll (call after <see cref="BeginPoll"/>).</summary>
    public void Add(int rowIndex, string text)
    {
        var a = _acc[rowIndex];
        a.Polls++;
        if (text == "?") { a.Unknown++; return; }
        if (TryValue(text, out int v))
        {
            a.Reads++;
            if (v < a.Min) a.Min = v;
            if (v > a.Max) a.Max = v;
        }
    }

    /// <summary>The current hour's figures (rows with at least one poll), then cleared.</summary>
    public List<RowHour> Flush()
    {
        var list = new List<RowHour>();
        if (_hour is not { } h) return list;
        for (int i = 0; i < _rows.Length; i++)
        {
            var a = _acc[i];
            if (a.Polls == 0) continue;
            list.Add(new RowHour(h, _rows[i], a.Polls, a.Reads, a.Unknown, a.Reads > 0 ? a.Min : null, a.Reads > 0 ? a.Max : null));
            a.Clear();
        }
        return list;
    }

    /// <summary>The number a row's "ตอนนี้" text stands for: "123", "~25%", "&gt;90%", "หมด" (0).
    /// False for "—", "?" and anything else.</summary>
    public static bool TryValue(string text, out int value)
    {
        value = 0;
        if (text == Detection.Hud.ItemDetector.GoneText) return true;
        var s = text.AsSpan();
        if (s.StartsWith("Lv.")) s = s[3..];
        if (s.Length > 0 && (s[0] == '~' || s[0] == '>')) s = s[1..];
        if (s.Length > 0 && s[^1] == '%') s = s[..^1];
        return s.Length > 0 && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
