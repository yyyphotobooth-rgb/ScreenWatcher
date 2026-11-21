using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>One war pillar panel found on a frame, top to bottom. <c>Minutes</c> = remaining time
/// (HH*60+MM), -1 when the timer could not be read; <c>Covered</c>: unreadable and the game's
/// tooltip text lies over the icon box. <c>Hp</c> = the HP bar's fill in % (0..100), -1 when the bar
/// could not be measured; <c>HpCovered</c>: tooltip text lies over the bar.</summary>
public readonly record struct PillarPanel(Rectangle Box, Rectangle Crop, int Minutes, bool Covered, int Hp = -1, bool HpCovered = false)
{
    public bool Readable => Minutes >= 0;
}

/// <summary>
/// Finds the war pillar ("เสาวอ") panels under the top-left gauge and reads each countdown.
/// A panel is recognised by its icon box: a 1-px light outline with a 1-px blue line (FrameColor)
/// inside it; the line's top-left corner is the panel's reference point. While the pillar is
/// attacked the game flashes the whole box red/pink: a box with a line of any other colour (darker
/// than its outline by LineMinContrast) counts only with a pixel-exact timer or its HP track. The strip at FrameY
/// (+-SearchY) is searched from SearchLeft to the right edge, and further panels stack downward
/// (StackPitchY apart) under each one found, so any number of panels in any slot is found. The timer is Windows' Tahoma without smoothing (white, black outline): each glyph
/// is cut out by its ink columns and looked up pixel-exactly among Tahoma digits and ':' rendered
/// at start-up (FontMinHeight..FontMaxHeight, like the item counts); when icon art touches the
/// band, each glyph is matched inside its own box instead (ScanTimer). Only "H..H:MM" with all
/// digits of one font height is accepted. Reuses its buffers; allocates nothing per frame.
/// </summary>
public sealed class WarPillarFinder
{
    const int MaxGlyphs = 12;
    readonly WarPillarConfig _c;
    readonly Dictionary<string, (char Ch, int Height)> _glyphs;
    readonly Dictionary<string, (char Ch, int Height)>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    bool[] _ink = [];
    char[] _key = new char[256];
    readonly char[] _chars = new char[MaxGlyphs];
    readonly int[] _heights = new int[MaxGlyphs], _tops = new int[MaxGlyphs], _bottoms = new int[MaxGlyphs];

