using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>
/// Machine-gun and missile counters. A counter is empty when it reads 00000 by digit shape on
/// StablePolls consecutive polls; the alert fires once and re-arms when the counter reads above 0.
/// </summary>
public sealed class AmmoDetector : IHudDetector
{
    sealed class Counter(string id, AmmoCounterConfig config, GlyphSet glyphs, int stablePolls)
    {
        public readonly string Id = id;
        public readonly AmmoCounterConfig Config = config;
        public readonly DigitFieldReader Reader = new(config.Field, glyphs);
        public readonly StableValue<int> Stable = new(stablePolls);
        public bool Armed = true;
    }

    readonly Counter[] _counters;
    readonly CollectRegion _crop;
    readonly HudReadings _readings;

    public AmmoDetector(WatcherConfig config, IReadOnlyDictionary<string, GlyphSet> glyphs, HudReadings readings)
    {
        var a = config.Ammo;
        _counters =
        [
            new Counter(AlertIds.GunAmmoEmpty, a.Gun, glyphs[a.Gun.Field.Glyphs], a.StablePolls),
            new Counter(AlertIds.MissileEmpty, a.Missile, glyphs[a.Missile.Field.Glyphs], a.StablePolls),
        ];
        _crop = HudCrop.Region(config, a.CropRegion);
        _readings = readings;
    }

    /// <summary>The raw (unconfirmed) reading of each counter on this frame.</summary>
    public (DigitReading Gun, DigitReading Missile) ReadRaw(Frame frame, HudScale scale) =>
        (_counters[0].Reader.Read(frame, scale), _counters[1].Reader.Read(frame, scale));

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events)
    {
        foreach (var c in _counters)
        {
            var r = c.Reader.Read(frame, scale);
            if (Update(c, r.Ok ? r.Value : null))
                events.Add(new WatchEvent(WatchEventKind.Hud, c.Config.Message, now, c.Id, _crop.Locate(frame.Width, frame.Height, scale)));
        }
    }

    /// <summary>Logic only (tests): one poll for a counter (0 = gun, 1 = missile; null =
    /// unreadable). Returns true when the "empty" alert fires.</summary>
    public bool Update(int counter, int? value) => Update(_counters[counter], value);

    bool Update(Counter c, int? value)
    {
        bool fire = false;
        if (c.Stable.Push(value))
        {
            if (c.Stable.Value > 0) c.Armed = true;
            else if (c.Armed) { c.Armed = false; fire = true; }
        }
        if (c.Stable.Value is { } v) _readings.Set(c.Id, v.ToString(), v == 0);
        else _readings.Set(c.Id, RowReading.Unknown);
        return fire;
    }

    public void Reset()
    {
        foreach (var c in _counters)
        {
            c.Stable.Reset();
            _readings.Set(c.Id, RowReading.None);
        }
    }
}
