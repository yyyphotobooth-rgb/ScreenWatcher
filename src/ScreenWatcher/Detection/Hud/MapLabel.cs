using System.Drawing;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>A small owned BGRA picture (tightly packed).</summary>
public sealed record BgraImage(byte[] Pixels, int Width, int Height)
{
    public static readonly BgraImage Empty = new([], 0, 0);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static BgraImage Copy(Frame frame, Rectangle rect)
    {
        rect.Intersect(new Rectangle(0, 0, frame.Width, frame.Height));
        if (rect.Width <= 0 || rect.Height <= 0) return Empty;
        var px = new byte[rect.Width * rect.Height * 4];
        frame.CopyRect(rect, px);
        return new BgraImage(px, rect.Width, rect.Height);
    }
}

/// <summary>
/// The map label: the map name line at the top right of the client and the zone label ("โซน N")
/// under the minimap, copied as two small crops (no OCR). Taken only when an EXP test starts or
/// ends (never kept in the HUD ring). Two labels are compared on their text pixels only (the
/// label's own colour next to its dark outline), so the scenery behind does not count.
/// </summary>
public sealed record MapLabel(BgraImage Name, BgraImage Zone, HudScale Scale)
{
    /// <summary>Name above zone, both right-aligned (as on screen), dark background.</summary>
    public BgraImage Composite()
    {
        int w = Math.Max(Name.Width, Zone.Width), h = Name.Height + Zone.Height;
        if (w <= 0 || h <= 0) return BgraImage.Empty;
        var px = new byte[w * h * 4];
        var bg = PngEncoder.ComposeBackground;
        for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 1], px[i + 2], px[i + 3]) = (bg.B, bg.G, bg.R, 255);
        void Put(BgraImage img, int top)
        {
            int left = w - img.Width;
            for (int y = 0; y < img.Height; y++)
                Array.Copy(img.Pixels, y * img.Width * 4, px, ((top + y) * w + left) * 4, img.Width * 4);
        }
        Put(Name, 0);
        Put(Zone, Name.Height);
        return new BgraImage(px, w, h);
    }
}

/// <summary>How the end label compares with the start label.</summary>
public readonly record struct MapCompare(bool Comparable, bool Changed, int NameDiff, int ZoneDiff, string Why);

public sealed class MapLabelReader
{
    readonly ExpTestConfig _c;
    readonly CollectRegion _name, _zone;

    public MapLabelReader(ExpTestConfig config)
    {
        _c = config;
        _name = new CollectRegion(new CollectRegionConfig("mapName", "topRight", config.MapNameRect, []));
        _zone = new CollectRegion(new CollectRegionConfig("mapZone", "topRight", config.MapZoneRect, []));
    }

    public (Rectangle Name, Rectangle Zone) Locate(int width, int height, HudScale scale) =>
        (_name.Locate(width, height, scale), _zone.Locate(width, height, scale));

    /// <summary>Copies the two crops (allocates; call only at a test's start / end).</summary>
    public MapLabel Capture(Frame frame, HudScale scale)
    {
        var (n, z) = Locate(frame.Width, frame.Height, scale);
        return new MapLabel(BgraImage.Copy(frame, n), BgraImage.Copy(frame, z), scale);
    }

    /// <summary>Text pixels of a crop: colour inside the ink box and a dark outline pixel within one pixel.</summary>
    public static bool[] TextMask(BgraImage img, TextInk ink)
    {
        int w = img.Width, h = img.Height;
        var m = new bool[Math.Max(0, w * h)];
        var p = img.Pixels;
        bool Dark(int x, int y)
        {
            int i = (y * w + x) * 4;
            return p[i] <= ink.OutlineMax && p[i + 1] <= ink.OutlineMax && p[i + 2] <= ink.OutlineMax;
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int b = p[i], g = p[i + 1], r = p[i + 2];
                if (r < ink.Min[0] || g < ink.Min[1] || b < ink.Min[2] || r > ink.Max[0] || g > ink.Max[1] || b > ink.Max[2]) continue;
                bool outline = false;
                for (int dy = -1; dy <= 1 && !outline; dy++)
                    for (int dx = -1; dx <= 1 && !outline; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if ((dx != 0 || dy != 0) && xx >= 0 && yy >= 0 && xx < w && yy < h && Dark(xx, yy)) outline = true;
                    }
                m[y * w + x] = outline;
            }
        return m;
    }

    /// <summary>
    /// Start vs end: each part's text masks are compared pixel by pixel. Changed when either part
    /// differs in more than its allowed number of pixels (x scale^2). A part not seen on one side
    /// (too few text pixels, e.g. covered) is not compared; when neither part can be compared the
    /// result is not comparable (no change is claimed). Different client sizes are not comparable.
    /// </summary>
    public MapCompare Compare(MapLabel a, MapLabel b)
    {
        if (a.Name.Width != b.Name.Width || a.Name.Height != b.Name.Height || a.Zone.Width != b.Zone.Width || a.Zone.Height != b.Zone.Height)
            return new(false, false, -1, -1, "size");
        double area = a.Scale.X * a.Scale.Y;
        int minText = (int)Math.Ceiling(_c.MapMinTextPixels * area);
        int? Diff(BgraImage x, BgraImage y, TextInk ink)
        {
            var mx = TextMask(x, ink);
            var my = TextMask(y, ink);
            int cx = mx.Count(v => v), cy = my.Count(v => v);
            if (cx < minText || cy < minText) return null;
            int d = 0;
            for (int i = 0; i < mx.Length; i++) if (mx[i] != my[i]) d++;
            return d;
        }
        int? name = Diff(a.Name, b.Name, _c.MapNameInk), zone = Diff(a.Zone, b.Zone, _c.MapZoneInk);
        if (name is null && zone is null) return new(false, false, -1, -1, "not seen");
        bool nameChanged = name > _c.MapNameMaxDiffPixels * area, zoneChanged = zone > _c.MapZoneMaxDiffPixels * area;
        return new(true, nameChanged || zoneChanged, name ?? -1, zone ?? -1,
            nameChanged ? "name" : zoneChanged ? "zone" : name is null ? "zone only" : zone is null ? "name only" : "same");
    }
}