    public WarPillarFinder(WarPillarConfig config)
    {
        _c = config;
        _glyphs = Rendered(config.FontFace, config.FontMinHeight, config.FontMaxHeight);
        _lookup = _glyphs.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    static readonly Dictionary<(string, int, int), Dictionary<string, (char Ch, int Height)>> Cache = [];

    /// <summary>Digits and ':' at every height (rendered once per process; read-only afterwards).
    /// A bitmap that would mean two different characters is dropped.</summary>
    static Dictionary<string, (char Ch, int Height)> Rendered(string face, int min, int max)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((face, min, max), out var done)) return done;
            var map = new Dictionary<string, (char Ch, int Height)>(StringComparer.Ordinal);
            var clash = new HashSet<string>(StringComparer.Ordinal);
            for (int h = min; h <= max; h++)
                foreach (char ch in "0123456789:")
                {
                    if (CountFont.Glyph(face, h, ch) is not { } key || clash.Contains(key)) continue;
                    if (map.TryGetValue(key, out var have) && have.Ch != ch) { map.Remove(key); clash.Add(key); }
                    else map.TryAdd(key, (ch, h));
                }
            return Cache[(face, min, max)] = map;
        }
    }

    /// <summary>All panels on the frame, top to bottom, then left to right (cleared first). The first
    /// row is found along the strip at FrameY; under each panel found, the next one is looked for
    /// StackPitchY lower (+-SearchY rows and columns), down to StackMax panels per column.</summary>
    public void Find(Frame f, HudScale scale, List<PillarPanel> into)
    {
        into.Clear();
        var s = HudLayout.Snap(scale);
        int yNom = (int)Math.Round(_c.FrameY * s.Y);
        int sy = s.Y == 1 ? _c.SearchY : (int)Math.Ceiling(_c.SearchY * s.Y) + 1;
        int sx = s.X == 1 ? _c.SearchY : (int)Math.Ceiling(_c.SearchY * s.X) + 1;
        int left = Math.Max(1, (int)Math.Round(_c.SearchLeft * s.X));
        int boxW = (int)Math.Round((_c.FrameWidth + 4) * s.X);
        int right = f.Width - boxW - 1;
        int pitch = (int)Math.Round(_c.StackPitchY * s.Y);
        for (int x = left; x < right; x++)
            for (int y = Math.Max(1, yNom - sy); y <= yNom + sy; y++)
            {
                if (TryPanel(f, x, y, s) is not { } panel) continue;
                into.Add(panel);
                if (pitch > 0) FindBelow(f, s, x, y, pitch, sx, sy, right, into);
                x += boxW;   // next panel starts after this box
                break;
            }
        if (into.Count > 1) into.Sort(TopThenLeft);
    }

    static readonly Comparison<PillarPanel> TopThenLeft =
        (a, b) => a.Box.Y != b.Box.Y ? a.Box.Y.CompareTo(b.Box.Y) : a.Box.X.CompareTo(b.Box.X);

    /// <summary>Panels stacked under the one at (x, y): each StackPitchY below the last one found.</summary>
    void FindBelow(Frame f, HudScale s, int x, int y, int pitch, int sx, int sy, int right, List<PillarPanel> into)
    {
        int boxH = (int)Math.Round((_c.FrameHeight + 4) * s.Y);
        for (int n = 1; n < _c.StackMax; n++)
        {
            int yNext = y + pitch;
            if (yNext + boxH >= f.Height) return;
            bool found = false;
            for (int dy = 0; dy <= 2 * sy && !found; dy++)
            {
                int yy = yNext + (dy % 2 == 0 ? dy / 2 : -(dy + 1) / 2);   // nearest rows first
                for (int xx = Math.Max(1, x - sx); xx <= Math.Min(right - 1, x + sx); xx++)
                {
                    if (TryPanel(f, xx, yy, s) is not { } panel) continue;
                    into.Add(panel);
                    (x, y, found) = (xx, yy, true);
                    break;
                }
            }
            if (!found) return;
        }
    }

    bool Blue(ReadOnlySpan<byte> px, Frame f, int x, int y)
    {
        if ((uint)x >= (uint)f.Width || (uint)y >= (uint)f.Height) return false;
        int i = (y * f.Width + x) * 4;
        var c = _c.FrameColor;
        return Math.Abs(px[i + 2] - c[0]) + Math.Abs(px[i + 1] - c[1]) + Math.Abs(px[i] - c[2]) <= _c.FrameTolerance;
    }

    /// <summary>The panel whose line corner is at (x, y), or null. A blue box (the line's colour, as
    /// always) is a panel; a box of any other line colour (the red/pink flash while the pillar is
    /// attacked, or a blend) only by its geometry plus a pixel-exact timer or the HP track.</summary>
    PillarPanel? TryPanel(Frame f, int x, int y, HudScale s)
    {
        if (IsFrame(f, x, y, s)) return ReadPanel(f, x, y, s);
        if (!IsOutlinedBox(f, x, y, s)) return null;
        var p = ReadPanel(f, x, y, s);
        return p.Readable || p.Hp >= 0 ? p : null;
    }

    static int Lum(ReadOnlySpan<byte> px, int i) => (px[i + 2] * 3 + px[i + 1] * 6 + px[i]) / 10;

    /// <summary>Light outline pixel at (ox, oy) with a line pixel (lx, ly) inside it that is at least
    /// LineMinContrast darker: the box's edge whatever colour the line is.</summary>
    bool Edge(ReadOnlySpan<byte> px, Frame f, int lx, int ly, int ox, int oy)
    {
        if ((uint)lx >= (uint)f.Width || (uint)ly >= (uint)f.Height || (uint)ox >= (uint)f.Width || (uint)oy >= (uint)f.Height) return false;
        int o = Lum(px, (oy * f.Width + ox) * 4);
        return o >= _c.OutlineMinLum && o - Lum(px, (ly * f.Width + lx) * 4) >= _c.LineMinContrast;
    }

    /// <summary>The box geometry alone (any line colour): light outline with a darker 1-px line inside
    /// it along the left column and the top row, FrameMinShare of the samples each.</summary>
    bool IsOutlinedBox(Frame f, int x, int y, HudScale s)
    {
        var px = f.Pixels;
        int ox = Math.Max(1, (int)Math.Round(s.X)), oy = Math.Max(1, (int)Math.Round(s.Y));
        int y10 = y + (int)Math.Round(10 * s.Y), y25 = y + (int)Math.Round(25 * s.Y), x20 = x + (int)Math.Round(20 * s.X);
        if (!Edge(px, f, x, y10, x - ox, y10) || !Edge(px, f, x, y25, x - ox, y25) || !Edge(px, f, x20, y, x20, y - oy)) return false;
        int hit = 0, n = 0;
        for (int dy = 2; dy <= _c.FrameHeight; dy++, n++)
        {
            int yy = y + (int)Math.Round(dy * s.Y);
            if (Edge(px, f, x, yy, x - ox, yy)) hit++;
        }
        if (hit < _c.FrameMinShare * n) return false;
        hit = n = 0;
        for (int dx = 2; dx <= _c.FrameWidth; dx++, n++)
        {
            int xx = x + (int)Math.Round(dx * s.X);
            if (Edge(px, f, xx, y, xx, y - oy)) hit++;
        }
        return hit >= _c.FrameMinShare * n;
    }

    bool IsFrame(Frame f, int x, int y, HudScale s)
    {
        var px = f.Pixels;
        // Quick reject (almost every position): three points of the blue line.
        if (!Blue(px, f, x, y + (int)Math.Round(10 * s.Y)) || !Blue(px, f, x, y + (int)Math.Round(25 * s.Y))
            || !Blue(px, f, x + (int)Math.Round(20 * s.X), y)) return false;
        // Blue line plus the light outline just outside it (left: one pixel to the left; top: one
        // row above). Blue sky alone (43,83,126) passes the colour test but has no outline.
        int ox = Math.Max(1, (int)Math.Round(s.X)), oy = Math.Max(1, (int)Math.Round(s.Y));
        int blue = 0, light = 0, n = 0;
        for (int dy = 2; dy <= _c.FrameHeight; dy++, n++)
        {
            int yy = y + (int)Math.Round(dy * s.Y);
            if (Blue(px, f, x, yy)) blue++;
            if (Light(px, f, x - ox, yy)) light++;
        }
        if (blue < _c.FrameMinShare * n || light < _c.FrameMinShare * n) return false;
        blue = light = n = 0;
        for (int dx = 2; dx <= _c.FrameWidth; dx++, n++)
        {
            int xx = x + (int)Math.Round(dx * s.X);
            if (Blue(px, f, xx, y)) blue++;
            if (Light(px, f, xx, y - oy)) light++;
        }
        return blue >= _c.FrameMinShare * n && light >= _c.FrameMinShare * n;
    }

    bool Light(ReadOnlySpan<byte> px, Frame f, int x, int y)
    {
        if ((uint)x >= (uint)f.Width || (uint)y >= (uint)f.Height) return false;
        int i = (y * f.Width + x) * 4;
        return (px[i + 2] * 3 + px[i + 1] * 6 + px[i]) / 10 >= _c.OutlineMinLum;
    }

    PillarPanel ReadPanel(Frame f, int x, int y, HudScale s)
    {
        var frameRect = new Rectangle(0, 0, f.Width, f.Height);
        var box = Rectangle.Intersect(frameRect, new Rectangle(x - (int)Math.Ceiling(s.X), y - (int)Math.Ceiling(s.Y),
            (int)Math.Round((_c.FrameWidth + 7) * s.X), (int)Math.Round((_c.FrameHeight + 4) * s.Y)));
        var crop = Rectangle.Intersect(frameRect, new Rectangle(x + (int)Math.Round(_c.Crop[0] * s.X), y + (int)Math.Round(_c.Crop[1] * s.Y),
            (int)Math.Round(_c.Crop[2] * s.X), (int)Math.Round(_c.Crop[3] * s.Y)));
        int minutes = ReadTimer(f, x, y, s);
        bool covered = minutes < 0 && TooltipTextPixels(f, box) >= _c.CoverTextMin;
        var (hp, hpCovered) = ReadHp(f, x, y, s);
        return new PillarPanel(box, crop, minutes, covered, hp, hpCovered);
    }

    /// <summary>
    /// HP bar of the box at (x, y): the track's ends from its dark top border row (searched over
    /// BarSearchX; one row up/down when scaled), checked by the light row under the bar; the fill =
    /// columns from the left end whose middle rows are mostly fill colour (red, orange or yellow).
    /// Fill must be one run from the left (at most one stray column inside it); fill beyond a gap
    /// means something is drawn over the bar: unreadable, never a drop. Tooltip text over the bar
    /// area: covered.
    /// </summary>
    (int Hp, bool Covered) ReadHp(Frame f, int bx, int by, HudScale s)
    {
        int sx0 = bx + (int)Math.Round(_c.BarSearchX[0] * s.X), sx1 = bx + (int)Math.Round(_c.BarSearchX[1] * s.X);
        int yNom = by + (int)Math.Round(_c.BarBorderY * s.Y);
        int barH = Math.Max(1, (int)Math.Round(_c.BarHeight * s.Y));
        double expect = _c.BarLength * s.X, tol = Math.Max(_c.BarLengthTolerance, _c.BarLengthTolerance * s.X);
        var area = Rectangle.Intersect(new Rectangle(0, 0, f.Width, f.Height),
            new Rectangle(sx0, yNom - 1, sx1 - sx0 + 1, barH + 3));
        bool textOver = TooltipTextPixels(f, area) >= _c.CoverTextMin;
        if (textOver) return (-1, true);
        if (sx0 < 0 || sx1 >= f.Width || yNom - 1 < 0 || yNom + barH + 2 >= f.Height) return (-1, false);

        var px = f.Pixels;
        int dyMax = s.Y == 1 ? 0 : 1;
        // Mode 0: the track's own colours (dark-blue border, light-blue row under the bar). Mode 1
        // (the red/pink flash tints both): colour-free, the border row darker than the row under
        // the bar by BarMinContrast, and that row light. The fill and the empty track keep their
        // colours in the flash.
        for (int mode = 0; mode < 2; mode++)
        for (int dy = 0; dy <= dyMax * 2; dy++)
        {
            int ty = yNom + (dy == 0 ? 0 : dy == 1 ? -1 : 1);
            int lightRow = ty + barH + 1;
            // Longest dark run on the border row.
            int bestA = -1, bestB = -1;
            for (int x = sx0; x <= sx1;)
            {
                if (!TrackBorder(px, f, mode, x, ty, lightRow)) { x++; continue; }
                int a = x;
                while (x <= sx1 && TrackBorder(px, f, mode, x, ty, lightRow)) x++;
                if (x - a > bestB - bestA + 1) (bestA, bestB) = (a, x - 1);
            }
            if (bestA < 0) continue;
            // Fill columns: between the dark left column and the light right column (the dark top
            // row runs over both).
            int left = bestA + 1, right = bestB - 1, len = right - left + 1;
            if (Math.Abs(len - expect) > tol) continue;
            // The light row just under the bar.
            int light = 0;
            for (int x = left; x <= right; x++)
                if (mode == 0 ? LightBlue(px, f, x, lightRow) : Lum(px, (lightRow * f.Width + x) * 4) >= _c.BarLightMinLum) light++;
            if (light < _c.FrameMinShare * len) continue;


            // Middle rows of the bar (the top and bottom rows are a highlight and a shadow).
            int r0 = ty + 1 + barH / 4, r1 = ty + barH - barH / 4;
            int last = -1, fills = 0;
            for (int x = left; x <= right; x++)
            {
                int n = 0, rows = 0;
                for (int y = r0; y <= r1; y++, rows++) if (Fill(px, f, x, y)) n++;
                if (n * 2 > rows) { fills++; last = x; }
            }
            int run = last < 0 ? 0 : last - left + 1;
            if (run - fills > 1) return (-1, false);   // gaps inside the fill: something over the bar
            return ((int)Math.Round(100.0 * run / len), false);
        }
        return (-1, false);
    }

    /// <summary>Border pixel of the HP track: by its colour (mode 0) or, in the red flash (mode 1),
    /// at least BarMinContrast darker than the row under the bar in the same column.</summary>
    bool TrackBorder(ReadOnlySpan<byte> px, Frame f, int mode, int x, int y, int lightRow) =>
        mode == 0 ? Border(px, f, x, y)
                  : Lum(px, (lightRow * f.Width + x) * 4) - Lum(px, (y * f.Width + x) * 4) >= _c.BarMinContrast;

    bool Border(ReadOnlySpan<byte> px, Frame f, int x, int y)
    {
        int i = (y * f.Width + x) * 4;
        int b = px[i], g = px[i + 1], r = px[i + 2];
        return (r * 3 + g * 6 + b) / 10 <= _c.BarBorderMaxLum && b - r >= _c.BarBorderMinBlue;
    }

    bool LightBlue(ReadOnlySpan<byte> px, Frame f, int x, int y)
    {
        int i = (y * f.Width + x) * 4;
        int b = px[i], g = px[i + 1], r = px[i + 2];
        return (r * 3 + g * 6 + b) / 10 >= _c.BarLightMinLum && b > r;
    }

    bool Fill(ReadOnlySpan<byte> px, Frame f, int x, int y)
    {
        int i = (y * f.Width + x) * 4;
        int b = px[i], g = px[i + 1], r = px[i + 2];
        return r >= _c.FillMinRed && r - b >= _c.FillMinRedOverBlue && g - r <= _c.FillMaxGreenOverRed;
    }

    /// <summary>Remaining minutes of the timer in the box at (x, y), or -1.</summary>
    int ReadTimer(Frame f, int bx, int by, HudScale s)
    {
        int x0 = bx + (int)Math.Floor(_c.TimerX[0] * s.X), x1 = bx + (int)Math.Ceiling((_c.TimerX[1] + 1) * s.X) - 1;
        int y0 = by + (int)Math.Floor(_c.TimerY[0] * s.Y), y1 = by + (int)Math.Ceiling((_c.TimerY[1] + 1) * s.Y) - 1;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(f.Width - 1, x1); y1 = Math.Min(f.Height - 1, y1);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        if (w <= 0 || h <= 0) return -1;
        if (_ink.Length < w * h) _ink = new bool[w * h];
        var px = f.Pixels;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = ((y0 + y) * f.Width + x0 + x) * 4;
                _ink[y * w + x] = px[i] >= _c.InkMin && px[i + 1] >= _c.InkMin && px[i + 2] >= _c.InkMin;
            }

        int count = 0;
        bool cut = true;
        for (int x = 0; x < w && cut;)
        {
            if (!ColumnHasInk(x, w, h)) { x++; continue; }
            int a = x;
            while (x < w && ColumnHasInk(x, w, h)) x++;
            cut = AddSegment(a, x - 1, w, h, ref count);
        }
        int minutes = cut ? Parse(count) : -1;
        return minutes >= 0 ? minutes : ScanTimer(w, h, Math.Max(_c.TimerMaxGap, (int)Math.Ceiling(_c.TimerMaxGap * s.X)));
    }

    /// <summary>One rendered glyph (from its lookup key): its ink pixels and its halo, the pixels
    /// 4-adjacent to the ink that are not ink (where the game draws the black outline; its diagonal corners are not outlined), as offsets
    /// from the glyph box's top-left (the halo reaches one pixel outside the box).</summary>
    sealed record GlyphBits(char Ch, int FontHeight, int W, int H, int[] Ink, int[] Halo);

    List<GlyphBits>? _bits;
    readonly List<(char Ch, int X, int W, int Top, int Bottom, int Font)> _found = [];
    bool[] _inChain = new bool[32];

    List<GlyphBits> Bits()
    {
        if (_bits is not null) return _bits;
        var list = new List<GlyphBits>();
        foreach (var (key, (ch, fh)) in _glyphs)
        {
            var rows = key.Split('/');
            int gw = rows[0].Length, gh = rows.Length;
            bool On(int x, int y) => x >= 0 && y >= 0 && x < gw && y < gh && rows[y][x] == '#';
            var ink = new List<int>();
            var halo = new List<int>();
            for (int y = -1; y <= gh; y++)
                for (int x = -1; x <= gw; x++)
                {
                    if (On(x, y)) { ink.Add(y); ink.Add(x); continue; }
                    if (On(x - 1, y) || On(x + 1, y) || On(x, y - 1) || On(x, y + 1)) { halo.Add(y); halo.Add(x); }
                }
            list.Add(new GlyphBits(ch, fh, gw, gh, [.. ink], [.. halo]));
        }
        // Widest first: at one position the whole glyph wins over a part of it.
        list.Sort((a, b) => b.W != a.W ? b.W.CompareTo(a.W) : b.H.CompareTo(a.H));
        return _bits = list;
    }

    /// <summary>Glyph g at (x, y) of the ink map: all its ink pixels ink and its halo (inside the
    /// band) no ink. Other pixels (the hollow inside a 0, icon art next to the text) do not matter:
    /// the game's text has a black outline but no background.</summary>
    bool At(GlyphBits g, int x, int y, int w, int h)
    {
        var ink = g.Ink;
        for (int i = 0; i < ink.Length; i += 2)
            if (!_ink[(y + ink[i]) * w + x + ink[i + 1]]) return false;
        var halo = g.Halo;
        for (int i = 0; i < halo.Length; i += 2)
        {
            int yy = y + halo[i], xx = x + halo[i + 1];
            if ((uint)yy < (uint)h && (uint)xx < (uint)w && _ink[yy * w + xx]) return false;
        }
        return true;
    }

    /// <summary>
    /// Fallback when the ink columns cannot be cut into glyphs (something white touches the band):
    /// every rendered glyph is looked for pixel-exactly inside its own box; the matches (digits on
    /// one line, ':' between them) are chained left to right at most maxGap columns apart and must
    /// give exactly one "H..H:MM". -1 when none or more than one does.
    /// </summary>
    int ScanTimer(int w, int h, int maxGap)
    {
        _found.Clear();
        foreach (var g in Bits())
            for (int y = 0; y + g.H <= h; y++)
                for (int x = 0; x + g.W <= w; x++)
                    if (At(g, x, y, w, h)) _found.Add((g.Ch, x, g.W, y, y + g.H - 1, g.FontHeight));
        if (_found.Count < 4) return -1;
        _found.Sort((a, b) => a.X.CompareTo(b.X));

        int result = -1;
        if (_inChain.Length < _found.Count) _inChain = new bool[_found.Count * 2];
        Array.Clear(_inChain);
        // Each digit not already inside an earlier chain is a possible first glyph (never the middle
        // of the hours); follow the nearest glyph of the same font and line.
        for (int i = 0; i < _found.Count; i++)
        {
            var first = _found[i];
            if (first.Ch == ':' || _inChain[i]) continue;
            int count = 0, end = first.X + first.W - 1, j = i;
            Put(count++, first);
            while (count < MaxGlyphs)
            {
                int next = -1;
                for (int k = j + 1; k < _found.Count; k++)
                {
                    var c = _found[k];
                    if (c.X <= end) continue;   // overlaps the last glyph
                    if (c.X - end - 1 > maxGap) break;
                    if (c.Font != first.Font) continue;
                    bool sameLine = c.Ch == ':' ? c.Top > first.Top && c.Bottom <= first.Bottom
                                                : c.Top == first.Top && c.Bottom == first.Bottom;
                    if (!sameLine) continue;
                    if (next < 0 || c.X < _found[next].X) next = k;
                }
                if (next < 0) break;
                _inChain[next] = true;
                Put(count++, _found[next]);
                end = _found[next].X + _found[next].W - 1;
                j = next;
            }
            int m = Parse(count);
            if (m < 0) continue;
            if (result >= 0 && result != m) return -1;   // two different readings: none
            result = m;
        }
        return result;

        void Put(int slot, (char Ch, int X, int W, int Top, int Bottom, int Font) g) =>
            (_chars[slot], _heights[slot], _tops[slot], _bottoms[slot]) = (g.Ch, g.Font, g.Top, g.Bottom);
    }

    bool ColumnHasInk(int x, int w, int h)
    {
        for (int y = 0; y < h; y++) if (_ink[y * w + x]) return true;
        return false;
    }

    /// <summary>One run of ink columns: a single glyph, or (touching glyphs) two that each match.</summary>
    bool AddSegment(int a, int b, int w, int h, ref int count)
    {
        if (Match(a, b, w, h, count)) { count++; return count <= MaxGlyphs; }
        for (int k = a; k < b; k++)
            if (count + 1 < MaxGlyphs && Match(a, k, w, h, count) && Match(k + 1, b, w, h, count + 1)) { count += 2; return true; }
        return false;
    }

    /// <summary>Columns a..b cropped to their ink rows, looked up among the rendered glyphs.</summary>
    bool Match(int a, int b, int w, int h, int slot)
    {
        if (slot >= MaxGlyphs) return false;
        int top = -1, bottom = -1;
        for (int y = 0; y < h && top < 0; y++) for (int x = a; x <= b; x++) if (_ink[y * w + x]) { top = y; break; }
        if (top < 0) return false;
        for (int y = h - 1; y >= 0 && bottom < 0; y--) for (int x = a; x <= b; x++) if (_ink[y * w + x]) { bottom = y; break; }
        int cols = b - a + 1, len = (bottom - top + 1) * (cols + 1) - 1;
        if (_key.Length < len) _key = new char[len * 2];
        int n = 0;
        for (int y = top; y <= bottom; y++)
        {
            if (y > top) _key[n++] = '/';
            for (int x = a; x <= b; x++) _key[n++] = _ink[y * w + x] ? '#' : '.';
        }
        if (!_lookup.TryGetValue(_key.AsSpan(0, n), out var g)) return false;
        (_chars[slot], _heights[slot], _tops[slot], _bottoms[slot]) = (g.Ch, g.Height, top, bottom);
        return true;
    }

    /// <summary>"H..H:MM" with every digit of one font height on one line -> minutes, else -1.</summary>
    int Parse(int count)
    {
        if (count < 4) return -1;
        int colon = -1;
        for (int i = 0; i < count; i++) if (_chars[i] == ':') { if (colon >= 0) return -1; colon = i; }
        if (colon < 1 || colon > 3 || count - colon - 1 != 2) return -1;
        int first = 0, hours = 0, mins = 0;
        for (int i = 0; i < count; i++)
        {
            if (i == colon) continue;
            if (_heights[i] != _heights[first] || _tops[i] != _tops[first] || _bottoms[i] != _bottoms[first]) return -1;
            int d = _chars[i] - '0';
            if (i < colon) hours = hours * 10 + d; else mins = mins * 10 + d;
        }
        if (_heights[colon] != _heights[first]) return -1;
        return mins >= 60 ? -1 : hours * 60 + mins;
    }

    /// <summary>Pixels of the game's light-grey tooltip text (CoverText per channel) next to a dark
    /// outline (every channel at most CoverOutlineMax) inside the rectangle (the kill counter's rule).
    /// The panel's own label box uses the same text colour, so only icon boxes are checked.</summary>
    public int TooltipTextPixels(Frame f, Rectangle r)
    {
        var px = f.Pixels;
        int[] t = _c.CoverText;
        int max = _c.CoverOutlineMax, text = 0;
        for (int y = Math.Max(1, r.Top); y < Math.Min(f.Height - 1, r.Bottom); y++)
            for (int x = Math.Max(1, r.Left); x < Math.Min(f.Width - 1, r.Right); x++)
            {
                int i = (y * f.Width + x) * 4;
                if (In(px[i + 2]) && In(px[i + 1]) && In(px[i])
                    && (Dark(px, i - 4) || Dark(px, i + 4) || Dark(px, i - f.Width * 4) || Dark(px, i + f.Width * 4))) text++;
            }
        return text;

        bool In(int v) => v >= t[0] && v <= t[1];
        bool Dark(ReadOnlySpan<byte> p, int i) => p[i] <= max && p[i + 1] <= max && p[i + 2] <= max;
    }
}

