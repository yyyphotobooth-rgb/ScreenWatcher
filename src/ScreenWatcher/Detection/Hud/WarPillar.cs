using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>One war pillar panel found on a frame, left to right. <c>Minutes</c> = remaining time
/// (HH*60+MM), -1 when the timer could not be read; <c>Covered</c>: unreadable and the game's
/// tooltip text lies over the icon box.</summary>
public readonly record struct PillarPanel(Rectangle Box, Rectangle Crop, int Minutes, bool Covered)
{
    public bool Readable => Minutes >= 0;
}

/// <summary>
/// Finds the war pillar ("เสาวอ") panels under the top-left gauge and reads each countdown.
/// A panel is recognised by its icon box: a 1-px light outline with a 1-px blue line (FrameColor)
/// inside it; the blue line's top-left corner is the panel's reference point. The strip at FrameY
/// (+-SearchY) is searched from SearchLeft to the right edge, so any number of panels in any slot
/// is found. The timer is Windows' Tahoma without smoothing (white, black outline): each glyph
/// is cut out by its ink columns and looked up pixel-exactly among Tahoma digits and ':' rendered
/// at start-up (FontMinHeight..FontMaxHeight, like the item counts). Only "H..H:MM" with all
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

    /// <summary>All panels on the frame, left to right (cleared first).</summary>
    public void Find(Frame f, HudScale scale, List<PillarPanel> into)
    {
        into.Clear();
        var s = HudLayout.Snap(scale);
        int yNom = (int)Math.Round(_c.FrameY * s.Y);
        int sy = s.Y == 1 ? _c.SearchY : (int)Math.Ceiling(_c.SearchY * s.Y) + 1;
        int left = Math.Max(1, (int)Math.Round(_c.SearchLeft * s.X));
        int boxW = (int)Math.Round((_c.FrameWidth + 4) * s.X);
        int right = f.Width - boxW - 1;
        for (int x = left; x < right; x++)
            for (int y = Math.Max(1, yNom - sy); y <= yNom + sy; y++)
            {
                if (!IsFrame(f, x, y, s)) continue;
                into.Add(ReadPanel(f, x, y, s));
                x += boxW;   // next panel starts after this box
                break;
            }
    }

    bool Blue(ReadOnlySpan<byte> px, Frame f, int x, int y)
    {
        if ((uint)x >= (uint)f.Width || (uint)y >= (uint)f.Height) return false;
        int i = (y * f.Width + x) * 4;
        var c = _c.FrameColor;
        return Math.Abs(px[i + 2] - c[0]) + Math.Abs(px[i + 1] - c[1]) + Math.Abs(px[i] - c[2]) <= _c.FrameTolerance;
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
        return new PillarPanel(box, crop, minutes, covered);
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
        for (int x = 0; x < w;)
        {
            if (!ColumnHasInk(x, w, h)) { x++; continue; }
            int a = x;
            while (x < w && ColumnHasInk(x, w, h)) x++;
            if (!AddSegment(a, x - 1, w, h, ref count)) return -1;
        }
        return Parse(count);
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
/// Which pillar is which, from the remaining times alone (slots shift left when a pillar ends, so
/// a slot says nothing). A reading continues a pillar when it is not above the pillar's last
/// reading and at most (whole minutes since that reading + 1) below it; pairs are taken smallest
/// difference first. Anything else is a new pillar. A pillar alerts once, when it has been read on
/// StablePolls polls and its time is at or below the threshold (also when first seen already at or
/// below it). Pillars not seen (hidden panel, ended) are kept until their countdown would be
/// ForgetAfterMinutes past zero, so a panel that comes back is the same pillar and never alerts
/// twice. An unconfirmed new pillar not seen again within 3 s is dropped (a misread).
/// </summary>
public sealed class WarPillarTracker(int stablePolls, int forgetAfterMinutes)
{
    sealed class Pillar
    {
        public int Minutes, Reads;
        public DateTime ReadAt;
        public bool Alerted;
        public int Taken;   // poll stamp: matched on this poll
    }

    readonly List<Pillar> _pillars = [];
    readonly List<(int Obs, Pillar P, int Gap)> _pairs = [];
    Pillar?[] _assigned = new Pillar?[8];
    int _stamp;

    /// <summary>Pillars remembered now (tests).</summary>
    public int Count => _pillars.Count;

    /// <summary>One poll: the readable timers in slot order (minutes). Adds (slot, minutes) to
    /// <paramref name="fire"/> for each pillar that alerts now.</summary>
    public void Update(IReadOnlyList<int> minutes, DateTime now, int threshold, List<(int Slot, int Minutes)> fire)
    {
        _stamp++;
        _pillars.RemoveAll(p => p.Minutes - (now - p.ReadAt).TotalMinutes < -forgetAfterMinutes
                                || (p.Reads < stablePolls && (now - p.ReadAt).TotalSeconds > 3));
        if (_assigned.Length < minutes.Count) _assigned = new Pillar?[minutes.Count * 2];
        Array.Clear(_assigned);

        _pairs.Clear();
        for (int i = 0; i < minutes.Count; i++)
            foreach (var p in _pillars)
            {
                int gap = p.Minutes - minutes[i];
                if (gap >= 0 && gap <= (int)((now - p.ReadAt).TotalSeconds / 60) + 1) _pairs.Add((i, p, gap));
            }
        // Smallest difference first; on a tie the pillar read more often (established) wins.
        _pairs.Sort((a, b) => a.Gap != b.Gap ? a.Gap.CompareTo(b.Gap) : b.P.Reads.CompareTo(a.P.Reads));
        foreach (var (obs, p, _) in _pairs)
        {
            if (_assigned[obs] is not null || p.Taken == _stamp) continue;
            _assigned[obs] = p;
            p.Taken = _stamp;
        }

        for (int i = 0; i < minutes.Count; i++)
        {
            var p = _assigned[i];
            if (p is null)
            {
                p = new Pillar { Taken = _stamp };
                _pillars.Add(p);
            }
            p.Minutes = minutes[i];
            p.ReadAt = now;
            p.Reads++;
            if (!p.Alerted && p.Reads >= stablePolls && p.Minutes <= threshold)
            {
                p.Alerted = true;
                fire.Add((i, p.Minutes));
            }
        }
    }
}

/// <summary>
/// Row "เสาวอ": one alert per pillar when its remaining time reaches the user's minutes
/// ("แจ้งเมื่อเหลือ N นาที", default DefaultMinutes, 1..120), with a crop of that pillar's label box
/// and icon. "ตอนนี้": every timer left to right ("00:15 · 00:42"), "ไม่มี" without panels (no war:
/// never unreadable), "?" only while the game's tooltip text covers a panel that was showing (the
/// only way into "อ่านค่าไม่ได้", as for the kill counter). A box whose timer cannot be read without
/// tooltip text leaves the row as it was.
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
    readonly List<int> _values = [], _slots = [], _shownValues = [];
    readonly List<(int Slot, int Minutes)> _fire = [];
    readonly List<Rectangle> _lastBoxes = [];
    bool _showing;

    public WarPillarDetector(WatcherConfig config, HudReadings readings, HudOptions options)
    {
        _c = config.WarPillar;
        _finder = new WarPillarFinder(_c);
        _tracker = new WarPillarTracker(_c.StablePolls, _c.ForgetAfterMinutes);
        _readings = readings;
        _options = options;
    }

    public WarPillarFinder Finder => _finder;

    /// <summary>The user's minutes (clamped to 1..120).</summary>
    public int ThresholdMinutes => Math.Clamp(_options.Threshold(AlertIds.WarPillar, _c.DefaultMinutes), MinMinutes, MaxMinutes);

    public static string Format(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    /// <summary>Remaining minutes of the first (smallest) timer in a "ตอนนี้" text ("00:15 · 00:42"
    /// -> 15); false for anything else.</summary>
    public static bool TryParseRow(string text, out int minutes)
    {
        minutes = int.MaxValue;
        if (text.Length < 5) return false;
        foreach (var part in text.Split(" · "))
        {
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
            if (p.Readable) { _values.Add(p.Minutes); _slots.Add(i); }
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
        _tracker.Update(_values, now, threshold, _fire);
        foreach (var (slot, minutes) in _fire)
            events.Add(new WatchEvent(WatchEventKind.Hud, string.Format(_c.Message, minutes), now, AlertIds.WarPillar, _panels[_slots[slot]].Crop));

        if (covered)
        {
            if (_showing) _readings.Set(AlertIds.WarPillar, RowReading.Unknown);
            return;   // _lastBoxes kept: the panels are still there under the tooltip
        }
        if (_panels.Count == 0)
        {
            _showing = false;
            _lastBoxes.Clear();
            _shownValues.Clear();
            _readings.Set(AlertIds.WarPillar, NoPanelText);
            return;
        }
        if (hold) return;
        _showing = true;
        _lastBoxes.Clear();
        foreach (var p in _panels) _lastBoxes.Add(p.Box);
        bool reached = false;
        foreach (int v in _values) reached |= v <= threshold;
        var current = _readings.Get(AlertIds.WarPillar);
        if (!_values.SequenceEqual(_shownValues) || current.Reached != reached || current.Text == RowReading.Unknown || current.Text == RowReading.None)
        {
            _shownValues.Clear();
            _shownValues.AddRange(_values);
            _readings.Set(AlertIds.WarPillar, string.Join(" · ", _values.Select(Format)), reached);
        }
    }

    /// <summary>Frames stopped: the row goes blank; the pillars (and which alerted) are kept.</summary>
    public void Reset()
    {
        _lastBoxes.Clear();
        _shownValues.Clear();
        _readings.Set(AlertIds.WarPillar, RowReading.None);
    }
}
