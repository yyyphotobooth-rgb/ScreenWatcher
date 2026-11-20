using System.Drawing;
using System.Text.Json;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>Icon pictures of the watched items: for each, the mean colour of every 2x2 pixel
/// block of the 28x28 slot interior (14x14 cells, 1.0 layout), from labelled item-bar crops.
/// Stored in icons.json.</summary>
public sealed class IconSet
{
    public const string FileName = "icons.json";
    public const int Cells = 14;

    /// <summary>Icon name -> Cells*Cells*3 bytes (row-major, R G B).</summary>
    public IReadOnlyDictionary<string, byte[]> Icons { get; }

    public IconSet(IReadOnlyDictionary<string, string[]> rows)
    {
        var d = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, r) in rows)
        {
            if (r.Length != Cells || r.Any(s => s.Length != Cells * 6))
                throw new InvalidDataException($"icons: '{name}' must be {Cells} rows of {Cells * 6} hex characters");
            var px = new byte[Cells * Cells * 3];
            for (int y = 0; y < Cells; y++)
                for (int i = 0; i < Cells * 3; i++)
                    px[y * Cells * 3 + i] = Convert.ToByte(r[y].Substring(i * 2, 2), 16);
            d[name] = px;
        }
        Icons = d;
    }

    public static IconSet Load(string path) => Parse(File.ReadAllText(path), path);

    public static IconSet Parse(string json, string source = FileName) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string[]>>(json) ?? throw new InvalidDataException($"Empty icon file: {source}"));
}

/// <summary>One watched icon found on the bar. Count is null when the digits could not be read.</summary>
public readonly record struct FoundItem(string ItemId, int Slot, int? Count);

/// <summary>What one frame shows of the item bar.</summary>
public sealed class ItemBarReading
{
    /// <summary>Found icons, left to right.</summary>
    public List<FoundItem> Found { get; } = [];
    /// <summary>The bar itself is on screen (slot frames or a known icon seen).</summary>
    public bool BarVisible { get; set; }
    /// <summary>Slots that hold a potion kit whose type cannot be told on this frame (something,
    /// e.g. the game's own mouse cursor, covers the box or its mark). Nothing may be concluded
    /// about a missing potion from such a frame.</summary>
    public List<int> UncertainKits { get; } = [];
    /// <summary>Slots that positively look like an empty slot (whole frame visible, dark navy
    /// interior). Only these (or another item in its slot) let an item count as gone; anything
    /// else drawn over a slot (tooltip, window, cursor) says nothing about what is under it.</summary>
    public List<int> EmptySlots { get; } = [];
}

/// <summary>
/// Finds the watched items on the bottom-centre item bar BY ICON (an item can sit in any of the
/// ten slots) and reads the small white count at each icon's top-right.
/// Icons: every slot's interior is compared, cell by cell, with the stored icon pictures; only the
/// cells that the count, the hotkey digit and the grade letter never cover are used. Potion kits
/// share one box picture (grades A, B and C differ only in the letter, which is not compared), so
/// their type comes from the mark on the box: the MarkCells alone are compared with each kit's
/// picture, and the nearest must be close and clearly nearer than the next. A kit whose mark is
/// covered, or a slot that is kit-like but too far off (covered by the game's cursor), is reported
/// as uncertain instead of being guessed.
/// Counts: the game draws them with a crisp 5x8 pixel font that is NOT stretched with the HUD, so
/// the digits are matched pixel-exactly: pure white pixels fenced in by the black outline.
/// </summary>
public sealed class ItemBarReader
{
    readonly ItemsConfig _c;
    readonly (ItemDef Def, byte[] Icon)[] _icons;
    readonly (ItemDef Def, byte[] Icon)[] _marks;
    readonly bool[] _cellMark = new bool[IconSet.Cells * IconSet.Cells];
    readonly int _cellsMark;
    readonly CountFont _font;
    char[] _key = [];
    readonly bool[] _cellUsed = new bool[IconSet.Cells * IconSet.Cells];
    readonly int _cellsUsed;
    readonly byte[] _cell = new byte[IconSet.Cells * IconSet.Cells * 3];
    bool[] _mask = [];