/// <summary>
/// Which pillar is which, from the remaining times alone (slots shift up/left when a pillar ends, so
/// a slot says nothing). A reading continues a pillar when it is not above the pillar's last
/// reading and at most (whole minutes since that reading + 1) below it; pairs are taken smallest
/// difference first. Anything else is a new pillar. A pillar alerts once, when it has been read on
/// StablePolls polls and its time is at or below the threshold (also when first seen already at or
/// below it). Pillars not seen (hidden panel, ended) are kept until their countdown would be
/// ForgetAfterMinutes past zero, so a panel that comes back is the same pillar and never alerts
/// twice. An unconfirmed new pillar not seen again within 3 s is dropped (a misread).
/// HP is part of a pillar's state: it never rises (a higher reading is ignored); a lower one is
/// taken only when read on two consecutive readings of that pillar (the higher of the two; polls
/// where it was not seen, e.g. flash frames, do not break the pair), so one misread never counts. Each HP level (HpLevels, e.g. 75/50/25) alerts once per pillar when the
/// pillar's HP is below it; several levels at once give one alert, for the lowest. A pillar first
/// seen already below a level alerts once on confirmation, like the time alert. In matching, HP
/// only breaks ties of remaining time (a reading above a pillar's HP is the worse match).
/// </summary>
public sealed class WarPillarTracker(int stablePolls, int forgetAfterMinutes, int[]? hpLevels = null, int hpRiseTolerance = 3)
{
    /// <summary>One timer read this poll; <c>Hp</c> -1 when its bar was unreadable.</summary>
    public readonly record struct Reading(int Minutes, int Hp = -1);

