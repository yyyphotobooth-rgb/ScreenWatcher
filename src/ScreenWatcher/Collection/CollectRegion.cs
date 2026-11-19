using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;

namespace ScreenWatcher.Collection;

public enum HudAnchor { TopLeft, TopRight, BottomCenter, Center, BottomRight }

/// <summary>A collect region: config rect (1.0 layout, offset from an anchor) mapped onto a client.</summary>
public sealed class CollectRegion
{
    public string Name { get; }
    public HudAnchor Anchor { get; }
    public IReadOnlyList<ColourKey> Keys { get; }
    /// <summary>Region-specific change threshold at 1.0, or null for the global one.</summary>
    public double? MinChangedPixels { get; }
    readonly int[] _rect;

    public CollectRegion(CollectRegionConfig c)
    {
        if (c.Rect is not { Length: 4 } || c.Rect[2] <= 0 || c.Rect[3] <= 0)
            throw new InvalidDataException($"collect region '{c.Name}': rect must be [x, y, w, h] with w, h > 0");
        Name = c.Name;
        Anchor = Enum.Parse<HudAnchor>(c.Anchor, ignoreCase: true);
        _rect = c.Rect;
        Keys = c.Keys ?? [];
        MinChangedPixels = c.MinChangedPixels;
        foreach (var k in Keys)
            if (k.Min is not { Length: 3 } || k.Max is not { Length: 3 })
                throw new InvalidDataException($"collect region '{c.Name}', key '{k.Name}': min/max must be [r, g, b]");
    }

    /// <summary>Client rectangle at this client size and HUD scale, clipped to the client (may be empty).</summary>
    public Rectangle Locate(int clientWidth, int clientHeight, HudScale scale)
    {
        (double ax, double ay) = Anchor switch
        {
            HudAnchor.TopLeft => (0.0, 0.0),
            HudAnchor.TopRight => (clientWidth, 0.0),
            HudAnchor.BottomCenter => (clientWidth / 2.0, clientHeight),
            HudAnchor.BottomRight => (clientWidth, clientHeight),
            _ => (clientWidth / 2.0, clientHeight / 2.0),
        };
        int x = (int)Math.Round(ax + _rect[0] * scale.X);
        int y = (int)Math.Round(ay + _rect[1] * scale.Y);
        int w = (int)Math.Round(_rect[2] * scale.X);
        int h = (int)Math.Round(_rect[3] * scale.Y);
        var r = Rectangle.Intersect(new Rectangle(x, y, w, h), new Rectangle(0, 0, clientWidth, clientHeight));
        return r.Width > 0 && r.Height > 0 ? r : Rectangle.Empty;
    }
}

/// <summary>Pre = taken from the in-memory ring just before an auto-collection rule started.</summary>
public enum SaveReason { Change, Heartbeat, Event, Periodic, Pre }

/// <summary>
/// When to save one region: never twice within MinGap; on a content change once MinGap has
/// passed; otherwise a heartbeat once Heartbeat has passed since the last save. The first call
/// (nothing saved yet) saves a heartbeat as the baseline.
/// </summary>
public sealed class SaveSchedule(TimeSpan minGap, TimeSpan heartbeat)
{
    public DateTime? LastSave { get; private set; }

    public SaveReason? Decide(bool changed, DateTime now)
    {
        if (LastSave is not { } last) return SaveReason.Heartbeat;
        var since = now - last;
        if (since < minGap) return null;
        if (changed) return SaveReason.Change;
        return since >= heartbeat ? SaveReason.Heartbeat : null;
    }

    public void MarkSaved(DateTime now) => LastSave = now;
}
