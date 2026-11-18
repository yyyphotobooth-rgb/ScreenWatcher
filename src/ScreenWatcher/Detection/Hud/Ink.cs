using System.Drawing;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>Which HUD colour counts as "ink" (the bright core of a digit) for a reader.</summary>
public enum InkKind
{
    /// <summary>Ammo digits, normal: blue-white core (207,236,254) on a blue outline.</summary>
    AmmoBlue,
    /// <summary>Ammo digits of an empty counter: pink core on a red outline.</summary>
    AmmoRed,
    /// <summary>Kill counter digits: flat orange (255,83,15).</summary>
    Kill,
    /// <summary>Item counts: pure white with a black outline.</summary>
    Count,
}

/// <summary>
/// Per-pixel "ink" (0..1) of one frame rectangle, kept in a reused buffer. Readers sample it with
/// bilinear interpolation, so the same 1.0-layout templates work when the game stretches its HUD.
/// </summary>
public sealed class InkBuffer
{
    float[] _ink = [];
    int _x0, _y0, _w, _h;

    public void Fill(Frame frame, Rectangle rect, InkKind kind)
    {
        rect.Intersect(new Rectangle(0, 0, frame.Width, frame.Height));
        _x0 = rect.X; _y0 = rect.Y; _w = Math.Max(0, rect.Width); _h = Math.Max(0, rect.Height);
        if (_ink.Length < _w * _h) _ink = new float[_w * _h];
        var px = frame.Pixels;
        for (int y = 0; y < _h; y++)
        {
            int i = ((_y0 + y) * frame.Width + _x0) * 4, o = y * _w;
            for (int x = 0; x < _w; x++, i += 4)
            {
                int b = px[i], g = px[i + 1], r = px[i + 2];
                _ink[o + x] = kind switch
                {
                    InkKind.AmmoBlue => AmmoBlue(r, g, b),
                    InkKind.AmmoRed => AmmoRed(r, g, b),
                    InkKind.Kill => Kill(r, g, b),
                    _ => Count(r, g, b),
                };
            }
        }
    }

    /// <summary>Bilinear ink at a native pixel position (pixel centres at integers); 0 outside.</summary>
    public float Sample(double x, double y)
    {
        x -= _x0; y -= _y0;
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        if (xi < -1 || yi < -1 || xi >= _w || yi >= _h) return 0;
        float fx = (float)(x - xi), fy = (float)(y - yi);
        float a = At(xi, yi), b = At(xi + 1, yi), c = At(xi, yi + 1), d = At(xi + 1, yi + 1);
        return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
    }

    float At(int x, int y) => (uint)x < (uint)_w && (uint)y < (uint)_h ? _ink[y * _w + x] : 0;

    // Core (207,236,254); outline (7,98,174). Stretched HUDs blend the two, so ink follows G
    // between them. Grey cloud seen through the panel is (229,229,229): kept out by B - R.
    static float AmmoBlue(int r, int g, int b) =>
        b >= 200 && b - r >= 25 && g > 98 ? Math.Min(1f, (g - 98) / 138f) : 0;

    // Empty counter: pink core (250..255, 160..225, 160..225) on a red outline (225..244,22..42,26..44).
    static float AmmoRed(int r, int g, int b) =>
        r >= 215 && r - g >= 20 && Math.Abs(g - b) <= 40 && g > 60 ? Math.Min(1f, (g - 60) / 110f) : 0;

    // Fill (255,83,15), edges the same hue but darker.
    static float Kill(int r, int g, int b) =>
        r >= 140 && b * 4 <= r && g * 100 >= r * 22 && g * 100 <= r * 42 ? Math.Min(1f, (r - 120) / 110f) : 0;

    static float Count(int r, int g, int b)
    {
        int m = Math.Min(r, Math.Min(g, b));
        return m >= 200 ? Math.Min(1f, (m - 190) / 60f) : 0;
    }
}
