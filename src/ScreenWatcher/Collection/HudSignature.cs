using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Collection;

/// <summary>
/// One region of one frame reduced to a class per pixel: which of the region's HUD colour keys
/// the pixel matches (0 = none). The buffer is reused; it only grows when the region gets larger.
///
/// Why colour keys: the HUD's own content (digits, bar fills, dialog body, text) is drawn in a
/// few fixed, opaque colours (e.g. ammo digits (207,236,254) with a (3..6,96,172) outline, kill
/// digits orange-red, item counts white with a black outline). The 3D scene seen through the
/// semi-transparent panels changes every frame but rarely lands inside these narrow colour
/// boxes, so comparing key classes reacts to HUD content (digits, bars, panel present/absent)
/// and mostly ignores the scene. Brightness/edge-based tests were tried first and failed on
/// live data (sky -> rock behind a panel looked like a digit change).
/// </summary>
public sealed class HudSignature
{
    byte[] _cls = [];

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool Valid { get; private set; }
    /// <summary>Number of pixels that matched any key.</summary>
    public int Count { get; private set; }

    public byte this[int x, int y] => _cls[y * Width + x];

    public void Compute(Frame frame, Rectangle r, IReadOnlyList<ColourKey> keys)
    {
        int w = r.Width, h = r.Height;
        Width = w;
        Height = h;
        if (_cls.Length < w * h) _cls = new byte[w * h];
        var px = frame.Pixels;
        int fw = frame.Width, fh = frame.Height, count = 0;
        for (int y = 0; y < h; y++)
        {
            int fy = r.Y + y;
            int src = (fy * fw + r.X) * 4, dst = y * w;
            for (int x = 0; x < w; x++, src += 4)
            {
                byte b = px[src], g = px[src + 1], rr = px[src + 2];
                byte cls = 0;
                for (int k = 0; k < keys.Count; k++)
                {
                    var key = keys[k];
                    if (rr < key.Min[0] || rr > key.Max[0] || g < key.Min[1] || g > key.Max[1] || b < key.Min[2] || b > key.Max[2]) continue;
                    if (key.OutlineMax >= 0 && !HasDarkNeighbour(px, fw, fh, r.X + x, fy, key.OutlineMax)) continue;
                    if (key.FlatRadius > 0 && !IsFlat(px, fw, r.X + x, fy, key.FlatRadius, key.FlatTolerance)) continue;
                    cls = (byte)(k + 1);
                    break;
                }
                _cls[dst + x] = cls;
                if (cls != 0) count++;
            }
        }
        Count = count;
        Valid = true;
    }

    static bool HasDarkNeighbour(ReadOnlySpan<byte> px, int fw, int fh, int x, int y, int darkMax)
    {
        for (int yy = Math.Max(0, y - 1); yy <= Math.Min(fh - 1, y + 1); yy++)
            for (int xx = Math.Max(0, x - 1); xx <= Math.Min(fw - 1, x + 1); xx++)
            {
                int i = (yy * fw + xx) * 4;
                if (px[i] <= darkMax && px[i + 1] <= darkMax && px[i + 2] <= darkMax) return true;
            }
        return false;
    }

    static bool IsFlat(ReadOnlySpan<byte> px, int fw, int x, int y, int radius, int tol)
    {
        if (x - radius < 0 || x + radius >= fw) return false;
        int c = (y * fw + x) * 4, l = c - radius * 4, rr = c + radius * 4;
        for (int ch = 0; ch < 3; ch++)
            if (Math.Abs(px[c + ch] - px[l + ch]) > tol || Math.Abs(px[c + ch] - px[rr + ch]) > tol) return false;
        return true;
    }

    public void CopyFrom(HudSignature other)
    {
        Width = other.Width;
        Height = other.Height;
        Count = other.Count;
        int n = Width * Height;
        if (_cls.Length < n) _cls = new byte[n];
        Array.Copy(other._cls, _cls, n);
        Valid = other.Valid;
    }

    /// <summary>
    /// Pixels whose key class differs between the two signatures, at the best whole-pixel
    /// alignment within +-maxShift (absorbs a 1 px offset, e.g. a centred region on an odd-width
    /// client). Pixels within maxShift of the border are not compared. -1 when not comparable
    /// (size differs or one is not computed). Stops counting at <paramref name="stopAt"/>.
    /// </summary>
    public static int CountChanged(HudSignature current, HudSignature saved, int maxShift, int stopAt = int.MaxValue)
    {
        if (!current.Valid || !saved.Valid || current.Width != saved.Width || current.Height != saved.Height) return -1;
        int w = current.Width, h = current.Height, s = Math.Max(0, maxShift);
        if (w <= 2 * s || h <= 2 * s) return 0;

        int best = Count(0, 0, stopAt);
        for (int dy = -s; dy <= s && best > 0; dy++)
            for (int dx = -s; dx <= s && best > 0; dx++)
                if (dx != 0 || dy != 0) best = Math.Min(best, Count(dx, dy, best));
        return best;

        int Count(int dx, int dy, int limit)
        {
            int d = 0;
            var a = current._cls; var b = saved._cls;
            for (int y = s; y < h - s; y++)
            {
                int ia = y * w + s, ib = (y + dy) * w + s + dx;
                for (int x = s; x < w - s; x++, ia++, ib++)
                    if (a[ia] != b[ib] && ++d >= limit) return d;
            }
            return d;
        }
    }

    /// <summary>Minimum changed pixels at this scale (thresholds are at 1.0; they scale with area).
    /// <paramref name="regionMinPixels"/> overrides the global MinChangedPixels.</summary>
    public static int Threshold(ChangeConfig c, HudScale scale, double? regionMinPixels = null) =>
        Math.Max(1, (int)Math.Ceiling((regionMinPixels ?? c.MinChangedPixels) * scale.X * scale.Y));

    /// <summary>True when the HUD content changed meaningfully (or the signatures are not comparable).</summary>
    public static bool Changed(HudSignature current, HudSignature saved, ChangeConfig c, HudScale scale, double? regionMinPixels = null)
    {
        int min = Threshold(c, scale, regionMinPixels);
        int n = CountChanged(current, saved, c.MaxShift, stopAt: min);
        return n < 0 || n >= min;
    }
}
