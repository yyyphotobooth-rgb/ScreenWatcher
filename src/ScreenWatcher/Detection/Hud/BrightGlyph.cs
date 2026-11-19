using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>
/// A light HUD shape drawn over a semi-transparent panel (the quest panel's check and X, the
/// system message box's "SYSTEM MESSAGE" title). Absolute colours follow the scenery behind the
/// panel, so a cell is "on" when its luminance is at least Delta above the mean of the box's
/// border. Mask: '#' shape, '.' panel, any other character = either (edge cells).
/// </summary>
public sealed class BrightGlyph
{
    readonly sbyte[] _mask;   // 1 = shape, 0 = panel, -1 = don't care
    readonly int[] _keys;
    public int W { get; }
    public int H { get; }

    public BrightGlyph(string[] rows)
    {
        W = rows[0].Length;
        H = rows.Length;
        if (rows.Any(r => r.Length != W)) throw new InvalidDataException("glyph mask rows must have one length");
        _mask = new sbyte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                _mask[y * W + x] = rows[y][x] switch { '#' => 1, '.' => 0, _ => -1 };
        // Six shape cells spread over the mask, for the quick reject.
        var on = Enumerable.Range(0, _mask.Length).Where(i => _mask[i] == 1).ToArray();
        if (on.Length < 6) throw new InvalidDataException("glyph mask needs at least 6 '#' cells");
        _keys = [.. Enumerable.Range(0, 6).Select(k => on[k * (on.Length - 1) / 5])];
    }

    static int Lum(ReadOnlySpan<byte> px, int i) => (px[i + 2] * 3 + px[i + 1] * 6 + px[i]) / 10;

    /// <summary>Box size in native pixels at this (snapped) scale.</summary>
    public (int W, int H) Size(HudScale s) => ((int)Math.Round(W * s.X), (int)Math.Round(H * s.Y));

    /// <summary>The shape with its box's top-left at native (x, y); the box must lie inside the frame.</summary>
    public bool Match(Frame f, int x, int y, HudScale s, int delta, double minMatch)
    {
        var px = f.Pixels;
        var (bw, bh) = Size(s);
        if (x < 0 || y < 0 || x + bw > f.Width || y + bh > f.Height) return false;
        // Quick reject (most positions): a few shape cells must stand out from the box corners.
        int corners = (Lum(px, (y * f.Width + x) * 4) + Lum(px, (y * f.Width + x + bw - 1) * 4) +
                       Lum(px, ((y + bh - 1) * f.Width + x) * 4) + Lum(px, ((y + bh - 1) * f.Width + x + bw - 1) * 4)) / 4;
        foreach (int k in _keys)
        {
            int ky = y + (int)((k / W) * s.Y + 0.5 * (s.Y - 1)), kx = x + (int)((k % W) * s.X + 0.5 * (s.X - 1));
            if (Lum(px, (ky * f.Width + kx) * 4) < corners + delta / 2) return false;
        }
        // Panel level: mean luminance of the box's border (the shapes never touch it).
        long sum = 0; int n = 0;
        for (int xx = x; xx < x + bw; xx++)
        {
            sum += Lum(px, (y * f.Width + xx) * 4) + Lum(px, ((y + bh - 1) * f.Width + xx) * 4); n += 2;
        }
        for (int yy = y + 1; yy < y + bh - 1; yy++)
        {
            sum += Lum(px, (yy * f.Width + x) * 4) + Lum(px, (yy * f.Width + x + bw - 1) * 4); n += 2;
        }
        int cut = (int)(sum / n) + delta;
        int good = 0, total = 0, shapeHit = 0, shapeAll = 0;
        for (int ty = 0; ty < H; ty++)
        {
            int yy = y + (int)(ty * s.Y + 0.5 * (s.Y - 1));
            for (int tx = 0; tx < W; tx++)
            {
                int m = _mask[ty * W + tx];
                if (m < 0) continue;
                int xx = x + (int)(tx * s.X + 0.5 * (s.X - 1));
                bool on = Lum(px, (yy * f.Width + xx) * 4) >= cut;
                total++;
                if (on == (m == 1)) good++;
                if (m == 1) { shapeAll++; if (on) shapeHit++; }
            }
        }
        return good >= minMatch * total && shapeHit >= minMatch * shapeAll;
    }
}
