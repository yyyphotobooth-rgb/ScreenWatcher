using System.Drawing;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>Result of reading one fixed-cell number. <c>Ok</c> false = unreadable ("?").</summary>
/// <param name="Digits">How many digit cells were filled (0 = the field is blank).</param>
/// <param name="Score">Lowest per-digit match (normalised cross-correlation, 1 = perfect).</param>
/// <param name="Margin">Smallest lead of a chosen digit over the next best digit.</param>
/// <param name="Red">Ammo only: the digits are drawn in the red "empty" colours.</param>
public readonly record struct DigitReading(bool Ok, int Value, int Digits, double Score, double Margin, bool Red)
{
    public static readonly DigitReading Unreadable = new(false, 0, 0, 0, 0, false);
    public override string ToString() => Ok ? $"{Value} ({Digits}d, score {Score:F2}, margin {Margin:F2}{(Red ? ", red" : "")})" : $"? (score {Score:F2}, margin {Margin:F2})";
}

/// <summary>
/// Reads a number drawn in fixed-pitch cells (ammo counters, kill counter). Each cell is compared
/// with the ten digit templates by normalised cross-correlation of its "ink", at small offsets (the
/// measured HUD scale is only accurate to ~0.5 %, and the game rounds each digit's position). Cells are sampled at 1.0-layout positions mapped through the HUD scale, so a stretched
/// (blurred) HUD is read with the same templates. Allocates nothing per read.
/// </summary>
public sealed class DigitFieldReader
{
    readonly DigitFieldConfig _c;
    readonly GlyphSet _glyphs;
    readonly HudAnchor _anchor;
    readonly InkKind[] _inks;
    readonly InkBuffer _buffer = new();
    readonly float[] _cell;

    public DigitFieldReader(DigitFieldConfig config, GlyphSet glyphs)
    {
        _c = config;
        _glyphs = glyphs;
        _anchor = Enum.Parse<HudAnchor>(config.Anchor, ignoreCase: true);
        _inks = config.Inks.Select(i => Enum.Parse<InkKind>(i, ignoreCase: true)).ToArray();
        _cell = new float[glyphs.Width * glyphs.Height];
    }

    /// <summary>Native rectangle the field occupies (with the search margin), clipped to the frame.</summary>
    public Rectangle Bounds(Frame frame, HudScale scale)
    {
        var s = HudLayout.Snap(scale);
        double ax = HudLayout.AnchorX(_anchor, frame.Width), ay = HudLayout.AnchorY(_anchor, frame.Height);
        double x0 = ax + _c.CellX.Min() * s.X, x1 = ax + (_c.CellX.Max() + _glyphs.Width) * s.X;
        double y0 = ay + _c.Y * s.Y, y1 = ay + (_c.Y + _glyphs.Height) * s.Y;
        int m = Search(s) + 2;
        var r = Rectangle.FromLTRB((int)Math.Floor(x0) - m, (int)Math.Floor(y0) - m, (int)Math.Ceiling(x1) + m, (int)Math.Ceiling(y1) + m);
        r.Intersect(new Rectangle(0, 0, frame.Width, frame.Height));
        return r;
    }

    /// <summary>Search radius in native pixels: SearchPx at 1.0, wider on a stretched HUD (the
    /// offsets grow with the scale, and so does the effect of the scale's measuring error).</summary>
    int Search(HudScale snapped) =>
        snapped.X == 1 && snapped.Y == 1 ? _c.SearchPx : (int)Math.Ceiling(_c.SearchPx * Math.Max(snapped.X, snapped.Y)) + 1;

    /// <summary>Tries each ink colour in turn and returns the first readable result (else the
    /// best unreadable one, for diagnostics).</summary>
    public DigitReading Read(Frame frame, HudScale scale)
    {
        var bounds = Bounds(frame, scale);
        if (bounds.Width <= 0 || bounds.Height <= 0) return DigitReading.Unreadable;
        var best = DigitReading.Unreadable;
        foreach (var ink in _inks)
        {
            var r = Read(frame, scale, bounds, ink);
            if (r.Ok) return r;
            if (r.Score > best.Score) best = r;
        }
        return best;
    }