    /// <summary>An alert: <c>HpLevel</c> 0 = time alert, else the HP level crossed.</summary>
    public readonly record struct Alert(int Slot, int Minutes, int HpLevel = 0);

    const int NoLevel = int.MaxValue;
    readonly int[] _levels = hpLevels ?? [];

    sealed class Pillar
    {
        public int Minutes, Reads;
        public DateTime ReadAt;
        public bool Alerted;
        public int Taken;   // poll stamp: matched on this poll
        public int Hp = -1, PendingHp = -1, HpLevelAlerted = NoLevel;
    }

    readonly List<Pillar> _pillars = [];
    readonly List<(int Obs, Pillar P, int Gap, int HpCost)> _pairs = [];
    Pillar?[] _assigned = new Pillar?[8];
    int _stamp;

    /// <summary>Pillars remembered now (tests).</summary>
    public int Count => _pillars.Count;

    /// <summary>Time-only poll (tests).</summary>
    public void Update(IReadOnlyList<int> minutes, DateTime now, int threshold, List<Alert> fire) =>
        Update(minutes.Select(m => new Reading(m)).ToList(), now, threshold, fire, null);

    /// <summary>One poll: the readable timers in slot order. Adds each alert raised now to
    /// <paramref name="fire"/>; <paramref name="hpOut"/> (optional) gets each reading's pillar HP
    /// (never rising; -1 unknown), in the same order.</summary>
    public void Update(IReadOnlyList<Reading> readings, DateTime now, int threshold, List<Alert> fire, List<int>? hpOut)
    {
        _stamp++;
        _pillars.RemoveAll(p => p.Minutes - (now - p.ReadAt).TotalMinutes < -forgetAfterMinutes
                                || (p.Reads < stablePolls && (now - p.ReadAt).TotalSeconds > 3));
        if (_assigned.Length < readings.Count) _assigned = new Pillar?[readings.Count * 2];
        Array.Clear(_assigned);

        _pairs.Clear();
        for (int i = 0; i < readings.Count; i++)
            foreach (var p in _pillars)
            {
                int gap = p.Minutes - readings[i].Minutes;
                if (gap < 0 || gap > (int)((now - p.ReadAt).TotalSeconds / 60) + 1) continue;
                int h = readings[i].Hp, cost = 0;
                if (h >= 0 && p.Hp >= 0) cost = h > p.Hp + hpRiseTolerance ? 1000 : Math.Max(0, p.Hp - h);
                _pairs.Add((i, p, gap, cost));
            }
        // Smallest time difference first; on a tie the closer HP (never rising), then the pillar
        // read more often (established) wins.
        _pairs.Sort((a, b) => a.Gap != b.Gap ? a.Gap.CompareTo(b.Gap)
                            : a.HpCost != b.HpCost ? a.HpCost.CompareTo(b.HpCost)
                            : b.P.Reads.CompareTo(a.P.Reads));
        foreach (var (obs, p, _, _) in _pairs)
        {
            if (_assigned[obs] is not null || p.Taken == _stamp) continue;
            _assigned[obs] = p;
            p.Taken = _stamp;
        }
        // Not seen this poll (a flash frame, a window over it): nothing changes; a pending HP drop
        // stays pending for the pillar's next reading.

        hpOut?.Clear();
        for (int i = 0; i < readings.Count; i++)
        {
            var p = _assigned[i];
            if (p is null)
            {
                p = new Pillar { Taken = _stamp };
                _pillars.Add(p);
            }
            p.Minutes = readings[i].Minutes;
            p.ReadAt = now;
            p.Reads++;
            UpdateHp(p, readings[i].Hp);
            if (!p.Alerted && p.Reads >= stablePolls && p.Minutes <= threshold)
            {
                p.Alerted = true;
                fire.Add(new Alert(i, p.Minutes));
            }
            if (p.Reads >= stablePolls && p.Hp >= 0)
            {
                int level = LevelOf(p.Hp);
                if (level < p.HpLevelAlerted)
                {
                    p.HpLevelAlerted = level;
                    fire.Add(new Alert(i, p.Minutes, level));
                }
            }
            hpOut?.Add(p.Hp);
        }
    }

