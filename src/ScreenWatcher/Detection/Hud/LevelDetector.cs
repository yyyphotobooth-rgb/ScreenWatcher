using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>One read of the level line. PctHundredths: the percentage in hundredths (8669 = 86.69 %),
/// null when it could not be read exactly.</summary>
public readonly record struct LevelLine(int Level, int? PctHundredths)
{
    public static string FormatPct(int hundredths) =>
        (hundredths < 0 ? "-" : "") + $"{Math.Abs(hundredths) / 100}.{Math.Abs(hundredths) % 100:D2}";
    public string PctText => PctHundredths is { } p ? FormatPct(p) : "?";
    public override string ToString() => $"Lv.{Level} {PctText}%";
}

/// <summary>
/// Reads the level from the line "Lv.88 50.61%" at the bottom centre of the client (on the exp
/// bar, under the item bar), light grey (222,222,222) with a black outline.
/// The game draws it character by character in Windows' Tahoma without smoothing (character
/// height 10 px on 1024x768 .. 1920x1080 clients, 11 px on the owner's 1920x1200 client; a whole
/// line rendered at once does not match, its letter spacing differs). Primary read: the line is
/// cut into characters at blank columns, the "L" is found, "v" and "." are skipped and the digits
/// are looked up pixel-exactly among Tahoma digits rendered at start-up (FontMinHeight..Max).
/// Fallback (no Tahoma match): the 4x7 cell table of the first version (1.0 layout, cells sampled
/// at the HUD scale on a stretched HUD).
/// </summary>
public sealed class LevelReader
{
    readonly LevelConfig _c;
    readonly bool[] _prefix;
    readonly int _prefixW;
    readonly (int Digit, bool[] Mask)[] _digits;
    const int GlyphW = 5, GlyphH = 7;
    bool[] _ink = [];
    int _ah;

    readonly CountFont _font;
    readonly Dictionary<string, int> _lKeys = new();
    readonly HashSet<string> _pctKeys = new();
    readonly int[] _lWidths, _splitWidths;
    readonly Dictionary<string, int> _cutDigits;
    char[] _key = [];

    public LevelReader(LevelConfig config)
    {
        _c = config;
        _font = CountFont.Render(config.FontFace, config.FontMinHeight, config.FontMaxHeight);
        for (int h = config.FontMinHeight; h <= config.FontMaxHeight; h++)
        {
            if (CountFont.Glyph(config.FontFace, h, 'L') is { } l) _lKeys.TryAdd(l, h);
            if (CountFont.Glyph(config.FontFace, h, '%') is { } p) _pctKeys.Add(p);
        }
        static int Width(string key) => key.IndexOf('/') < 0 ? key.Length : key.IndexOf('/');
        _lWidths = [.. _lKeys.Keys.Select(Width).Distinct()];
        // Digits whose last column is a single pixel (Tahoma 9/10 "4": the crossbar's end) also
        // without that column: the game draws the line glyph by glyph with a black outline, and
        // the next glyph's outline paints over that pixel when the next glyph starts right after
        // it with ink on that row (live 2026-11-19 18:22..18:34, "47.40%".."48.99%", "4" before
        // 0/4/6/8). Only taken when the next glyph follows after exactly that one blank column.
        var cut = new Dictionary<string, int>();
        var clash = new HashSet<string>();
        foreach (var (key, d) in _font.Digits)
        {
            var rows = key.Split('/');
            int w = rows[0].Length;
            if (w < 2 || rows.Count(r => r[w - 1] == '#') != 1 || rows.All(r => r[w - 2] != '#')) continue;
            string k = string.Join("/", rows.Select(r => r[..(w - 1)]));
            if (_font.Digits.ContainsKey(k) || (cut.TryGetValue(k, out int other) && other != d)) clash.Add(k);
            else cut[k] = d;
        }
        foreach (var k in clash) cut.Remove(k);
        _cutDigits = cut;
        // Piece widths tried when touching glyphs are split: the dot, every digit, the "%".
        _splitWidths = [.. new[] { 1 }.Concat(_font.Digits.Keys.Select(Width)).Concat(_cutDigits.Keys.Select(Width)).Concat(_pctKeys.Select(Width)).Distinct().Order()];
        if (config.Prefix.Length != GlyphH) throw new InvalidDataException("level: prefix must have 7 rows");
        _prefixW = config.Prefix[0].Length;
        _prefix = Parse(config.Prefix, _prefixW);
        _digits = [.. config.Digits.Select(d => (int.Parse(d.Key), Parse(d.Value, GlyphW)))];
    }

