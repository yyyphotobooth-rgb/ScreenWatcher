using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>What one frame shows of the system message box.</summary>
public struct SystemMessageReading
{
    /// <summary>The framed box's "SYSTEM MESSAGE" title was found.</summary>
    public bool Title;
    /// <summary>Message text (green or yellow, outlined) in the text area: the box is there, with or without its frame.</summary>
    public bool Text;
    /// <summary>The watched phrase (inventory full) is one of the lines.</summary>
    public bool Phrase;
}

/// <summary>
/// Reads the system message box at the bottom right. The phrase is matched by shape: the game
/// draws it with Windows' Tahoma, unsmoothed, in pure yellow; the same GDI call renders it pixel for
/// pixel, so the phrase is rendered once at start-up for each character height in the config range
/// and compared with the yellow pixels of the text area (anywhere in it: lines scroll). The framed
/// box is recognised by its title (BrightGlyph); a frameless box only by its text.
/// Rendering draws into an off-screen bitmap of this process; nothing touches the game.
/// </summary>
public sealed class SystemMessageReader
{
    sealed class Phrase(bool[,] mask)
    {
        public readonly bool[,] Mask = mask;
        public readonly int H = mask.GetLength(0), W = mask.GetLength(1);
        public readonly int FirstX = Enumerable.Range(0, mask.GetLength(1)).First(x => mask[0, x]);
        public readonly (int X, int Y, bool On)[] Keys = KeysOf(mask);

        static (int, int, bool)[] KeysOf(bool[,] m)
        {
            // 24 cells spread over the box (on and off) for the quick reject.
            int h = m.GetLength(0), w = m.GetLength(1);
            return [.. Enumerable.Range(0, 24).Select(k => { int x = (k * 37 + 5) % w, y = (k * 7 + 3) % h; return (x, y, m[y, x]); })];
        }
    }

    readonly InventoryConfig _c;
    readonly Phrase[] _phrases;
    readonly BrightGlyph _title;
    bool[] _yellow = [];

    public SystemMessageReader(InventoryConfig config)
    {
        _c = config;
        var list = new List<Phrase>();
        for (int h = config.FontMinHeight; h <= config.FontMaxHeight; h++)
        {
            var m = CountFont.TextMask(config.FontFace, h, config.Phrase);
            if (m.Length > 0 && !list.Any(p => Same(p.Mask, m))) list.Add(new Phrase(m));
        }
        if (list.Count == 0) throw new InvalidDataException($"inventory: the phrase could not be rendered in {config.FontFace}");
        _phrases = [.. list];
        _title = new BrightGlyph(config.TitleMask);
    }

    static bool Same(bool[,] a, bool[,] b)
    {
        if (a.GetLength(0) != b.GetLength(0) || a.GetLength(1) != b.GetLength(1)) return false;
        for (int y = 0; y < a.GetLength(0); y++)
            for (int x = 0; x < a.GetLength(1); x++)
                if (a[y, x] != b[y, x]) return false;
        return true;
    }

    /// <summary>Phrase sizes rendered (width x height), for logs and tests.</summary>
    public IEnumerable<Size> PhraseSizes => _phrases.Select(p => new Size(p.W, p.H));

    /// <summary>Native rectangle of the text area on this client.</summary>
    public Rectangle TextArea(int width, int height, HudScale scale)
    {
        var s = HudLayout.Snap(scale);
        var a = _c.TextArea;
        int x = (int)Math.Round(width + a[0] * s.X), y = (int)Math.Round(height + a[1] * s.Y);
        var r = new Rectangle(x, y, (int)Math.Round(a[2] * s.X), (int)Math.Round(a[3] * s.Y));
        r.Intersect(new Rectangle(0, 0, width, height));
        return r;
    }

    public SystemMessageReading Read(Frame f, HudScale scale)
    {
        var s = HudLayout.Snap(scale);
        var r = new SystemMessageReading { Title = FindTitle(f, s) };
        var area = TextArea(f.Width, f.Height, scale);
        if (area.Width <= 0 || area.Height <= 0) return r;

        // Yellow and green pixels of the area (one pass; the yellow mask is reused between polls).
        int aw = area.Width, ah = area.Height;
        if (_yellow.Length < aw * ah) _yellow = new bool[aw * ah];
        var px = f.Pixels;
        int text = 0, yellow = 0;
        for (int y = 0; y < ah; y++)
        {
            int i = ((area.Y + y) * f.Width + area.X) * 4;
            for (int x = 0; x < aw; x++, i += 4)
            {
                int b = px[i], g = px[i + 1], rr = px[i + 2];
                bool ye = In(rr, g, b, _c.YellowMin, _c.YellowMax);
                _yellow[y * aw + x] = ye;
                if (ye) { yellow++; text++; }
                else if (In(rr, g, b, _c.GreenMin, _c.GreenMax)) text++;
            }
        }
        r.Text = text >= _c.TextMinPixels;
        if (yellow >= _phrases.Min(p => p.W)) r.Phrase = FindPhrase(aw, ah);
        return r;
    }

    static bool In(int r, int g, int b, int[] min, int[] max) =>
        r >= min[0] && r <= max[0] && g >= min[1] && g <= max[1] && b >= min[2] && b <= max[2];

    bool FindTitle(Frame f, HudScale s)
    {
        int x0 = (int)Math.Round(f.Width + _c.TitleAt[0] * s.X), y0 = (int)Math.Round(f.Height + _c.TitleAt[1] * s.Y);
        double min = s.X == 1 && s.Y == 1 ? _c.TitleMinMatch : _c.TitleMinMatchScaled;
        int search = _c.TitleSearch;
        for (int dy = 0; dy <= search; dy++)
            for (int sy = -1; sy <= 1; sy += 2)
            {
                if (dy == 0 && sy > 0) continue;
                for (int dx = -search; dx <= search; dx++)
                    if (_title.Match(f, x0 + dx, y0 + dy * sy, s, _c.TitleDelta, min)) return true;
            }
        return false;
    }

    /// <summary>Every yellow pixel can be the first ink pixel of the phrase's top row.</summary>
    bool FindPhrase(int aw, int ah)
    {
        foreach (var p in _phrases)
        {
            int allowed = (int)(_c.MaxMismatch * p.W * p.H);
            for (int y = 0; y + p.H <= ah; y++)
                for (int x = 0; x < aw; x++)
                {
                    if (!_yellow[y * aw + x]) continue;
                    int ox = x - p.FirstX;
                    if (ox < 0 || ox + p.W > aw) continue;
                    int miss = 0;
                    foreach (var (kx, ky, on) in p.Keys)
                        if (_yellow[(y + ky) * aw + ox + kx] != on && ++miss > 1) break;
                    if (miss > 1) continue;
                    miss = 0;
                    for (int ty = 0; ty < p.H && miss <= allowed; ty++)
                    {
                        int row = (y + ty) * aw + ox;
                        for (int tx = 0; tx < p.W; tx++)
                            if (_yellow[row + tx] != p.Mask[ty, tx]) miss++;
                    }
                    if (miss <= allowed) return true;
                }
        }
        return false;
    }
}
