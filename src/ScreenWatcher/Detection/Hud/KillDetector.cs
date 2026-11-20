using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>One poll of the kill counter: a number, no panel ("gone": no digits), unreadable
/// digits, or <see cref="Covered"/> (the game's tooltip text lies over the field).</summary>
public readonly record struct KillObservation(bool Readable, int? Value, bool Covered = false)
{
    public static readonly KillObservation Unreadable = new(false, null);
    public static readonly KillObservation Gone = new(true, null);
    public static readonly KillObservation CoveredByText = new(false, null, true);
    public static KillObservation Of(int value) => new(true, value);
}

/// <summary>
/// Kill counter (counts to 300). The panel appears only while an EXP card is in use; after 300 it
/// goes away and stays away until the next card (owner, 2026-11-20; field event 13:27: the right
/// alert, then a false "อ่านค่าไม่ได้ : นับมอน" because the panel never came back).
/// - Done alert, once: when a confirmed reading reaches Target, or when the panel has been absent
///   for AbsentSeconds in a row while the last confirmed value was at least NearTarget. Tooltip
///   text over the field restarts that wait.
/// - Absent = no digits, or digits that cannot be read (dark or busy scenery), without tooltip text.
/// - "ตอนนี้": the live number; "ครบแล้ว" after the alert while the panel is away; "ไม่มี" when there
///   is no panel and nothing at NearTarget or more was pending (no card used); "?" only while
///   tooltip text covers the field of a panel that was showing (the only way into "อ่านค่าไม่ได้").
/// - Re-arms when the panel shows a confirmed value below RearmBelow (a new card starts at 0).
/// "Confirmed": equal to the previous poll's reading or continuing it upwards by at most
/// MaxStepPerPoll (the counter can go up every second in combat).
/// </summary>
public sealed class KillDetector : IHudDetector, IArmedState
{
    public const string DoneText = "ครบแล้ว";
    public const string NoPanelText = "ไม่มี";

    readonly KillConfig _c;
    readonly DigitFieldReader _reader;
    readonly CollectRegion _crop;
    readonly HudReadings _readings;
    int? _previous;      // last poll's reading, when it was a number
    int? _lastSeen;      // last confirmed value of the panel showing now (or until it went away)
    DateTime? _absentSince;
    bool _armed = true, _done;
    // Last crop of the kill region in which a number was read (buffer reused; copied when an alert fires).
    byte[] _goodPx = [];
    int _goodW, _goodH, _goodValue;
    bool _haveGood;

    public KillDetector(WatcherConfig config, IReadOnlyDictionary<string, GlyphSet> glyphs, HudReadings readings)
    {
        _c = config.Kill;
        _reader = new DigitFieldReader(_c.Field, glyphs[_c.Field.Glyphs]);
        _crop = HudCrop.Region(config, _c.CropRegion);
        _readings = readings;
    }

    public DigitReading ReadRaw(Frame frame, HudScale scale) => _reader.Read(frame, scale);

    public static KillObservation Observe(DigitReading r) =>
        !r.Ok ? KillObservation.Unreadable : r.Digits == 0 ? KillObservation.Gone : KillObservation.Of(r.Value);

    /// <summary>One frame's observation: a field without a readable number is "covered" when the
    /// game's tooltip text lies over it; otherwise the reader's own result.</summary>
    public KillObservation Observe(Frame frame, HudScale scale)
    {
        var o = Observe(_reader.Read(frame, scale));
        return o.Value is null && TooltipTextPixels(frame, _reader.Bounds(frame, scale)) >= _c.CoverTextMin ? KillObservation.CoveredByText : o;
    }

    /// <summary>Pixels of the game's light-grey UI text (CoverText range per channel) next to a dark
    /// outline (every channel at most CoverOutlineMax) inside the digit field.</summary>
    public int TooltipTextPixels(Frame frame, HudScale scale) => TooltipTextPixels(frame, _reader.Bounds(frame, scale));

