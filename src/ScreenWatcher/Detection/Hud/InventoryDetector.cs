using System.Drawing;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>
/// Row "ช่องเก็บของเต็ม". The first time the phrase is seen in the system message box: one alert
/// with a crop of the box, then the row stays latched (no more alerts, "ตอนนี้" = เต็ม (รอรีเซ็ต))
/// until the user resets it (the row's "รีเซ็ต" button or /reset in Discord); the app starts
/// un-latched. After a reset the old line may still be in the box, so the row watches again once
/// the phrase has been gone for MissingPolls polls. "?" when the framed box has not been seen for MissingPolls polls (the
/// "อ่านค่าไม่ได้" row reports it as "กล่องข้อความระบบ"); a frameless box without text gives no news.
/// </summary>
public sealed class InventoryDetector : IHudDetector, IArmedState
{
    readonly InventoryConfig _c;
    readonly SystemMessageReader _reader;
    readonly CollectRegion _crop;
    readonly HudReadings _readings;
    volatile bool _resetRequested;
    bool _latched, _framed, _seenOnce, _waitClear;
    int _missing, _clearRun;
    string _shown = RowReading.None;

    public InventoryDetector(WatcherConfig config, HudReadings readings)
    {
        _c = config.Inventory;
        _reader = new SystemMessageReader(_c);
        _crop = HudCrop.Region(config, _c.CropRegion);
        _readings = readings;
        Show(RowReading.None);
    }

    public SystemMessageReader Reader => _reader;

    /// <summary>Latched ("full, waiting for reset").</summary>
    public bool Latched => _latched;

    /// <summary>Any thread (UI button, Discord /reset): un-latch on the next poll.</summary>
    public void RequestReset() => _resetRequested = true;

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events) =>
        Update(_reader.Read(frame, scale), now, _crop.Locate(frame.Width, frame.Height, scale), events);

    /// <summary>Logic for one poll's reading (also called directly by tests).</summary>
    public void Update(SystemMessageReading r, DateTime now, Rectangle crop, List<WatchEvent> events)
    {
        if (_resetRequested) { _resetRequested = false; _latched = false; _clearRun = 0; _waitClear = true; }
        // After a reset the old line may still be in the box: watch again once it has gone.
        if (_waitClear && (r.Phrase ? _clearRun = 0 : ++_clearRun) >= _c.MissingPolls) _waitClear = false;
        if (r.Phrase && !_latched && !_waitClear)
        {
            _latched = true;
            events.Add(new WatchEvent(WatchEventKind.Hud, _c.Message, now, AlertIds.InventoryFull, crop));
        }

        string status = _latched ? _c.FullText : _c.OkText;
        if (r.Title || r.Phrase) { _framed = r.Title || _framed; _seenOnce = true; _missing = 0; Show(status); }
        else if (r.Text) { _framed = false; _seenOnce = true; _missing = 0; Show(status); }
        else if (_framed)
        {
            // The framed box is gone (closed or covered).
            if (++_missing >= _c.MissingPolls) Show(RowReading.Unknown);
        }
        else if (_seenOnce && _shown != RowReading.None) Show(status);   // frameless and empty: no news
    }

    void Show(string text)
    {
        if (text == _shown) return;
        _shown = text;
        _readings.Set(AlertIds.InventoryFull, text, _latched);
    }

    public void CopyArmed(IDictionary<string, bool> into) => into[AlertIds.InventoryFull] = !_latched;

    /// <summary>Frames stopped: the row goes blank; the latch is kept (only the user resets it).</summary>
    public void Reset()
    {
        _missing = 0;
        Show(RowReading.None);
    }
}
