using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>One poll of the kill counter: a number, no panel ("gone"), or unreadable.</summary>
public readonly record struct KillObservation(bool Readable, int? Value)
{
    public static readonly KillObservation Unreadable = new(false, null);
    public static readonly KillObservation Gone = new(true, null);
    public static KillObservation Of(int value) => new(true, value);
}

/// <summary>
/// Kill counter (counts to 300; the 300 itself shows only briefly, then the panel goes away).
/// The alert fires once when a confirmed reading reaches Target, or when the panel is gone for
/// StablePolls polls while the last confirmed value was at least NearTarget. It never fires when
/// the panel disappears below that (landing, town, death). Re-arms when the panel shows a
/// confirmed value below RearmBelow.
/// The counter can go up every second in combat, so "stable" here means: equal to the previous
/// poll's reading, or continuing it upwards by at most MaxStepPerPoll.
/// </summary>
public sealed class KillDetector : IHudDetector, IArmedState
{
    readonly KillConfig _c;
    readonly DigitFieldReader _reader;
    readonly CollectRegion _crop;
    readonly HudReadings _readings;
    int? _previous;      // last poll's reading, when it was a number
    int? _confirmed;     // last confirmed value while the panel is (or was just) showing
    int _goneRun, _unreadableRun;
    bool _armed = true;
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

    /// <summary>Alert text: the configured message plus the last number seen on the panel.</summary>
    public static string MessageWithLastSeen(string message, int lastSeen) => $"{message} (เลขล่าสุดที่เห็น {lastSeen})";

    /// <summary>Polling thread: the last poll's observation (EXP test kill sum).</summary>
    public KillObservation LastObservation { get; private set; } = KillObservation.Unreadable;

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events)
    {
        var o = LastObservation = Observe(_reader.Read(frame, scale));
        var rect = _crop.Locate(frame.Width, frame.Height, scale);
        if (o.Value is { } v && !rect.IsEmpty) KeepGoodCrop(frame, rect, v);
        if (!Update(o)) return;
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

    /// <summary>Logic only (tests): one poll. Returns true when the alert fires.</summary>
    public bool Update(KillObservation o)
    {
        bool fire = false;
        if (!o.Readable)
        {
            // Breaks every run; the last confirmed value is kept (the panel may just be covered).
            _previous = null;
            _goneRun = 0;
            _unreadableRun++;
        }
        else if (o.Value is { } v)
        {
            _unreadableRun = _goneRun = 0;
            if (_previous is { } p && v >= p && v - p <= _c.MaxStepPerPoll)
            {
                _confirmed = v;
                if (v < _c.RearmBelow) _armed = true;
                else if (v >= _c.Target && _armed) { _armed = false; fire = true; }
            }
            _previous = v;
        }
        else
        {
            _unreadableRun = 0;
            _previous = null;
            if (++_goneRun >= _c.StablePolls)
            {
                if (_confirmed >= _c.NearTarget && _armed) { _armed = false; fire = true; }
                _confirmed = null;
            }
        }

        // "ตอนนี้": the live number; "—" while the panel is not shown.
        if (o.Value is { } shown) _readings.Set(AlertIds.Kill300, shown.ToString(), shown >= _c.Target);
        else if (o.Readable) { if (_goneRun >= _c.StablePolls) _readings.Set(AlertIds.Kill300, RowReading.None); }
        else if (_unreadableRun >= StableValue<int>.UnknownAfter) _readings.Set(AlertIds.Kill300, RowReading.Unknown);
        return fire;
    }

    public void CopyArmed(IDictionary<string, bool> into) => into[AlertIds.Kill300] = _armed;

    public void Reset()
    {
        LastObservation = KillObservation.Unreadable;
        _haveGood = false;
        _previous = _confirmed = null;
        _goneRun = _unreadableRun = 0;
        _readings.Set(AlertIds.Kill300, RowReading.None);
    }
}
