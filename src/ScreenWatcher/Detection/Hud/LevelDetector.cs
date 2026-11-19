using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

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
    char[] _key = [];

    public LevelReader(LevelConfig config)
    {
        _c = config;
        _font = CountFont.Render(config.FontFace, config.FontMinHeight, config.FontMaxHeight);
        for (int h = config.FontMinHeight; h <= config.FontMaxHeight; h++)
            if (CountFont.Glyph(config.FontFace, h, 'L') is { } l) _lKeys.TryAdd(l, h);
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

    public int? Read(Frame f, HudScale scale)
    {
        _fail = null;
        if (ReadTahoma(f, scale) is { } level) return level;
        return ReadCells(f, scale);
    }

    /// <summary>Ink mask of the area into _ink; returns its size (0 when empty).</summary>
    (int W, int H) Mask(Frame f, Rectangle area)
    {
        int aw = area.Width, ah = area.Height;
        if (aw <= 0 || ah <= 0) return (0, 0);
        if (_ink.Length < aw * ah) _ink = new bool[aw * ah];
        _ah = ah;
        var px = f.Pixels;
        for (int y = 0; y < ah; y++)
        {
            int i = ((area.Y + y) * f.Width + area.X) * 4;
            for (int x = 0; x < aw; x++, i += 4) _ink[y * aw + x] = Ink(px, i);
        }
        return (aw, ah);
    }

    /// <summary>Next run of ink columns from column x (inclusive); false when none is left.</summary>
    bool NextSegment(int aw, int ah, ref int x, out int x0, out int x1, out int y0, out int y1)
    {
        x0 = x1 = y0 = y1 = -1;
        for (; x < aw; x++)
        {
            int top = -1, bottom = -1;
            for (int y = 0; y < ah; y++) if (_ink[y * aw + x]) { if (top < 0) top = y; bottom = y; }
            if (top < 0) { if (x0 >= 0) { x1 = x - 1; return true; } continue; }
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
        int x = 0;
        // 1. The "L": its rows are the digits' rows.
        int top = -1, bottom = -1, prevEnd = -1;
        while (NextSegment(aw, ah, ref x, out int s0, out int s1, out int t0, out int t1))
            if (lLookup.ContainsKey(Key(aw, s0, s1, t0, t1))) { top = t0; bottom = t1; prevEnd = s1; break; }
        if (top < 0) return null;
        // 2. "v" then ".": a 1-column segment ending on the digits' bottom row, within the next two.
        bool dot = false;
        for (int k = 0; k < 2 && !dot; k++)
        {
            if (!NextSegment(aw, ah, ref x, out int s0, out int s1, out int t0, out int t1)) return null;
            dot = s1 == s0 && t1 == bottom && t0 > top;
            prevEnd = s1;
        }
        if (!dot) return null;
        // 3. Digits: full height, adjacent (at most MaxDigitGap blank columns), up to MaxDigits.
        int value = 0, digits = 0;
        while (digits < _c.MaxDigits && NextSegment(aw, ah, ref x, out int s0, out int s1, out int t0, out int t1))
        {
            if (digits > 0 && s0 - prevEnd - 1 > _c.MaxDigitGap) break;   // the space before the percentage
            if (t0 != top || t1 != bottom || !dLookup.TryGetValue(Key(aw, s0, s1, t0, t1), out int d)) return null;   // something else glued to the number
            value = value * 10 + d;
            digits++;
            prevEnd = s1;
        }
        return digits == 0 ? null : value;
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
/// Row "เลเวลอัพ". "ตอนนี้" = "Lv.N" ("?" when unreadable or off-screen). The first stable reading
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

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events) =>
        Update(_reader.Read(frame, scale), now, frame.Width, frame.Height, events);

    /// <summary>One poll's reading (null = unreadable); also called directly by tests.</summary>
    public void Update(int? level, DateTime now, int width, int height, List<WatchEvent> events)
    {
        bool confirmed = _stable.Push(level);
        if (level is not null) _seen = true;
        // "?" after a few unreadable polls once a level was seen in this game session (like the
        // other rows); nothing before that.
        _readings.Set(AlertIds.LevelUp, _stable.Latest is { } l ? $"Lv.{l}" : _seen ? RowReading.Unknown : RowReading.None);
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