    public ItemBarReader(ItemsConfig config, IconSet icons, CountFont? font = null)
    {
        _c = config;
        // The verified 1.0 digits from the config always count; the other sizes are rendered.
        _font = font ?? CountFont.Render(config.CountFontFace, config.CountFontMinHeight, config.CountFontMaxHeight, config.CountGlyphs);
        _icons = config.Items.SelectMany(d => d.Icons.Select(i =>
            (d, icons.Icons.TryGetValue(i, out var px) ? px : throw new InvalidDataException($"items: no icon '{i}' in {IconSet.FileName}")))).ToArray();
        _marks = config.Items.Where(d => d.Mark is not null).Select(d =>
            (d, icons.Icons.TryGetValue(d.Mark!, out var px) ? px : throw new InvalidDataException($"items: no icon '{d.Mark}' in {IconSet.FileName}"))).ToArray();
        var m = config.MarkCells;
        for (int cy = 0; cy < IconSet.Cells; cy++)
            for (int cx = 0; cx < IconSet.Cells; cx++)
            {
                if (_cellUsed[cy * IconSet.Cells + cx] = config.IconRows.Any(r => cy >= r[0] && cy <= r[1] && cx >= r[2] && cx <= r[3])) _cellsUsed++;
                if (_cellMark[cy * IconSet.Cells + cx] = cy >= m[0] && cy <= m[1] && cx >= m[2] && cx <= m[3])
                {
                    if (!_cellUsed[cy * IconSet.Cells + cx]) throw new InvalidDataException("items: markCells must lie inside iconRows");
                    _cellsMark++;
                }
            }
    }

    public void Read(Frame frame, HudScale scale, ItemBarReading result)
    {
        result.Found.Clear();
        result.UncertainKits.Clear();
        result.EmptySlots.Clear();
        var s = HudLayout.Snap(scale);
        bool exact = s.X == 1 && s.Y == 1;
        double cx = frame.Width / 2.0;
        int search = exact ? _c.SearchPx : _c.SearchPxScaled;

        for (int k = 0; k < _c.Slots; k++)
        {
            // Nominal top-left of the slot's 28x28 interior.
            double left = cx + (_c.SlotLeft + _c.SlotPitch * k + 1) * s.X, top = frame.Height + (_c.SlotTop + 1) * s.Y;
            int best = -1, bestX = 0, bestY = 0;
            double bestD = double.MaxValue;
            for (int dy = -search; dy <= search; dy++)
                for (int dx = -search; dx <= search; dx++)
                {
                    double x = Math.Round(left) + dx, y = Math.Round(top) + dy;
                    if (!SampleCells(frame, x, y, s, exact)) continue;
                    for (int i = 0; i < _icons.Length; i++)
                    {
                        double d = Distance(_icons[i].Icon);
                        if (d < bestD) { bestD = d; best = i; bestX = (int)x; bestY = (int)y; }
                    }
                }
            if (best < 0) continue;
            var def = _icons[best].Def;
            if (bestD > (def.MaxDistance ?? _c.IconMaxDistance))
            {
                // Kit-like but too far off: a kit with something drawn over it (no other slot
                // content comes this close to the kit pictures).
                if (def.Mark is not null && bestD <= _c.KitUncertainDistance) result.UncertainKits.Add(k);
                else if (IsEmptySlot(frame, left, top, s, exact, search)) result.EmptySlots.Add(k);
                continue;
            }
            if (def.Mark is not null)
            {
                // Potion kit: the type is the mark on the box.
                SampleCells(frame, bestX, bestY, s, exact);
                var potion = MarkType();
                if (potion is null) { result.UncertainKits.Add(k); continue; }
                def = potion;
            }
            int right = bestX + (int)Math.Round(28 * s.X);
            result.Found.Add(new FoundItem(def.Id, k, ReadCount(frame, right, bestY, s)));
        }
        result.BarVisible = result.Found.Count > 0 || SlotFramesVisible(frame, s);
    }

    /// <summary>The slot whose interior starts at (left, top) looks like a real empty slot: its whole
    /// frame is visible (a tooltip or window over the bar hides at least one edge) and its interior
    /// is the empty slot's dark navy (not black like a tooltip body, not an icon).</summary>
    bool IsEmptySlot(Frame f, double left, double top, HudScale s, bool exact, int search)
    {
        double fx0 = left - s.X, fy0 = top - s.Y, fx1 = left + 28 * s.X, fy1 = top + 28 * s.Y;   // frame lines (1.0: 1 px each)
        if (!EdgeVisible(f, fx0, fy0, fx1, horizontal: true, search) || !EdgeVisible(f, fx0, fy1, fx1, horizontal: true, search)
            || !EdgeVisible(f, fy0, fx0, fy1, horizontal: false, search) || !EdgeVisible(f, fy0, fx1, fy1, horizontal: false, search))
            return false;
        if (!SampleCells(f, Math.Round(left), Math.Round(top), s, exact)) return false;
        int navy = 0;
        for (int c = 0; c < _cellUsed.Length; c++)
        {
            if (!_cellUsed[c]) continue;
            int o = c * 3, r = _cell[o], g = _cell[o + 1], b = _cell[o + 2];
            if (r <= _c.EmptyMaxChannel && g <= _c.EmptyMaxChannel && b <= _c.EmptyMaxChannel && b - r >= _c.EmptyMinBlueOverRed) navy++;
        }
        return navy >= _cellsUsed * _c.EmptyNavyMinShare;
    }

