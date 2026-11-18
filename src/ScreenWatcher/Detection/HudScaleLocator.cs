using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection;

/// <summary>HUD scale relative to the 1.0 layout in detectors.json. X and Y can differ slightly
/// when the OS squeezes a window that is larger than the screen.</summary>
public readonly record struct HudScale(double X, double Y)
{
    public static readonly HudScale One = new(1, 1);

    /// <summary>Maps a 1.0-layout pixel index to this scale (pixel centres scale about the client origin).</summary>
    public int MapX(double x) => (int)Math.Round((x + 0.5) * X - 0.5);
    public int MapY(double y) => (int)Math.Round((y + 0.5) * Y - 0.5);
    public int SizeX(int w) => Math.Max(1, (int)Math.Round(w * X));
    public int SizeY(int h) => Math.Max(1, (int)Math.Round(h * Y));

    public override string ToString() =>
        Math.Abs(X - Y) < 0.01 ? $"{(X + Y) / 2:F2}" : $"{X:F2}x{Y:F2}";
}

/// <summary>Result of one ring search. Ring diameters are the measured outer size in pixels.</summary>
public readonly record struct HudScaleMeasurement(bool Found, HudScale Scale, double Score, double PeakToMean, double RingWidthPx, double RingHeightPx);

/// <summary>
/// Measures the HUD scale by locating the outer ring of the HP gauge near the client top-left.
/// The game scales its HUD about the client origin, so the ring at scale s has its centre at
/// (centre * s) and outer radius (outerRadius * s). For each candidate scale the ring's outer and
/// inner boundaries are checked for a colour edge all the way round. This uses colour
/// differences, not specific colours, so it works whether the ring is cyan, red (low HP / dead)
/// or flashing. A coarse uniform search is refined separately for X and Y.
/// Allocates only its angle tables, once.
/// </summary>
public sealed class HudScaleLocator
{
    readonly HudScaleConfig _c;
    readonly double[] _cos, _sin;

    public HudScaleLocator(HudScaleConfig config)
    {
        _c = config;
        _cos = new double[config.Angles];
        _sin = new double[config.Angles];
        for (int i = 0; i < config.Angles; i++)
        {
            double a = 2 * Math.PI * i / config.Angles;
            _cos[i] = Math.Cos(a);
            _sin[i] = Math.Sin(a);
        }
    }

    public HudScaleMeasurement Measure(Frame frame)
    {
        // Coarse: same scale on both axes.
        double best = double.MinValue, bestS = 1, sum = 0;
        int n = 0;
        for (double s = _c.MinScale; s <= _c.MaxScale + 1e-9; s += _c.CoarseStep)
        {
            double sc = Score(frame, s, s);
            if (double.IsNaN(sc)) continue;
            sum += sc;
            n++;
            if (sc > best) { best = sc; bestS = s; }
        }
        if (n == 0) return new HudScaleMeasurement(false, HudScale.One, 0, 0, 0, 0);
        double mean = sum / n;

        // Refine X then Y then X again around the coarse peak.
        double sx = bestS, sy = bestS;
        for (int pass = 0; pass < 3; pass++)
        {
            bool xAxis = pass != 1;
            double centre = xAxis ? sx : sy, bestAxis = centre, bestScore = double.MinValue;
            for (double d = -_c.RefineRange; d <= _c.RefineRange + 1e-9; d += _c.RefineStep)
            {
                double v = centre + d;
                double sc = xAxis ? Score(frame, v, sy) : Score(frame, sx, v);
                if (!double.IsNaN(sc) && sc > bestScore) { bestScore = sc; bestAxis = v; }
            }
            if (xAxis) sx = bestAxis; else sy = bestAxis;
            best = bestScore;
        }

        double ratio = mean > 0 ? best / mean : 0;
        bool found = best >= _c.MinScore && ratio >= _c.MinPeakToMean;
        var scale = new HudScale(sx, sy);
        return new HudScaleMeasurement(found, scale, best, ratio, 2 * _c.RingOuterRadius * sx, 2 * _c.RingOuterRadius * sy);
    }

