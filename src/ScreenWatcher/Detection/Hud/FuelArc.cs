using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>Result of one look at the fuel arc. <c>Ok</c> false = unreadable. <c>Lit</c> = how
/// many of the arc's segments are lit, counted from the pump end.</summary>
public readonly record struct FuelReading(bool Ok, int Lit, int Segments)
{
    public override string ToString() => Ok ? $"{Lit}/{Segments} segments" : "?";
}

/// <summary>
/// Reads the orange fuel arc on the gauge's outer ring (top of the ring, clockwise to the pump
/// icon). The game draws it in whole segments: the part next to the pump is lit, the part towards
/// the top is dark, and the first three lit segments are dimmer steps. Each segment is probed at a
/// few points and must be clearly "lit" or the arc's own dark brown; anything else (gauge hidden,
/// something drawn over it) makes the reading unreadable. The "xxx/300" text is never read.
/// </summary>
public sealed class FuelArcReader(FuelConfig config)
{
    enum Seg { Other, Dark, Lit }

    public FuelReading Read(Frame frame, HudScale scale)
    {
        var s = HudLayout.Snap(scale);
        int n = config.Segments, lit = 0;
        bool darkSeen = false;
        // Segment 0 touches the pump; walk towards the top of the ring.
        for (int j = 0; j < n; j++)
        {
            double end = config.PumpEndDeg - j * config.SegmentDeg, start = end - config.SegmentDeg;
            // Keep clear of the segment borders, of the bright cap at the pump end and of the
            // junction with the cyan ring at the very top.
            double a0 = Math.Max(start + 1.5, config.TopClearDeg), a1 = Math.Min(end - 1.5, config.PumpEndDeg - config.PumpCapDeg);
            int litVotes = 0, darkVotes = 0, votes = 0;
            for (int k = 0; k < config.ProbesPerSegment; k++)
            {
                double deg = config.ProbesPerSegment == 1 ? (a0 + a1) / 2 : a0 + (a1 - a0) * k / (config.ProbesPerSegment - 1);
                double rad = deg * Math.PI / 180, sin = Math.Sin(rad), cos = Math.Cos(rad);
                foreach (double r in config.Radii)
                {
                    // 1.0-layout continuous coordinates (pixel i spans [i, i+1)) -> native pixel.
                    int x = (int)Math.Floor((config.Center[0] + r * sin) * s.X);
                    int y = (int)Math.Floor((config.Center[1] - r * cos) * s.Y);
                    if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) return new FuelReading(false, 0, n);
                    votes++;
                    var c = frame.Pixel(x, y);
                    if (IsLit(c)) litVotes++;
                    else if (IsDark(c)) darkVotes++;
                }
            }
            // The fuel text crosses one or two segments, so a clear majority is enough.
            Seg seg = litVotes >= votes * config.MinVoteShare ? Seg.Lit : darkVotes >= votes * config.MinVoteShare ? Seg.Dark : Seg.Other;
            if (seg == Seg.Other) return new FuelReading(false, 0, n);
            if (seg == Seg.Lit)
            {
                if (darkSeen) return new FuelReading(false, 0, n); // lit beyond a dark segment: not the arc
                lit++;
            }
            else darkSeen = true;
        }
        return new FuelReading(true, lit, n);
    }

    bool IsLit(Rgb c) =>
        c.R >= config.LitMinR && c.B <= config.LitMaxB && c.G >= c.R * config.HueMinGOverR && c.G <= c.R * config.HueMaxGOverR;

    bool IsDark(Rgb c) =>
        c.R >= config.DarkR[0] && c.R <= config.DarkR[1] && c.G >= config.DarkG[0] && c.G <= config.DarkG[1] && c.B <= config.DarkMaxB;

    /// <summary>Estimated fuel % for a number of lit segments: the middle of the range that
    /// segment count stands for (the arc cannot show more detail than one segment).</summary>
    public int Percent(int lit)
    {
        var (lo, hi) = PercentRange(lit);
        return (int)Math.Round((lo + hi) / 2);
    }

    /// <summary>Fuel % range a number of lit segments stands for.</summary>
    public (double Low, double High) PercentRange(int lit)
    {
        if (lit <= 0) return (0, config.PercentAtNoSegments);
        if (lit >= config.Segments) return (config.PercentAtNoSegments + (config.Segments - 1) * config.PercentPerSegment, 100);
        return (config.PercentAtNoSegments + (lit - 1) * config.PercentPerSegment, config.PercentAtNoSegments + lit * config.PercentPerSegment);
    }
}

/// <summary>
/// Fuel alert: fires once when the estimated % is at or below the row threshold on StablePolls
/// consecutive polls; re-arms when the estimate is at least RearmMarginPercent above the threshold.
/// </summary>
public sealed class FuelDetector(WatcherConfig config, HudReadings readings, HudOptions options) : IHudDetector, IArmedState
{
    readonly FuelConfig _c = config.Fuel;
    readonly FuelArcReader _reader = new(config.Fuel);
    readonly CollectRegion _crop = HudCrop.Region(config, config.Fuel.CropRegion);
    readonly StableValue<int> _stable = new(config.Fuel.StablePolls);
    bool _armed = true;

    public FuelArcReader Reader => _reader;

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events)
    {
        var r = _reader.Read(frame, scale);
        if (Update(r.Ok ? r.Lit : null) is { } message)
            events.Add(new WatchEvent(WatchEventKind.Hud, message, now, AlertIds.FuelLow, _crop.Locate(frame.Width, frame.Height, scale)));
    }

    /// <summary>Logic only (tests): one poll's lit-segment count (null = unreadable). Returns the
    /// alert text when the alert fires.</summary>
    public string? Update(int? litSegments)
    {
        string? fire = null;
        int threshold = options.Threshold(AlertIds.FuelLow, _c.DefaultThresholdPercent);
        if (_stable.Push(litSegments))
        {
            int pct = _reader.Percent(_stable.Value!.Value);
            if (pct >= threshold + _c.RearmMarginPercent) _armed = true;
            else if (pct <= threshold && _armed)
            {
                _armed = false;
                fire = string.Format(_c.Message, pct);
            }
        }
        if (_stable.Value is { } lit)
        {
            int pct = _reader.Percent(lit);
            readings.Set(AlertIds.FuelLow, lit >= _c.Segments ? $">{(int)_reader.PercentRange(lit).Low}%" : $"~{pct}%", pct <= threshold);
        }
        else readings.Set(AlertIds.FuelLow, RowReading.Unknown);
        return fire;
    }

    public void CopyArmed(IDictionary<string, bool> into) => into[AlertIds.FuelLow] = _armed;

    public void Reset()
    {
        _stable.Reset();
        readings.Set(AlertIds.FuelLow, RowReading.None);
    }
}
