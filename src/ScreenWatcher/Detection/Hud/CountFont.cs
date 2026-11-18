using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenWatcher.Detection.Hud;

/// <summary>
/// Digit bitmaps of the item-count font. The game draws the counts with Windows' own Tahoma, no
/// smoothing, at a pixel size that grows with the HUD (character height 11 px at HUD scale 1.0,
/// 15 px in the ~1.3 sample). The same GDI call reproduces the digits pixel for pixel, so the
/// shapes for every size are rendered once at start-up instead of being stored.
/// Key format: rows of '#'/'.' joined by '/'. This only draws text into an off-screen bitmap of
/// this process; it does not touch the game.
/// </summary>
public sealed class CountFont
{
    /// <summary>Glyph bitmap -> digit, for all rendered sizes together.</summary>
    public Dictionary<string, int> Digits { get; }
    /// <summary>Digit heights (rows) that exist among the rendered sizes.</summary>
    public IReadOnlySet<int> Heights { get; }
    public int MaxWidth { get; }

    CountFont(Dictionary<string, int> digits)
    {
        Digits = digits;
        Heights = digits.Keys.Select(k => k.Count(c => c == '/') + 1).ToHashSet();
        MaxWidth = digits.Keys.Max(k => k.IndexOf('/'));
    }

    /// <summary>The digits as given (config), without rendering.</summary>
    public static CountFont FromRows(IReadOnlyDictionary<string, string[]> glyphs) =>
        new(glyphs.ToDictionary(g => string.Join("/", g.Value), g => int.Parse(g.Key)));

    /// <summary>Renders the digits 0-9 at every character height in the range. A bitmap that
    /// would mean two different digits (never seen) is dropped.</summary>
    public static CountFont Render(string face, int minHeight, int maxHeight, IReadOnlyDictionary<string, string[]>? always = null)
    {
        var map = new Dictionary<string, int>();
        var clash = new HashSet<string>();
        void Add(string key, int digit)
        {
            if (clash.Contains(key)) return;
            if (map.TryGetValue(key, out int have) && have != digit) { map.Remove(key); clash.Add(key); }
            else map[key] = digit;
        }
        if (always is not null) foreach (var g in always) Add(string.Join("/", g.Value), int.Parse(g.Key));
        using var bmp = new Bitmap(64, 64, PixelFormat.Format32bppRgb);
        for (int h = minHeight; h <= maxHeight; h++)
            for (int d = 0; d < 10; d++)
                if (Glyph(bmp, face, h, (char)('0' + d)) is { } key) Add(key, d);
        return new CountFont(map);
    }

    /// <summary>One character's bitmap at the given character height, or null if nothing was drawn.</summary>
    public static string? Glyph(string face, int charHeight, char ch)
    {
        using var bmp = new Bitmap(64, 64, PixelFormat.Format32bppRgb);
        return Glyph(bmp, face, charHeight, ch);
    }

    static string? Glyph(Bitmap bmp, string face, int charHeight, char ch)
    {
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            IntPtr hdc = g.GetHdc();
            // Negative height = character height in pixels; quality 3 = NONANTIALIASED_QUALITY.
            IntPtr font = CreateFontW(-charHeight, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 3, 0, face);
            try
            {
                IntPtr old = SelectObject(hdc, font);
                SetTextColor(hdc, 0xFFFFFF);
                SetBkColor(hdc, 0);
                TextOutW(hdc, 12, 12, ch.ToString(), 1);
                SelectObject(hdc, old);
            }
            finally
            {
                DeleteObject(font);
                g.ReleaseHdc(hdc);
            }
        }
        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).R > 128) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        if (x1 < 0) return null;
        var sb = new StringBuilder();
        for (int y = y0; y <= y1; y++)
        {
            if (y > y0) sb.Append('/');
            for (int x = x0; x <= x1; x++) sb.Append(bmp.GetPixel(x, y).R > 128 ? '#' : '.');
        }
        return sb.ToString();
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr hdc, uint colour);
    [DllImport("gdi32.dll")] static extern uint SetBkColor(IntPtr hdc, uint colour);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool TextOutW(IntPtr hdc, int x, int y, string text, int length);
}