    int TooltipTextPixels(Frame f, System.Drawing.Rectangle r)
    {
        var px = f.Pixels;
        int[] t = _c.CoverText ?? [215, 229];
        int text = 0;
        for (int y = Math.Max(1, r.Top); y < Math.Min(f.Height - 1, r.Bottom); y++)
            for (int x = Math.Max(1, r.Left); x < Math.Min(f.Width - 1, r.Right); x++)
            {
                int i = (y * f.Width + x) * 4;
                if (InRange(px[i + 2], t) && InRange(px[i + 1], t) && InRange(px[i], t)
                    && (Dark(px, i - 4) || Dark(px, i + 4) || Dark(px, i - f.Width * 4) || Dark(px, i + f.Width * 4))) text++;
            }
        return text;

        static bool InRange(int v, int[] range) => v >= range[0] && v <= range[1];
        bool Dark(ReadOnlySpan<byte> p, int i) => p[i] <= _c.CoverOutlineMax && p[i + 1] <= _c.CoverOutlineMax && p[i + 2] <= _c.CoverOutlineMax;
    }

    /// <summary>Alert text: the configured message plus the last number seen on the panel.</summary>
    public static string MessageWithLastSeen(string message, int lastSeen) => $"{message} (เลขล่าสุดที่เห็น {lastSeen})";

    /// <summary>Polling thread: the last poll's observation (EXP test kill sum).</summary>
    public KillObservation LastObservation { get; private set; } = KillObservation.Unreadable;

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events)
    {
        var o = LastObservation = Observe(frame, scale);
        var rect = _crop.Locate(frame.Width, frame.Height, scale);
        if (o.Value is { } v && !rect.IsEmpty) KeepGoodCrop(frame, rect, v);
        if (!Update(o, now)) return;
        // The panel is usually gone when the alert fires: send the last crop that showed the number.
        var image = LastGoodCrop;
        string text = image is null ? _c.Message : MessageWithLastSeen(_c.Message, image.Value);
        events.Add(new WatchEvent(WatchEventKind.Hud, text, now, AlertIds.Kill300, rect, image));
    }

    /// <summary>The last kept crop in which the number was read (null when none).</summary>
    public CropImage? LastGoodCrop =>
        _haveGood ? new CropImage(_goodPx.AsSpan(0, _goodW * _goodH * 4).ToArray(), _goodW, _goodH, _goodValue) : null;

    void KeepGoodCrop(Frame frame, System.Drawing.Rectangle rect, int value)
    {
        int n = rect.Width * rect.Height * 4;
        if (_goodPx.Length < n) _goodPx = new byte[n];
        frame.CopyRect(rect, _goodPx);
        (_goodW, _goodH, _goodValue, _haveGood) = (rect.Width, rect.Height, value, true);
    }

    /// <summary>Logic only (tests): one poll at <paramref name="now"/>. Returns true when the alert fires.</summary>
    public bool Update(KillObservation o, DateTime now)
    {
        bool fire = false;
        if (o.Value is { } v)
        {
            _absentSince = null;
            if (_previous is { } p && v >= p && v - p <= _c.MaxStepPerPoll)
            {
                _lastSeen = v;
                if (v < _c.RearmBelow) { _armed = true; _done = false; }
                else if (v >= _c.Target && _armed) { _armed = false; _done = fire = true; }
            }
            _previous = v;
            _readings.Set(AlertIds.Kill300, v.ToString(), v >= _c.Target);
            return fire;
        }

        _previous = null;
        if (o.Covered)
        {
            // Tooltip text over the field: no news about the panel, and the absence wait starts again.
            _absentSince = null;
            if (_lastSeen is not null) _readings.Set(AlertIds.Kill300, RowReading.Unknown);
            return false;
        }

        // No panel: no digits, or digits that cannot be read (scenery).
        _absentSince ??= now;
        if (_lastSeen is { } last && (now - _absentSince.Value).TotalSeconds >= _c.AbsentSeconds)
        {
            if (last >= _c.NearTarget && _armed) { _armed = false; _done = fire = true; }
            _lastSeen = null;
        }
        if (_lastSeen is null) _readings.Set(AlertIds.Kill300, _done ? DoneText : NoPanelText);
        else if (_readings.Get(AlertIds.Kill300).Text == RowReading.Unknown)
            _readings.Set(AlertIds.Kill300, _lastSeen.Value.ToString(), _lastSeen >= _c.Target);   // tooltip gone: last value again
        return fire;
    }

    public void CopyArmed(IDictionary<string, bool> into) => into[AlertIds.Kill300] = _armed;

    /// <summary>Frames stopped: runs start again; the panel state (last value, done) is kept.</summary>
    public void Reset()
    {
        LastObservation = KillObservation.Unreadable;
        _previous = null;
        _absentSince = null;
        _readings.Set(AlertIds.Kill300, RowReading.None);
    }
}