    static bool[] Parse(string[] rows, int w)
    {
        if (rows.Length != GlyphH || rows.Any(r => r.Length != w)) throw new InvalidDataException($"level: glyphs must be {w}x{GlyphH}");
        var m = new bool[w * GlyphH];
        for (int y = 0; y < GlyphH; y++)
            for (int x = 0; x < w; x++)
                m[y * w + x] = rows[y][x] == '#';
        return m;
    }

    /// <summary>Native rectangle searched on this client.</summary>
    public Rectangle Area(int width, int height, HudScale scale)
    {
        var s = HudLayout.Snap(scale);
        var a = _c.Area;
        var r = new Rectangle((int)Math.Round(width / 2.0 + a[0] * s.X), (int)Math.Round(height + a[1] * s.Y),
            (int)Math.Round(a[2] * s.X), (int)Math.Round(a[3] * s.Y));
        r.Intersect(new Rectangle(0, 0, width, height));
        return r;
    }

    bool Ink(ReadOnlySpan<byte> px, int i)
    {
        int b = px[i], g = px[i + 1], r = px[i + 2];
        int min = Math.Min(r, Math.Min(g, b)), max = Math.Max(r, Math.Max(g, b));
        return min >= _c.InkMin && max - min <= _c.InkMaxSpread;
    }

    /// <summary>The level, or null when the line is not there or cannot be read.</summary>
    /// <summary>Why the last Read returned null (logs and tests).</summary>
    public string? LastFailure => _fail switch
    {
        null => null,
        "prefix" => $"prefix {_failScore:F2}",
        "digit" => $"digit {_failK}: best {_failD} {_failScore:F2}, next {_failNext:F2}",
        _ => _fail,
    };
    string? _fail;
    double _failScore, _failNext;
    int _failK, _failD;

    public int? Read(Frame f, HudScale scale) => ReadLine(f, scale)?.Level;

    /// <summary>The level (exactly as <see cref="Read"/> reads it: Tahoma, then the 4x7 cells) and the
    /// percentage from a separate exact Tahoma pass (<see cref="ReadPercent(Frame, HudScale, int)"/>);
    /// null when the level itself cannot be read.</summary>
    public LevelLine? ReadLine(Frame f, HudScale scale)
    {
        _fail = null;
        int? level = ReadTahoma(f, scale) ?? ReadCells(f, scale);
        if (level is not { } l) return null;
        string? fail = _fail;
        var line = new LevelLine(l, ReadPercent(f, scale, l));
        _fail = fail;
        return line;
    }

    /// <summary>Ink mask of the area into _ink; returns its size (0 when empty).</summary>
    (int W, int H) Mask(Frame f, Rectangle area)
    {
        int aw = area.Width, ah = area.Height;
        if (aw <= 0 || ah <= 0) return (0, 0);
        if (_ink.Length < aw * ah) _ink = new bool[aw * ah];
        _ah = ah;
        _bandTop = 0;
        _bandBottom = ah - 1;
        var px = f.Pixels;
        for (int y = 0; y < ah; y++)
        {
            int i = ((area.Y + y) * f.Width + area.X) * 4, count = 0;
            for (int x = 0; x < aw; x++, i += 4) if (_ink[y * aw + x] = Ink(px, i)) count++;
            // A row more than half ink is not text: the exp bar's top edge, light grey over some
            // scenery (live 2026-11-19 18:32, 18:33, the row just above the text).
            if (count * 2 > aw) Array.Clear(_ink, y * aw, aw);
        }
        return (aw, ah);
    }

    // Rows that decide whether a column belongs to the text: the whole area until the "L" is found,
    // then only the L's rows. The exp bar's top edge (the row just above the text) can show a stray
    // light-grey pixel; in a column that is blank on the text rows (a gap between glyphs) it glued
    // the two glyphs into one run (live 2026-11-19 18:31:31, "48.25%": pixel between "." and "2").
    // Such a column is now a gap. Ink stacked on a glyph's own columns still counts (the glyph's
    // rows are taken over the whole area), so that glyph stays unreadable rather than guessed.
    int _bandTop, _bandBottom;