    void UpdateHp(Pillar p, int h)
    {
        if (h < 0) { p.PendingHp = -1; return; }   // unreadable: never a drop
        if (p.Hp < 0) { p.Hp = h; p.PendingHp = -1; return; }
        if (p.Reads <= stablePolls)
        {
            // Not confirmed yet: the higher reading (a misread is low: something over the bar).
            p.Hp = Math.Max(p.Hp, h);
            p.PendingHp = -1;
            return;
        }
        if (h >= p.Hp) { p.PendingHp = -1; return; }   // never rises
        if (p.PendingHp >= 0)
        {
            p.Hp = Math.Max(p.PendingHp, h);
            p.PendingHp = -1;
        }
        else p.PendingHp = h;
    }

    /// <summary>The lowest level the HP is below, or NoLevel.</summary>
    int LevelOf(int hp)
    {
        int best = NoLevel;
        foreach (int l in _levels) if (hp < l && l < best) best = l;
        return best;
    }
}

/// <summary>
/// Row "เสาวอ": one alert per pillar when its remaining time reaches the user's minutes
/// ("แจ้งเมื่อเหลือ N นาที", default DefaultMinutes, 1..120), with a crop of that pillar's label box
/// and icon. Same checkbox: one alert per pillar per HP level (HpLevels, fixed) when its HP bar
/// drops below it ("เสาวอเลือดเหลือ X%!", same crop). "ตอนนี้": every timer top to bottom with its
/// pillar's HP ("00:15 (80%) · 00:42 (100%)"), "ไม่มี" without panels (no war: never unreadable),
/// "?" only while the game's tooltip text covers a panel (timer or HP bar) that was showing (the
/// only way into "อ่านค่าไม่ได้", as for the kill counter). A box whose timer cannot be read without
/// tooltip text leaves the row as it was; so do panels missing for less than MissingGraceSeconds.
/// </summary>
public sealed class WarPillarDetector : IHudDetector
{
    public const string NoPanelText = "ไม่มี";
    public const int MinMinutes = 1, MaxMinutes = 120;

