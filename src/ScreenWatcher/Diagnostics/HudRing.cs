using System.Buffers;
using System.Drawing;
using ScreenWatcher.Collection;
using ScreenWatcher.Detection;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Diagnostics;

/// <summary>A copied ring entry. <see cref="Pixels"/> (tightly packed BGRA) is rented from
/// <see cref="ArrayPool{T}.Shared"/>; whoever takes it returns it (a SaveJob does so itself).</summary>
public sealed record RingCrop(string Region, DateTime Time, byte[] Pixels, int Width, int Height, int ClientWidth, int ClientHeight, HudScale Scale);

/// <summary>
/// The last few seconds of every HUD collect region, in memory: a fixed number of slots per region
/// (one per poll), each slot's buffer reused (it grows only when the region's crop gets larger,
/// e.g. after a HUD scale change). Pushed on the polling thread; <see cref="Snapshot"/> may be
/// called from any thread and copies what it returns.
/// </summary>
public sealed class HudRing
{
    sealed class Slot
    {
        public byte[] Buffer = [];
        public int Width, Height, ClientWidth, ClientHeight;
        public DateTime Time;
        public HudScale Scale;
        public bool Valid;
    }

    readonly object _gate = new();
    readonly CollectRegion[] _regions;
    readonly Slot[][] _slots;
    readonly int[] _next;

    public HudRing(IEnumerable<CollectRegion> regions, int slotsPerRegion)
    {
        _regions = regions.ToArray();
        SlotsPerRegion = Math.Max(1, slotsPerRegion);
        _slots = _regions.Select(_ => Enumerable.Range(0, SlotsPerRegion).Select(_ => new Slot()).ToArray()).ToArray();
        _next = new int[_regions.Length];
    }

    public int SlotsPerRegion { get; }
    public IReadOnlyList<CollectRegion> Regions => _regions;

    /// <summary>Bytes held by the slot buffers.</summary>
    public long Bytes
    {
        get { lock (_gate) return _slots.Sum(r => r.Sum(s => (long)s.Buffer.Length)); }
    }

    /// <summary>Stores this frame's crop of every region (overwriting the oldest slot).</summary>
    public void Push(Frame frame, HudScale scale, DateTime now)
    {
        lock (_gate)
        {
            for (int i = 0; i < _regions.Length; i++)
            {
                var rect = _regions[i].Locate(frame.Width, frame.Height, scale);
                if (rect.IsEmpty) continue;
                var s = _slots[i][_next[i]];
                int n = rect.Width * rect.Height * 4;
                if (s.Buffer.Length < n) s.Buffer = new byte[n];
                frame.CopyRect(rect, s.Buffer);
                (s.Width, s.Height, s.ClientWidth, s.ClientHeight, s.Time, s.Scale, s.Valid) =
                    (rect.Width, rect.Height, frame.Width, frame.Height, now, scale, true);
                _next[i] = (_next[i] + 1) % SlotsPerRegion;
            }
        }
    }

    /// <summary>Copies of the stored crops with Time in [from, to), oldest first, optionally of one
    /// region only. The caller owns (and must return) each <see cref="RingCrop.Pixels"/>.</summary>
    public List<RingCrop> Snapshot(DateTime from, DateTime to, string? region = null)
    {
        var list = new List<RingCrop>();
        lock (_gate)
        {
            for (int i = 0; i < _regions.Length; i++)
            {
                if (region is not null && _regions[i].Name != region) continue;
                for (int k = 0; k < SlotsPerRegion; k++)
                {
                    var s = _slots[i][(_next[i] + k) % SlotsPerRegion]; // oldest first
                    if (!s.Valid || s.Time < from || s.Time >= to) continue;
                    int n = s.Width * s.Height * 4;
                    var px = ArrayPool<byte>.Shared.Rent(n);
                    Array.Copy(s.Buffer, px, n);
                    list.Add(new RingCrop(_regions[i].Name, s.Time, px, s.Width, s.Height, s.ClientWidth, s.ClientHeight, s.Scale));
                }
            }
        }
        return list;
    }

    /// <summary>Forgets every entry (buffers are kept).</summary>
    public void Clear()
    {
        lock (_gate)
            foreach (var r in _slots) foreach (var s in r) s.Valid = false;
    }

    public static void Return(IEnumerable<RingCrop> crops)
    {
        foreach (var c in crops) ArrayPool<byte>.Shared.Return(c.Pixels);
    }

    public static Rectangle Bounds(RingCrop c) => new(0, 0, c.Width, c.Height);
}