    void Band(int top, int bottom) => (_bandTop, _bandBottom) = (top, bottom);

    /// <summary>Next run of ink columns from column x (inclusive); false when none is left.</summary>
    bool NextSegment(int aw, int ah, ref int x, out int x0, out int x1, out int y0, out int y1)
    {
        x0 = x1 = y0 = y1 = -1;
        int yA = Math.Max(0, _bandTop), yB = Math.Min(ah - 1, _bandBottom);
        for (; x < aw; x++)
        {
            bool text = false;
            for (int y = yA; y <= yB && !text; y++) text = _ink[y * aw + x];
            if (!text) { if (x0 >= 0) { x1 = x - 1; return true; } continue; }
            int top = -1, bottom = -1;
            for (int y = 0; y < ah; y++) if (_ink[y * aw + x]) { if (top < 0) top = y; bottom = y; }
            if (x0 < 0) { x0 = x; y0 = top; y1 = bottom; }
            else { y0 = Math.Min(y0, top); y1 = Math.Max(y1, bottom); }
        }
        if (x0 < 0) return false;
        x1 = aw - 1;
        return true;
    }

    /// <summary>The segment's ink as a CountFont key (rows of '#'/'.' joined by '/') in _key.</summary>
    ReadOnlySpan<char> Key(int aw, int x0, int x1, int y0, int y1)
    {
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        if (_key.Length < h * (w + 1)) _key = new char[h * (w + 1)];
        int n = 0;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++) _key[n++] = _ink[y * aw + x] ? '#' : '.';
            if (y < y1) _key[n++] = '/';
        }
        return _key.AsSpan(0, n);
    }

    int? ReadTahoma(Frame f, HudScale scale)
    {
        var area = Area(f.Width, f.Height, scale);
        var (aw, ah) = Mask(f, area);
        if (aw == 0) return null;
        var lLookup = _lKeys.GetAlternateLookup<ReadOnlySpan<char>>();
        var dLookup = _font.Digits.GetAlternateLookup<ReadOnlySpan<char>>();
        var cLookup = _cutDigits.GetAlternateLookup<ReadOnlySpan<char>>();
        int x = 0;
        // 1. The "L": its rows are the digits' rows. At 10 px the L's foot touches the "v": then
        // the L is the first columns of that segment (same rule as the percentage pass).
        int top = -1, bottom = -1, prevEnd = -1;
        bool glued = false;
        while (top < 0 && NextSegment(aw, ah, ref x, out int s0, out int s1, out int t0, out int t1))
        {
            if (lLookup.ContainsKey(Key(aw, s0, s1, t0, t1))) { top = t0; bottom = t1; break; }
            foreach (int w in _lWidths)
                if (s1 - s0 + 1 > w && Rows(aw, ah, s0, s0 + w - 1, out int u0, out int u1) && lLookup.ContainsKey(Key(aw, s0, s0 + w - 1, u0, u1)))
                { top = u0; bottom = u1; glued = true; break; }
        }
        if (top < 0) return null;
        Band(top, bottom);
        // 2. "v" then ".": a 1-column segment ending on the digits' bottom row, within the next two
        // (the next one when the v was glued to the L).
        bool dot = false;
        for (int k = 0; k < (glued ? 1 : 2) && !dot; k++)
        {
            if (!NextSegment(aw, ah, ref x, out int s0, out int s1, out int t0, out int t1)) return null;
            dot = s1 == s0 && t1 == bottom && t0 > top;
            prevEnd = s1;
        }
        if (!dot) return null;
        // 3. Digits: full height, adjacent (at most MaxDigitGap blank columns), up to MaxDigits.
        // A digit missing its last column (see _cutDigits) needs the next digit after exactly one blank column.
        int value = 0, digits = 0;
        bool cutPending = false;
        while (digits < _c.MaxDigits && NextSegment(aw, ah, ref x, out int s0, out int s1, out int t0, out int t1))
        {
            int gap = s0 - prevEnd - 1;
            if (cutPending && gap != 1) return null;
            if (digits > 0 && gap > _c.MaxDigitGap) break;   // the space before the percentage
            if (t0 != top || t1 != bottom) return null;   // something else glued to the number
            var key = Key(aw, s0, s1, t0, t1);
            if (dLookup.TryGetValue(key, out int d)) cutPending = false;
            else if (cLookup.TryGetValue(key, out d)) cutPending = true;
            else return null;
            value = value * 10 + d;
            digits++;
            prevEnd = s1;
        }
        if (cutPending) return null;
        return digits == 0 ? null : value;
    }

    /// <summary>Rows with ink in columns c0..c1 (false when none).</summary>
    bool Rows(int aw, int ah, int c0, int c1, out int top, out int bottom)
    {
        top = bottom = -1;
        for (int y = 0; y < ah; y++)
            for (int c = c0; c <= c1; c++)
                if (_ink[y * aw + c]) { if (top < 0) top = y; bottom = y; break; }
        return top >= 0;
    }

    /// <summary>
    /// Percentage pass, separate from the level read so the level row is unaffected. Same Tahoma
    /// segmentation, but glyphs may touch (Tahoma 10: the L's foot touches the v, a "4" touches
    /// its neighbours, a digit may touch the "%"): a run of ink columns that is not one glyph is
    /// split into pieces that are each exactly a Tahoma glyph. After "Lv." the digits must equal
    /// <paramref name="level"/>, then after a space exactly "N.NN%" / "NN.NN%" / "100.00%".
    /// Hundredths of a percent, or null when anything is missing or not exact.
    /// </summary>
    public int? ReadPercent(Frame f, HudScale scale, int level)
    {
        var area = Area(f.Width, f.Height, scale);
        var (aw, ah) = Mask(f, area);
        if (aw == 0) return null;
        var lLookup = _lKeys.GetAlternateLookup<ReadOnlySpan<char>>();
        int x = 0, top = -1, bottom = -1;
        bool glued = false;
        while (top < 0 && NextSegment(aw, ah, ref x, out int s0, out int s1, out int t0, out int t1))
        {
            if (lLookup.ContainsKey(Key(aw, s0, s1, t0, t1))) { top = t0; bottom = t1; break; }
            foreach (int w in _lWidths)
                if (s1 - s0 + 1 > w && Rows(aw, ah, s0, s0 + w - 1, out int u0, out int u1) && lLookup.ContainsKey(Key(aw, s0, s0 + w - 1, u0, u1)))
                { top = u0; bottom = u1; glued = true; break; }
        }
        if (top < 0) return null;
        Band(top, bottom);
        // "v" then "." (or only "." when the v was glued to the L).
        bool dot = false;
        int prevEnd = -1;
        for (int k = 0; k < (glued ? 1 : 2) && !dot; k++)
        {
            if (!NextSegment(aw, ah, ref x, out int s0, out int s1, out int t0, out int t1)) return null;
            dot = s1 == s0 && t1 == bottom && t0 > top;
            prevEnd = s1;
        }
        if (!dot) return null;
        // The rest of the line as glyph tokens, each with the blank columns before it.
        _tokens.Clear();
        while (_tokens.Count < 16 && NextSegment(aw, ah, ref x, out int s0, out int s1, out _, out _))
        {
            int first = _tokens.Count;
            if (!Tokenize(aw, ah, s0, s1, top, bottom)) break;   // not text of this line: the line ends here
            _tokens[first] = _tokens[first] with { Gap = s0 - prevEnd - 1 };
            prevEnd = s1;
        }
        // A digit missing its last column (see _cutDigits) counts only when the next glyph follows
        // after exactly one blank column (the painted-over one); otherwise the line ends there.
        for (int k = 0; k < _tokens.Count; k++)
            if (_tokens[k].Cut && (k + 1 >= _tokens.Count || _tokens[k + 1].Gap != 1)) { _tokens.RemoveRange(k, _tokens.Count - k); break; }
        int i = 0, n = _tokens.Count, gap = _c.MaxDigitGap;
        bool Next(out char c, out int g)
        {
            (c, g) = i < n ? (_tokens[i].C, _tokens[i].Gap) : ('\0', 0);
            i++;
            return c != '\0';
        }
        // Level digits.
        int lv = 0, digits = 0;
        while (i < n && char.IsAsciiDigit(_tokens[i].C) && _tokens[i].Gap <= gap && digits < _c.MaxDigits)
        {
            lv = lv * 10 + (_tokens[i].C - '0');
            digits++;
            i++;
        }
        if (digits == 0 || lv != level) return null;
        // Whole percent: 1..3 digits, the first after a space.
        gap = _c.PctMaxGap;   // inside the percentage ("1" has a wide left bearing at 11 px)
        int whole = 0, wn = 0;
        while (i < n && char.IsAsciiDigit(_tokens[i].C) && (wn == 0 ? _tokens[i].Gap > gap : _tokens[i].Gap <= gap) && wn < 3)
        {
            whole = whole * 10 + (_tokens[i].C - '0');
            wn++;
            i++;
        }
        if (wn == 0) return null;
        if (!Next(out char c, out int g) || c != '.' || g > gap) return null;
        int frac = 0;
        for (int k = 0; k < 2; k++)
        {
            if (!Next(out c, out g) || !char.IsAsciiDigit(c) || g > gap) return null;
            frac = frac * 10 + (c - '0');
        }
        if (!Next(out c, out g) || c != '%' || g > gap) return null;
        int value = whole * 100 + frac;
        return value <= 10000 ? value : null;
    }

    readonly record struct Token(char C, int X0, int X1, int Gap, bool Cut = false);
    readonly List<Token> _tokens = new(16);

    /// <summary>Columns c0..c1 (touching ink) as glyphs appended to _tokens; false (nothing added)
    /// when they cannot be split into exact glyphs.</summary>
    bool Tokenize(int aw, int ah, int c0, int c1, int top, int bottom)
    {
        if (GlyphAt(aw, ah, c0, c1, top, bottom, true, out bool cut) is { } whole) { _tokens.Add(new Token(whole, c0, c1, 0, cut)); return true; }
        foreach (int w in _splitWidths)
        {
            if (w >= c1 - c0 + 1 || GlyphAt(aw, ah, c0, c0 + w - 1, top, bottom, false, out _) is not { } g) continue;
            int count = _tokens.Count;
            _tokens.Add(new Token(g, c0, c0 + w - 1, 0));
            if (Tokenize(aw, ah, c0 + w, c1, top, bottom)) return true;
            _tokens.RemoveRange(count, _tokens.Count - count);
        }
        return false;
    }

    /// <summary>The glyph exactly filling columns c0..c1: a digit (on the digit rows), "%", or the
    /// dot (one column ending on the digits' bottom row); null otherwise. <paramref name="segmentEnd"/>:
    /// c1 ends a run of ink, so a digit missing its last column (<paramref name="cut"/>) may be there.</summary>
    char? GlyphAt(int aw, int ah, int c0, int c1, int top, int bottom, bool segmentEnd, out bool cut)
    {
        cut = false;
        if (!Rows(aw, ah, c0, c1, out int t0, out int t1)) return null;
        if (c0 == c1 && t1 == bottom && t0 > top && t1 - t0 <= 2) return '.';
        var key = Key(aw, c0, c1, t0, t1);
        if (t0 == top && t1 == bottom && _font.Digits.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out int d)) return (char)('0' + d);
        if (segmentEnd && t0 == top && t1 == bottom && _cutDigits.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out d)) { cut = true; return (char)('0' + d); }
        if (_pctKeys.GetAlternateLookup<ReadOnlySpan<char>>().Contains(key)) return '%';
        return null;
    }

    int? ReadCells(Frame f, HudScale scale)
    {
        var s = HudLayout.Snap(scale);
        var area = Area(f.Width, f.Height, scale);
        if (area.Width <= 0 || area.Height <= 0) return null;
        int aw = area.Width, ah = area.Height;
        if (_ink.Length < aw * ah) _ink = new bool[aw * ah];
        _ah = ah;
        var px = f.Pixels;
        int x0 = int.MaxValue;
        for (int y = 0; y < ah; y++)
        {
            int i = ((area.Y + y) * f.Width + area.X) * 4;
            for (int x = 0; x < aw; x++, i += 4)
                if (_ink[y * aw + x] = Ink(px, i)) x0 = Math.Min(x0, x);
        }
        if (x0 == int.MaxValue) { _fail = "no ink"; return null; }
        // Top of the "L" stem in that column.
        int y0 = -1;
        for (int y = 0; y < ah && y0 < 0; y++) if (_ink[y * aw + x0]) y0 = y;
        int gh = (int)Math.Round(GlyphH * s.Y);
        if (y0 + gh > ah) { _fail = "cut off"; return null; }
        double min = s.X == 1 && s.Y == 1 ? _c.MinScore : _c.MinScoreScaled;
        // Stretched HUD: a source pixel becomes 1 or 2 native ones, so where the source grid starts
        // relative to the first ink pixel is found once on "Lv." (1/8 steps) and used for
        // every digit.
        double px0 = x0, py0 = y0, ps = -1;
        bool exact = s.X == 1 && s.Y == 1;
        int steps = exact ? 1 : 8;
        for (int fy = 0; fy < steps; fy++)
            for (int fx = 0; fx < steps; fx++)
            {
                // The source pixel under the first ink pixel's centre starts up to one stretched pixel before it.
                double xo = exact ? x0 : x0 + 0.5 - fx * s.X / steps, yo = exact ? y0 : y0 + 0.5 - fy * s.Y / steps;
                double sc = Score(aw, xo, yo, s, _prefix, _prefixW);
                if (sc > ps) { ps = sc; px0 = xo; py0 = yo; }
            }
        if (ps < min) { _fail = "prefix"; _failScore = ps; return null; }

        int value = 0, digits = 0;
        for (int k = 0; k < _c.MaxDigits; k++)
        {
            double cx = px0 + (_c.DigitStart + _c.DigitPitch * k) * s.X;
            if (cx + GlyphW * s.X > aw) break;
            double best = -1, second = -1; int bestD = -1;
            foreach (var (d, m) in _digits)
            {
                double sc = Score(aw, cx, py0, s, m, GlyphW);
                if (sc > best) { second = best; best = sc; bestD = d; }
                else if (sc > second) second = sc;
            }
            if (best < min || best - second < _c.MinMargin)
            {
                // A blank cell ends the number (the space before the percentage); anything else: unreadable.
                if (digits > 0 && Blank(aw, (int)Math.Round(cx), y0, s)) break;
                (_fail, _failK, _failD, _failScore, _failNext) = ("digit", k, bestD, best, second);
                return null;
            }
            value = value * 10 + bestD;
            digits++;
        }
        return digits == 0 ? null : value;
    }

    double Score(int aw, double x, double y, HudScale s, bool[] m, int w)
    {
        int good = 0, ah = _ah;
        bool exact = s.X == 1 && s.Y == 1;
        for (int ty = 0; ty < GlyphH; ty++)
            for (int tx = 0; tx < w; tx++)
            {
                bool on;
                if (exact)
                {
                    int xx = (int)x + tx, yy = (int)y + ty;
                    if (xx >= aw || yy >= ah) return 0;
                    on = _ink[yy * aw + xx];
                }
                else
                {
                    // The native pixels whose centres fall in the cell; ink when at least half are.
                    int x0 = (int)Math.Ceiling(x + tx * s.X - 0.5), x1 = Math.Max(x0 + 1, (int)Math.Ceiling(x + (tx + 1) * s.X - 0.5));
                    int y0 = (int)Math.Ceiling(y + ty * s.Y - 0.5), y1 = Math.Max(y0 + 1, (int)Math.Ceiling(y + (ty + 1) * s.Y - 0.5));
                    if (x0 < 0 || y0 < 0 || x1 > aw || y1 > ah) return 0;
                    int ink = 0, all = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++) { all++; if (_ink[yy * aw + xx]) ink++; }
                    on = ink * 2 >= all;
                }
                if (on == m[ty * w + tx]) good++;
            }
        return (double)good / (w * GlyphH);
    }

    bool Blank(int aw, int x, int y, HudScale s)
    {
        int w = (int)Math.Round((GlyphW - 1) * s.X), h = (int)Math.Round(GlyphH * s.Y);
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < Math.Min(aw, x + w); xx++)
                if (_ink[yy * aw + xx]) return false;
        return true;
    }
}

