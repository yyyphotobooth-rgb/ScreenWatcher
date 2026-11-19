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
    readonly BrightGlyph _check, _close;
    readonly int _w, _h;

    public QuestPanelFinder(QuestPanelConfig config)
    {
        _c = config;
        _check = new BrightGlyph(config.CheckMask);
        _close = new BrightGlyph(config.CloseMask);
        if (_close.W != _check.W || _close.H != _check.H) throw new InvalidDataException("questPanel: checkMask and closeMask must be the same size");
        (_w, _h) = (_check.W, _check.H);
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

    bool Glyph(Frame f, int x, int y, HudScale s, BrightGlyph g) =>
        g.Match(f, x, y, s, _c.BrightDelta, s.X == 1 && s.Y == 1 ? _c.MinMatch : _c.MinMatchScaled);

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