    DigitReading Read(Frame frame, HudScale scale, Rectangle bounds, InkKind ink)
    {
        var s = HudLayout.Snap(scale);
        _buffer.Fill(frame, bounds, ink);

        double ax = HudLayout.AnchorX(_anchor, frame.Width), ay = HudLayout.AnchorY(_anchor, frame.Height);
        int cells = _c.CellX.Length, w = _glyphs.Width, h = _glyphs.Height;
        int value = 0, digits = 0, search = Search(s);
        double minScore = 1, minMargin = 1;
        Span<double> bestOf = stackalloc double[10];

        for (int c = 0; c < cells; c++)
        {
            // Blank test at the nominal position.
            if (SampleCell(ax, ay, s, c, 0, 0, out _) < _c.BlankInk)
            {
                // Blank cells are allowed only in front of the number.
                if (!_c.AllowBlank || digits > 0) return DigitReading.Unreadable;
                continue;
            }

            // The game rounds each digit's position on its own (pitch is not a whole pixel), so
            // every cell gets its own small search; each digit keeps its best match over it.
            bestOf.Fill(-1);
            for (int dy = -search; dy <= search; dy++)
                for (int dx = -search; dx <= search; dx++)
                {
                    double sum = SampleCell(ax, ay, s, c, dx, dy, out double sumSq);
                    double norm = Math.Sqrt(Math.Max(1e-9, sumSq - sum * sum / (w * h)));
                    for (int d = 0; d < 10; d++)
                    {
                        var t = _glyphs.Normalised[d];
                        double dot = 0;
                        for (int i = 0; i < t.Length; i++) dot += _cell[i] * t[i];
                        dot /= norm;
                        if (dot > bestOf[d]) bestOf[d] = dot;
                    }
                }
            double b1 = -1, b2 = -1;
            int d1 = -1;
            for (int d = 0; d < 10; d++)
            {
                if (bestOf[d] > b1) { b2 = b1; b1 = bestOf[d]; d1 = d; }
                else if (bestOf[d] > b2) b2 = bestOf[d];
            }
            value = value * 10 + d1;
            digits++;
            minScore = Math.Min(minScore, b1);
            minMargin = Math.Min(minMargin, b1 - b2);
        }

        if (digits == 0) return _c.AllowBlank ? new DigitReading(true, 0, 0, 0, 0, false) : DigitReading.Unreadable;
        bool ok = minScore >= _c.MinScore && minMargin >= _c.MinMargin;
        return new DigitReading(ok, value, digits, minScore, minMargin, ink == InkKind.AmmoRed);
    }

    /// <summary>Samples one cell on the 1.0-layout grid (shifted by dx, dy native pixels) into
    /// the cell buffer; returns the ink sum.</summary>
    double SampleCell(double ax, double ay, HudScale s, int c, int dx, int dy, out double sumSq)
    {
        int w = _glyphs.Width, h = _glyphs.Height;
        double sum = 0;
        sumSq = 0;
        for (int y = 0; y < h; y++)
        {
            double ny = ay + (_c.Y + y + 0.5) * s.Y - 0.5 + dy;
            for (int x = 0; x < w; x++)
            {
                float v = _buffer.Sample(ax + (_c.CellX[c] + x + 0.5) * s.X - 0.5 + dx, ny);
                _cell[y * w + x] = v;
                sum += v;
                sumSq += v * v;
            }
        }
        return sum;
    }
}

/// <summary>Anchor points and scale snapping shared by the HUD readers.</summary>
public static class HudLayout
{
    /// <summary>Clients up to 1080 px tall draw the HUD at exactly 1.0, but the ring measurement
    /// reads 0.99..1.0025 there; readers treat anything within 2 % of 1.0 as 1.0.</summary>
    public static HudScale Snap(HudScale s) =>
        new(Math.Abs(s.X - 1) <= 0.02 ? 1 : s.X, Math.Abs(s.Y - 1) <= 0.02 ? 1 : s.Y);

    public static double AnchorX(HudAnchor a, int width) => a switch
    {
        HudAnchor.TopLeft => 0,
        HudAnchor.TopRight or HudAnchor.BottomRight => width,
        _ => width / 2.0,
    };

    public static double AnchorY(HudAnchor a, int height) => a switch
    {
        HudAnchor.TopLeft or HudAnchor.TopRight => 0,
        HudAnchor.BottomCenter or HudAnchor.BottomRight => height,
        _ => height / 2.0,
    };
}