    readonly WarPillarConfig _c;
    readonly WarPillarFinder _finder;
    readonly WarPillarTracker _tracker;
    readonly HudReadings _readings;
    readonly HudOptions _options;
    readonly List<PillarPanel> _panels = [];
    readonly List<WarPillarTracker.Reading> _values = [];
    readonly List<int> _slots = [], _hp = [], _shown = [];
    readonly List<WarPillarTracker.Alert> _fire = [];
    readonly List<Rectangle> _lastBoxes = [];
    bool _showing;
    DateTime _lastFoundAt;

    public WarPillarDetector(WatcherConfig config, HudReadings readings, HudOptions options)
    {
        _c = config.WarPillar;
        _finder = new WarPillarFinder(_c);
        _tracker = new WarPillarTracker(_c.StablePolls, _c.ForgetAfterMinutes, _c.HpLevels, _c.HpRiseTolerance);
        _readings = readings;
        _options = options;
    }

    public WarPillarFinder Finder => _finder;

    /// <summary>The user's minutes (clamped to 1..120).</summary>
    public int ThresholdMinutes => Math.Clamp(_options.Threshold(AlertIds.WarPillar, _c.DefaultMinutes), MinMinutes, MaxMinutes);

    public static string Format(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    /// <summary>"00:15 (80%)", or "00:15" while the pillar's HP is unknown.</summary>
    public static string Format(int minutes, int hp) => hp < 0 ? Format(minutes) : $"{Format(minutes)} ({hp}%)";

    /// <summary>Remaining minutes of the first (smallest) timer in a "ตอนนี้" text ("00:15 (80%) ·
    /// 00:42 (100%)" or "00:15 · 00:42" -> 15); false for anything else.</summary>
    public static bool TryParseRow(string text, out int minutes)
    {
        minutes = int.MaxValue;
        if (text.Length < 5) return false;
        foreach (var item in text.Split(" · "))
        {
            var part = item;
            int paren = part.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0)
            {
                var hp = part.AsSpan(paren + 2);
                if (!hp.EndsWith("%)") || !int.TryParse(hp[..^2], System.Globalization.NumberStyles.None, null, out _)) { minutes = 0; return false; }
                part = part[..paren];
            }
            int c = part.IndexOf(':');
            if (c < 1 || part.Length - c != 3 || !int.TryParse(part.AsSpan(0, c), System.Globalization.NumberStyles.None, null, out int h)
                || !int.TryParse(part.AsSpan(c + 1), System.Globalization.NumberStyles.None, null, out int m) || m >= 60) { minutes = 0; return false; }
            minutes = Math.Min(minutes, h * 60 + m);
        }
        return true;
    }

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events)
    {
        _finder.Find(frame, scale, _panels);
        bool covered = false, hold = false;
        _values.Clear();
        _slots.Clear();
        for (int i = 0; i < _panels.Count; i++)
        {
            var p = _panels[i];
            if (p.Readable)
            {
                _values.Add(new WarPillarTracker.Reading(p.Minutes, p.Hp));
                _slots.Add(i);
                // Tooltip text over the bar: unreadable (the 10 s rule), never a drop. A bar that
                // cannot be measured without it: the timer still shows, with the pillar's last HP.
                if (p.HpCovered) covered = true;
            }
            else if (p.Covered) covered = true;
            else hold = true;
        }
        // Boxes that were showing on the last poll and are not found now: tooltip text over one of
        // them is a covered panel, not an ended one.
        if (!covered && _lastBoxes.Count > _panels.Count)
            foreach (var box in _lastBoxes)
            {
                bool stillThere = false;
                foreach (var p in _panels) if (p.Box.IntersectsWith(box)) { stillThere = true; break; }
                if (!stillThere && _finder.TooltipTextPixels(frame, box) >= _c.CoverTextMin) { covered = true; break; }
            }

        int threshold = ThresholdMinutes;
        _fire.Clear();
        _tracker.Update(_values, now, threshold, _fire, _hp);
        foreach (var a in _fire)
            events.Add(new WatchEvent(WatchEventKind.Hud,
                a.HpLevel > 0 ? string.Format(_c.HpMessage, a.HpLevel) : string.Format(_c.Message, a.Minutes),
                now, AlertIds.WarPillar, _panels[_slots[a.Slot]].Crop));

        if (covered)
        {
            if (_showing) _readings.Set(AlertIds.WarPillar, RowReading.Unknown);
            return;   // _lastBoxes kept: the panels are still there under the tooltip
        }
        if (_panels.Count > 0) _lastFoundAt = now;
        else if (_showing && (now - _lastFoundAt).TotalSeconds < _c.MissingGraceSeconds)
            return;   // a short gap (flash or black frame): the row keeps its value
        if (_panels.Count == 0)
        {
            _showing = false;
            _lastBoxes.Clear();
            _shown.Clear();
            _readings.Set(AlertIds.WarPillar, NoPanelText);
            return;
        }
        if (hold) return;
        _showing = true;
        _lastBoxes.Clear();
        foreach (var p in _panels) _lastBoxes.Add(p.Box);
        bool reached = false;
        foreach (var v in _values) reached |= v.Minutes <= threshold;
        // Shown: each timer with its pillar's HP (never rising). Rebuilt only when something changed.
        bool same = _shown.Count == _values.Count * 2;
        for (int i = 0; same && i < _values.Count; i++) same = _shown[2 * i] == _values[i].Minutes && _shown[2 * i + 1] == _hp[i];
        var current = _readings.Get(AlertIds.WarPillar);
        if (!same || current.Reached != reached || current.Text == RowReading.Unknown || current.Text == RowReading.None)
        {
            _shown.Clear();
            for (int i = 0; i < _values.Count; i++) { _shown.Add(_values[i].Minutes); _shown.Add(_hp[i]); }
            var parts = new string[_values.Count];
            for (int i = 0; i < parts.Length; i++) parts[i] = Format(_values[i].Minutes, _hp[i]);
            _readings.Set(AlertIds.WarPillar, string.Join(" · ", parts), reached);
        }
    }

    /// <summary>Frames stopped: the row goes blank; the pillars (and which alerted) are kept.</summary>
    public void Reset()
    {
        _lastBoxes.Clear();
        _shown.Clear();
        _readings.Set(AlertIds.WarPillar, RowReading.None);
    }
}