    /// <summary>One frame edge from a to b (native) at cross position at (+-search): the best line
    /// shows frame blue on at least EmptyFrameMinShare of the inner part (corners left out).</summary>
    bool EdgeVisible(Frame f, double a, double at, double b, bool horizontal, int search)
    {
        var px = f.Pixels;
        int from = (int)Math.Round(a) + 2, to = (int)Math.Round(b) - 2, c0 = (int)Math.Round(at);
        int len = to - from + 1;
        if (len <= 0) return false;
        for (int d = -search; d <= search; d++)
        {
            int c = c0 + d, blue = 0;
            if (c < 0 || c >= (horizontal ? f.Height : f.Width) || from < 0 || to >= (horizontal ? f.Width : f.Height)) continue;
            for (int t = from; t <= to; t++)
            {
                int i = horizontal ? (c * f.Width + t) * 4 : (t * f.Width + c) * 4;
                int bb = px[i], g = px[i + 1], r = px[i + 2];
                if (r <= _c.FrameMaxR && g >= _c.FrameG[0] && g <= _c.FrameG[1] && bb >= _c.FrameB[0] && bb <= _c.FrameB[1]) blue++;
            }
            if (blue >= len * _c.EmptyFrameMinShare) return true;
        }
        return false;
    }

    /// <summary>Mean colour of each 2x2 block (1.0 layout) of the interior at native (x, y).</summary>
    bool SampleCells(Frame f, double x, double y, HudScale s, bool exact)
    {
        if (x < 1 || y < 1 || x + 28 * s.X + 1 >= f.Width || y + 28 * s.Y + 1 >= f.Height) return false;
        var px = f.Pixels;
        int n = IconSet.Cells;
        for (int cy = 0; cy < n; cy++)
            for (int cxx = 0; cxx < n; cxx++)
            {
                int o = (cy * n + cxx) * 3;
                if (!_cellUsed[cy * n + cxx]) continue;
                if (exact)
                {
                    int i = (((int)y + cy * 2) * f.Width + (int)x + cxx * 2) * 4, j = i + f.Width * 4;
                    _cell[o] = (byte)((px[i + 2] + px[i + 6] + px[j + 2] + px[j + 6]) >> 2);
                    _cell[o + 1] = (byte)((px[i + 1] + px[i + 5] + px[j + 1] + px[j + 5]) >> 2);
                    _cell[o + 2] = (byte)((px[i] + px[i + 4] + px[j] + px[j + 4]) >> 2);
                }
                else
                {
                    // Stretched HUD: mean of the native pixels the block covers.
                    int x0 = (int)Math.Round(x + cxx * 2 * s.X), x1 = Math.Max(x0 + 1, (int)Math.Round(x + (cxx * 2 + 2) * s.X));
                    int y0 = (int)Math.Round(y + cy * 2 * s.Y), y1 = Math.Max(y0 + 1, (int)Math.Round(y + (cy * 2 + 2) * s.Y));
                    int r = 0, g = 0, b = 0, cnt = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int i = (yy * f.Width + x0) * 4;
                        for (int xx = x0; xx < x1; xx++, i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; cnt++; }
                    }
                    _cell[o] = (byte)(r / cnt); _cell[o + 1] = (byte)(g / cnt); _cell[o + 2] = (byte)(b / cnt);
                }
            }
        return true;
    }

    /// <summary>Mean absolute colour difference over the used cells (0 = identical).</summary>
    double Distance(byte[] icon)
    {
        int d = 0;
        for (int c = 0; c < _cellUsed.Length; c++)
        {
            if (!_cellUsed[c]) continue;
            int o = c * 3;
            d += Math.Abs(_cell[o] - icon[o]) + Math.Abs(_cell[o + 1] - icon[o + 1]) + Math.Abs(_cell[o + 2] - icon[o + 2]);
        }
        return (double)d / (_cellsUsed * 3);
    }

    /// <summary>The potion whose kit picture is nearest over the mark cells, if it is within
    /// MarkMaxDistance and at least MarkMinMargin nearer than the next one; else null.</summary>
    ItemDef? MarkType()
    {
        ItemDef? best = null;
        double bestD = double.MaxValue, second = double.MaxValue;
        foreach (var (def, icon) in _marks)
        {
            int d = 0;
            for (int c = 0; c < _cellMark.Length; c++)
            {
                if (!_cellMark[c]) continue;
                int o = c * 3;
                d += Math.Abs(_cell[o] - icon[o]) + Math.Abs(_cell[o + 1] - icon[o + 1]) + Math.Abs(_cell[o + 2] - icon[o + 2]);
            }
            double dd = (double)d / (_cellsMark * 3);
            if (dd < bestD) { second = bestD; bestD = dd; best = def; }
            else if (dd < second) second = dd;
        }
        return bestD <= _c.MarkMaxDistance && second - bestD >= _c.MarkMinMargin ? best : null;
    }

    /// <summary>Reads the count whose right end sits at the icon's top-right (native pixels).</summary>
    int? ReadCount(Frame f, int right, int top, HudScale s)
    {
        int x0 = Math.Max(1, right - (int)Math.Ceiling(_c.CountBoxWidth * s.X)), x1 = Math.Min(f.Width - 2, right + 2);
        int y0 = Math.Max(1, top - 3), y1 = Math.Min(f.Height - 2, top + (int)Math.Round(_c.CountBoxHeight * s.Y));
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        if (w <= 0 || h < 6) return null;
        if (_mask.Length < w * h) _mask = new bool[w * h];
        var px = f.Pixels;
        int minY = h, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = ((y0 + y) * f.Width + x0 + x) * 4;
                bool text = IsWhite(px, i) && Fence(px, i - 4) && Fence(px, i + 4) && Fence(px, i - f.Width * 4) && Fence(px, i + f.Width * 4);
                _mask[y * w + x] = text;
                if (text) { if (y < minY) minY = y; if (y > maxY) maxY = y; }
            }
        int rows = maxY - minY + 1;
        if (!_font.Heights.Contains(rows)) return null;

        int value = 0, digits = 0, start = -1;
        if (_key.Length < rows * (_font.MaxWidth + 1)) _key = new char[rows * (_font.MaxWidth + 1)];
        var key = _key.AsSpan();
        var lookup = _font.Digits.GetAlternateLookup<ReadOnlySpan<char>>();
        for (int x = 0; x <= w; x++)
        {
            bool any = false;
            if (x < w) for (int y = minY; y <= maxY; y++) any |= _mask[y * w + x];
            if (any) { if (start < 0) start = x; continue; }
            if (start < 0) continue;
            int gw = x - start;
            if (gw > _font.MaxWidth) return null;
            int n = 0;
            for (int y = minY; y <= maxY; y++)
            {
                for (int xx = start; xx < x; xx++) key[n++] = _mask[y * w + xx] ? '#' : '.';
                if (y < maxY) key[n++] = '/';
            }
            if (!lookup.TryGetValue(key[..n], out int digit)) return null;
            value = value * 10 + digit;
            if (++digits > 4) return null;
            start = -1;
        }
        return digits == 0 ? null : value;
    }

    bool IsWhite(ReadOnlySpan<byte> px, int i) => px[i] >= _c.CountWhiteMin && px[i + 1] >= _c.CountWhiteMin && px[i + 2] >= _c.CountWhiteMin;

    /// <summary>A neighbour of a digit pixel is either more digit (white) or the black outline.</summary>
    bool Fence(ReadOnlySpan<byte> px, int i) =>
        IsWhite(px, i) || (px[i] <= _c.CountDarkMax && px[i + 1] <= _c.CountDarkMax && px[i + 2] <= _c.CountDarkMax);

    /// <summary>The bar's slot frames: several long horizontal runs of the frame blue on one row
    /// near the slots' top edge.</summary>
    bool SlotFramesVisible(Frame f, HudScale s)
    {
        var px = f.Pixels;
        double cx = f.Width / 2.0;
        int x0 = Math.Max(0, (int)(cx + (_c.SlotLeft - 6) * s.X)), x1 = Math.Min(f.Width - 1, (int)(cx + (_c.SlotLeft + _c.SlotPitch * _c.Slots + 6) * s.X));
        int yc = (int)Math.Round(f.Height + _c.SlotTop * s.Y), span = (int)Math.Ceiling(4 * s.Y), minRun = (int)(_c.FrameMinRun * s.X);
        for (int y = Math.Max(0, yc - span); y <= Math.Min(f.Height - 1, yc + span); y++)
        {
            int runs = 0, run = 0;
            for (int x = x0; x <= x1; x++)
            {
                int i = (y * f.Width + x) * 4;
                int b = px[i], g = px[i + 1], r = px[i + 2];
                bool blue = r <= _c.FrameMaxR && g >= _c.FrameG[0] && g <= _c.FrameG[1] && b >= _c.FrameB[0] && b <= _c.FrameB[1];
                if (blue) run++;
                else { if (run >= minRun) runs++; run = 0; }
            }
            if (run >= minRun) runs++;
            if (runs >= _c.FrameMinRuns) return true;
        }
        return false;
    }
}
