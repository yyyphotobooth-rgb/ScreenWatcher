using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>
/// Finds the level-up quest panel (top of the client: portrait box on the left, text box, a check
/// and an X at the text box's lower right) by its FRAME and LAYOUT, never by its text or portrait:
/// 1. the check and the X: light glyphs, brighter than the panel band around them by BrightDelta
///    (the panel is semi-transparent, so absolute colours follow the scenery), matched cell by
///    cell against the masks in detectors.json; the X sits XOffset to the right of the check;
/// 2. the portrait box's bottom border: a bright line over a dark one, LineDy below the check
///    box, along LineDx of it, on at least LineMinShare of the columns.
/// The panel is searched along the whole right part of the client (its anchor at a stretched HUD
/// is unknown), only on demand (when a row it covers shows "?").
/// </summary>
public sealed class QuestPanelFinder
{
    readonly QuestPanelConfig _c;
    readonly sbyte[] _check, _close;   // 1 = glyph, 0 = band, -1 = don't care
    readonly int _w, _h;

    public QuestPanelFinder(QuestPanelConfig config)
    {
        _c = config;
        (_check, _w, _h) = Parse(config.CheckMask);
        int w2, h2;
        (_close, w2, h2) = Parse(config.CloseMask);
        if (w2 != _w || h2 != _h) throw new InvalidDataException("questPanel: checkMask and closeMask must be the same size");
        _checkKeys = Keys(_check);
        _closeKeys = Keys(_close);
    }

    readonly int[] _checkKeys, _closeKeys;

    /// <summary>Six glyph cells spread over the mask, for the quick reject.</summary>
    static int[] Keys(sbyte[] mask)
    {
        var on = Enumerable.Range(0, mask.Length).Where(i => mask[i] == 1).ToArray();
        return [.. Enumerable.Range(0, 6).Select(k => on[k * (on.Length - 1) / 5])];
    }

    static (sbyte[] Mask, int W, int H) Parse(string[] rows)
    {
        int w = rows[0].Length, h = rows.Length;
        if (rows.Any(r => r.Length != w)) throw new InvalidDataException("questPanel: mask rows must have one length");
        var m = new sbyte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                m[y * w + x] = rows[y][x] switch { '#' => 1, '.' => 0, _ => -1 };
        return (m, w, h);
    }

    /// <summary>Last match (native pixels of the check box's top-left), for logs and tests.</summary>
    public (int X, int Y) LastAt { get; private set; }
    bool _haveLast;

    public bool IsPresent(Frame f, HudScale scale)
    {
        var s = HudLayout.Snap(scale);
        int y0 = (int)Math.Round(_c.CheckY * s.Y);
        int bw = (int)Math.Round(_w * s.X), bh = (int)Math.Round(_h * s.Y);
        int xFrom = (int)(f.Width * _c.SearchFromWidth), xTo = f.Width - bw - 2;
        int dx2 = (int)Math.Round(_c.XOffset * s.X);
        // The panel does not move while it is up: try where it was last seen first.
        if (_haveLast && LastAt.X <= xTo && LastAt.Y + bh < f.Height - 1 && LastAt.X + dx2 + bw < f.Width &&
            Glyph(f, LastAt.X, LastAt.Y, s, _check) && Glyph(f, LastAt.X + dx2, LastAt.Y, s, _close) && PortraitLine(f, LastAt.X, LastAt.Y, s))
            return true;
        for (int y = Math.Max(1, y0 - _c.SearchY); y <= y0 + _c.SearchY && y + bh < f.Height - 1; y++)
            for (int x = xFrom; x <= xTo; x++)
            {
                if (!Glyph(f, x, y, s, _check)) continue;
                if (x + dx2 + bw >= f.Width || !Glyph(f, x + dx2, y, s, _close)) continue;
                if (!PortraitLine(f, x, y, s)) continue;
                LastAt = (x, y);
                _haveLast = true;
                return true;
            }
        return false;
    }

    static int Lum(ReadOnlySpan<byte> px, int i) => (px[i + 2] * 3 + px[i + 1] * 6 + px[i]) / 10;

    bool Glyph(Frame f, int x, int y, HudScale s, sbyte[] mask)
    {
        var px = f.Pixels;
        int bw = (int)Math.Round(_w * s.X), bh = (int)Math.Round(_h * s.Y);
        // Quick reject (most positions): a few glyph cells must stand out from the box corners.
        int corners = (Lum(px, (y * f.Width + x) * 4) + Lum(px, (y * f.Width + x + bw - 1) * 4) +
                       Lum(px, ((y + bh - 1) * f.Width + x) * 4) + Lum(px, ((y + bh - 1) * f.Width + x + bw - 1) * 4)) / 4;
        foreach (int k in mask == _check ? _checkKeys : _closeKeys)
        {
            int ky = y + (int)((k / _w) * s.Y + 0.5 * (s.Y - 1)), kx = x + (int)((k % _w) * s.X + 0.5 * (s.X - 1));
            if (Lum(px, (ky * f.Width + kx) * 4) < corners + _c.BrightDelta / 2) return false;
        }
        // Band level: mean luminance of the box's border (the glyphs never touch it).
        long sum = 0; int n = 0;
        for (int xx = x; xx < x + bw; xx++)
        {
            sum += Lum(px, (y * f.Width + xx) * 4) + Lum(px, ((y + bh - 1) * f.Width + xx) * 4); n += 2;
        }
        for (int yy = y + 1; yy < y + bh - 1; yy++)
        {
            sum += Lum(px, (yy * f.Width + x) * 4) + Lum(px, (yy * f.Width + x + bw - 1) * 4); n += 2;
        }
        int cut = (int)(sum / n) + _c.BrightDelta;
        int good = 0, total = 0, glyphHit = 0, glyphAll = 0;
        for (int ty = 0; ty < _h; ty++)
        {
            int yy = y + (int)(ty * s.Y + 0.5 * (s.Y - 1));
            for (int tx = 0; tx < _w; tx++)
            {
                int m = mask[ty * _w + tx];
                if (m < 0) continue;
                int xx = x + (int)(tx * s.X + 0.5 * (s.X - 1));
                bool on = Lum(px, (yy * f.Width + xx) * 4) >= cut;
                total++;
                if (on == (m == 1)) good++;
                if (m == 1) { glyphAll++; if (on) glyphHit++; }
            }
        }
        double min = s.X == 1 && s.Y == 1 ? _c.MinMatch : _c.MinMatchScaled;
        return good >= min * total && glyphHit >= min * glyphAll;
    }

    bool PortraitLine(Frame f, int x, int y, HudScale s)
    {
        var px = f.Pixels;
        int bright = y + (int)Math.Round(_c.LineDy * s.Y), dark = bright - Math.Max(1, (int)Math.Round(s.Y));
        if (dark < 1 || bright + 1 >= f.Height) return false;
        int x0 = x + (int)Math.Round(_c.LineDx[0] * s.X), x1 = x + (int)Math.Round(_c.LineDx[1] * s.X);
        if (x0 < 1 || x1 >= f.Width) return false;
        int hits = 0, cols = 0;
        for (int xx = x0; xx <= x1; xx++, cols++)
        {
            int b = Math.Max(Lum(px, (bright * f.Width + xx) * 4), Lum(px, ((bright + 1) * f.Width + xx) * 4));
            int d = Math.Min(Lum(px, (dark * f.Width + xx) * 4), Lum(px, ((dark - 1) * f.Width + xx) * 4));
            if (b - d >= _c.LineMinContrast) hits++;
        }
        return hits >= _c.LineMinShare * cols;
    }
}