/// <summary>
/// Row "เลเวลอัพ". "ตอนนี้" = "Lv.N · P%" or "Lv.N" ("?" when unreadable or off-screen). The first stable reading
/// (StablePolls in a row) after the app starts or the game is found again is the baseline (no
/// alert). A later stable reading higher by 1..MaxStep alerts once with the full client frame and
/// becomes the baseline. A lower reading or a bigger jump is ignored (reported once per value
/// through <see cref="Ignored"/> for the journal).
/// </summary>
public sealed class LevelDetector : IHudDetector
{
    readonly LevelConfig _c;
    readonly LevelReader _reader;
    readonly HudReadings _readings;
    readonly HudOptions _options;
    readonly StableValue<int> _stable;
    int? _baseline, _lastIgnored;

    public LevelDetector(WatcherConfig config, HudReadings readings, HudOptions options)
    {
        _c = config.Level;
        _reader = new LevelReader(_c);
        _readings = readings;
        _options = options;
        _stable = new StableValue<int>(_c.StablePolls);
        _readings.Set(AlertIds.LevelUp, RowReading.None);
    }

    public LevelReader Reader => _reader;

    /// <summary>When the last level-up alert went out (row switched on); the quest panel stays quiet
    /// for QuestQuietSeconds after it.</summary>
    public DateTime? LastAlertAt { get; private set; }