    /// <summary>Mean edge strength (sum of |dR|+|dG|+|dB|) across the ring's outer and inner
    /// boundaries at the given scale. NaN if the ring does not fit in the frame.</summary>
    public double Score(Frame frame, double sx, double sy)
    {
        double cx = _c.RingCenter[0] * sx, cy = _c.RingCenter[1] * sy;
        double ro = _c.RingOuterRadius, ri = _c.RingInnerRadius;
        if ((cx + ro * sx + 3) >= frame.Width || (cy + ro * sy + 3) >= frame.Height) return double.NaN;

        // Probe +-EdgeProbePx (screen pixels) across each boundary, expressed in 1.0-layout units.
        double d = _c.EdgeProbePx / ((sx + sy) / 2);
        double total = 0;
        for (int i = 0; i < _cos.Length; i++)
        {
            double c = _cos[i], s = _sin[i];
            total += Diff(frame, cx, cy, sx, sy, c, s, ro - d, ro + d);
            total += Diff(frame, cx, cy, sx, sy, c, s, ri + d, ri - d);
        }
        return total / (2 * _cos.Length);
    }

    static double Diff(Frame f, double cx, double cy, double sx, double sy, double c, double s, double r1, double r2)
    {
        var a = Sample(f, cx + r1 * c * sx, cy + r1 * s * sy);
        var b = Sample(f, cx + r2 * c * sx, cy + r2 * s * sy);
        return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    }

    /// <summary>Bilinear sample at continuous client coordinates (pixel i spans [i, i+1)).</summary>
    static Rgb Sample(Frame f, double x, double y)
    {
        x -= 0.5;
        y -= 0.5;
        int x0 = Math.Clamp((int)Math.Floor(x), 0, f.Width - 2), y0 = Math.Clamp((int)Math.Floor(y), 0, f.Height - 2);
        double fx = Math.Clamp(x - x0, 0, 1), fy = Math.Clamp(y - y0, 0, 1);
        var p00 = f.Pixel(x0, y0); var p10 = f.Pixel(x0 + 1, y0);
        var p01 = f.Pixel(x0, y0 + 1); var p11 = f.Pixel(x0 + 1, y0 + 1);
        double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        return new Rgb(
            p00.R * w00 + p10.R * w10 + p01.R * w01 + p11.R * w11,
            p00.G * w00 + p10.G * w10 + p01.G * w01 + p11.G * w11,
            p00.B * w00 + p10.B * w10 + p01.B * w01 + p11.B * w11);
    }
}

/// <summary>
/// Keeps the current HUD scale. Measures when the client size changes; if the ring is not found
/// it keeps the last good scale and retries every few polls.
/// </summary>
public sealed class HudScaleTracker(HudScaleConfig config)
{
    readonly HudScaleLocator _locator = new(config);
    int _measuredW, _measuredH, _pollsSinceTry;

    public HudScale Scale { get; private set; } = HudScale.One;
    /// <summary>True when the scale in use was measured at the current client size.</summary>
    public bool Current { get; private set; }
    public bool EverFound { get; private set; }
    public HudScaleMeasurement? Last { get; private set; }

    public HudScale Update(Frame frame)
    {
        bool sizeChanged = frame.Width != _measuredW || frame.Height != _measuredH;
        if (sizeChanged)
        {
            Current = false;
            _measuredW = frame.Width;
            _measuredH = frame.Height;
            _pollsSinceTry = config.RetryEveryPolls; // try now
        }
        if (Current) return Scale;

        if (++_pollsSinceTry < config.RetryEveryPolls) return Scale;
        _pollsSinceTry = 0;

        var m = _locator.Measure(frame);
        Last = m;
        if (m.Found)
        {
            Scale = m.Scale;
            Current = true;
            EverFound = true;
        }
        return Scale;
    }

    public string Describe() =>
        Current ? $"scale {Scale}"
        : EverFound ? $"scale {Scale} (ring not found, using last good)"
        : $"scale {Scale} (ring not found, default)";
}