    /// <summary>Polling thread: a stable reading that was ignored (from, to, reason "lower" / "jump").</summary>
    public event Action<int, int, string>? Ignored;

    /// <summary>Polling thread: the last poll's level reading (null = unreadable).</summary>
    public int? LastLevel { get; private set; }

    /// <summary>Polling thread: the last poll's whole line (level + percentage, read every poll; the
    /// percentage only when its level digits agree with the level read); null = unreadable.</summary>
    public LevelLine? LastLine { get; private set; }

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events)
    {
        // ReadLine reads the level exactly as Read does (Read = ReadLine().Level) plus the percentage.
        LastLine = _reader.ReadLine(frame, scale);
        LastLevel = LastLine?.Level;
        Update(LastLevel, now, frame.Width, frame.Height, events, LastLine?.PctHundredths);
    }

    /// <summary>"ตอนนี้" of the row: "Lv.N · P%" (P with 2 decimals), "Lv.N" when the percentage of
    /// this poll is unreadable.</summary>
    public static string RowText(int level, int? pctHundredths) =>
        pctHundredths is { } p ? $"Lv.{level} · {LevelLine.FormatPct(p)}%" : $"Lv.{level}";

    /// <summary>One poll's reading (null = unreadable); also called directly by tests.
    /// <paramref name="pctHundredths"/> only changes the row text.</summary>
    public void Update(int? level, DateTime now, int width, int height, List<WatchEvent> events, int? pctHundredths = null)
    {
        bool confirmed = _stable.Push(level);
        if (level is not null) _seen = true;
        // "?" after a few unreadable polls once a level was seen in this game session (like the
        // other rows); nothing before that. The percentage is shown only when this poll read it.
        _readings.Set(AlertIds.LevelUp, _stable.Latest is { } l ? RowText(l, level == l ? pctHundredths : null) : _seen ? RowReading.Unknown : RowReading.None);
        if (!confirmed) return;
        int v = _stable.Value!.Value;
        if (_baseline is not { } b) { _baseline = v; return; }
        if (v == b) { _lastIgnored = null; return; }
        if (v > b && v - b <= _c.MaxStep)
        {
            _baseline = v;
            _lastIgnored = null;
            if (_options.IsEnabled(AlertIds.LevelUp)) LastAlertAt = now;
            events.Add(new WatchEvent(WatchEventKind.Hud, string.Format(_c.Message, b, v), now, AlertIds.LevelUp, new Rectangle(0, 0, width, height)));
            return;
        }
        if (_lastIgnored == v) return;
        _lastIgnored = v;
        try { Ignored?.Invoke(b, v, v < b ? "lower" : "jump"); } catch (Exception) { }
    }

    bool _seen;

    /// <summary>Frames stopped: forget half-confirmed readings, blank the row; the baseline stays.</summary>
    public void Reset()
    {
        LastLevel = null;
        LastLine = null;
        _stable.Reset();
        _readings.Set(AlertIds.LevelUp, RowReading.None);
    }

    /// <summary>The game window is gone: the next stable reading is a new baseline.</summary>
    public void EndSession()
    {
        Reset();
        _baseline = _lastIgnored = null;
        _seen = false;
    }
}
