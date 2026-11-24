using System.Drawing;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>
/// Finds a buff's emblem (EXP card: blue winged emblem; handshake: two hands) anywhere along the top
/// buff bar (its slot moves as other buffs come and go). Only the emblem's top part is compared (the countdown text covers the lower
/// half of every buff icon). The emblem (1.0 layout) is resampled once per HUD scale; matching is
/// the mean colour difference per channel at every x and a few rows around the bar's y, with an
/// early exit, so it allocates nothing per poll.
/// </summary>
public sealed class BuffMatcher
{
    readonly BuffConfig _c;
    readonly string _name;
    readonly byte[] _emblem;          // 1.0 layout, RGB
    readonly int _ew, _eh;
    byte[] _t = [];                   // template at the current scale, RGB
    int _tw, _th;
    HudScale _tScale = new(double.NaN, double.NaN);

    public BuffMatcher(BuffConfig config, string name = "buff")
    {
        _c = config;
        _name = name;
        _eh = config.Emblem.Length;
        _ew = _eh == 0 ? 0 : config.Emblem[0].Length / 6;
        if (_eh == 0 || _ew == 0 || config.Emblem.Any(r => r.Length != _ew * 6))
            throw new InvalidDataException($"{_name}.emblem: rows of equal length (6 hex characters per pixel) expected");
        _emblem = new byte[_ew * _eh * 3];
        for (int y = 0; y < _eh; y++)
            for (int i = 0; i < _ew * 3; i++)
                _emblem[y * _ew * 3 + i] = Convert.ToByte(config.Emblem[y].Substring(i * 2, 2), 16);
    }

    /// <summary>Best match on this frame: mean difference (0..255; lower = closer) and where the
    /// emblem's top-left is. Distance is double.MaxValue when nothing came under the limit.</summary>
    public (double Distance, Point At) Find(Frame frame, HudScale scale)
    {
        Prepare(scale);
        bool scaled = Math.Abs(scale.X - 1) > 0.02 || Math.Abs(scale.Y - 1) > 0.02;
        int search = scaled ? _c.SearchYScaled : _c.SearchY;
        int top = scale.MapY(_c.EmblemTop);
        int y0 = Math.Max(0, top - search), y1 = Math.Min(frame.Height - _th, top + search);
        int x0 = Math.Max(0, scale.MapX(_c.SearchLeft)), x1 = frame.Width - _tw;
        var px = frame.Pixels;
        int stride = frame.Width * 4, n = _tw * _th;
        // Early exit as soon as the running sum exceeds what the limit allows over the whole emblem.
        long budget = (long)Math.Ceiling(_c.MaxDistance * n * 3);
        long best = long.MaxValue;
        var at = Point.Empty;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                long limit = Math.Min(budget, best);
                long sum = 0;
                for (int j = 0; j < _th && sum <= limit; j++)
                {
                    int fi = (y + j) * stride + x * 4, ti = j * _tw * 3;
                    for (int i = 0; i < _tw; i++, fi += 4, ti += 3)
                        sum += Math.Abs(px[fi + 2] - _t[ti]) + Math.Abs(px[fi + 1] - _t[ti + 1]) + Math.Abs(px[fi] - _t[ti + 2]);
                }
                if (sum <= limit && sum < best) { best = sum; at = new Point(x, y); }
            }
        return best == long.MaxValue ? (double.MaxValue, Point.Empty) : (best / (3.0 * n), at);
    }

    public bool IsPresent(Frame frame, HudScale scale) => Find(frame, scale).Distance <= _c.MaxDistance;

    /// <summary>Bilinear resample of the 1.0 emblem to this scale (only when the scale changed).</summary>
    void Prepare(HudScale scale)
    {
        if (scale == _tScale) return;
        _tScale = scale;
        _tw = scale.SizeX(_ew);
        _th = scale.SizeY(_eh);
        _t = new byte[_tw * _th * 3];
        for (int y = 0; y < _th; y++)
        {
            double sy = Math.Clamp((y + 0.5) * _eh / _th - 0.5, 0, _eh - 1);
            int ya = (int)sy, yb = Math.Min(ya + 1, _eh - 1);
            double fy = sy - ya;
            for (int x = 0; x < _tw; x++)
            {
                double sx = Math.Clamp((x + 0.5) * _ew / _tw - 0.5, 0, _ew - 1);
                int xa = (int)sx, xb = Math.Min(xa + 1, _ew - 1);
                double fx = sx - xa;
                for (int c = 0; c < 3; c++)
                {
                    double a = _emblem[(ya * _ew + xa) * 3 + c] * (1 - fx) + _emblem[(ya * _ew + xb) * 3 + c] * fx;
                    double b = _emblem[(yb * _ew + xa) * 3 + c] * (1 - fx) + _emblem[(yb * _ew + xb) * 3 + c] * fx;
                    _t[(y * _tw + x) * 3 + c] = (byte)Math.Round(a * (1 - fy) + b * fy);
                }
            }
        }
    }
}

/// <summary>
/// Alert picture of the buff rows: the buff bar only (all icons and their countdown text), not the
/// full frame. Left edge and height are fixed (1.0 layout, scaled); the right end follows the yellow
/// countdown text from the left edge, crossing gaps up to MaxGap (icons without text), so the target
/// HP bar and the quest text further right are left out. Allocation-free scan of a few rows.
/// </summary>
public static class BuffBar
{
    public static Rectangle CropRect(Frame frame, HudScale scale, BuffBarConfig c)
    {
        int w = frame.Width, h = frame.Height;
        int x0 = Math.Clamp(scale.MapX(c.CropLeft), 0, w);
        int height = Math.Min(h, scale.SizeY(c.CropHeight));
        int ty0 = Math.Clamp(scale.MapY(c.TextRows[0]), 0, h - 1), ty1 = Math.Clamp(scale.MapY(c.TextRows[1]), 0, h - 1);
        int maxGap = scale.SizeX(c.MaxGap);
        var px = frame.Pixels;
        int stride = w * 4, last = -1;
        for (int x = x0; x < w; x++)
        {
            if (x - (last < 0 ? x0 : last) > maxGap) break;
            for (int y = ty0; y <= ty1; y++)
            {
                int i = y * stride + x * 4;
                if (px[i + 2] >= c.TextMinRG && px[i + 1] >= c.TextMinRG && px[i] <= c.TextMaxB) { last = x; break; }
            }
        }
        int right = last < 0 ? x0 + scale.SizeX(c.FallbackWidth) : last + 1 + scale.SizeX(c.Margin);
        right = Math.Clamp(right, x0, w);
        return new Rectangle(x0, 0, right - x0, height);
    }
}

/// <summary>
/// A timed buff's alert (second/third line of the kill row): "บัพมอน 300 ตัว ครบ 30 นาทีแล้ว" (EXP card)
/// and "บัพจับมือ ครบ 60 นาทีแล้ว" (handshake). Once the buff was seen, it fires once when it has been
/// absent for AbsentSeconds of continuous polls (any poll that sees it restarts the wait: cursor over
/// the icon, flicker). Re-arms only when the buff is seen again. "ตอนนี้": มีบัพ / ไม่มีบัพ. Not part
/// of the "อ่านค่าไม่ได้" rule (no buff is a normal state).
/// </summary>
public sealed class BuffDetector : IHudDetector, IArmedState
{
    public const string HaveText = "มีบัพ";
    public const string NoneText = "ไม่มีบัพ";

    readonly string _id;
    readonly BuffConfig _c;
    readonly BuffBarConfig _bar;
    readonly BuffMatcher _matcher;
    readonly HudReadings _readings;
    bool _armed;            // seen since the last alert
    DateTime? _absentSince;
    int _absentPolls;

    public BuffDetector(string alertId, BuffConfig config, BuffBarConfig bar, HudReadings readings)
    {
        _id = alertId;
        _c = config;
        _bar = bar;
        _matcher = new BuffMatcher(config, alertId);
        _readings = readings;
    }

    public static BuffDetector ExpCard(WatcherConfig config, HudReadings readings) => new(AlertIds.ExpCard, config.ExpCard, config.BuffBar, readings);
    public static BuffDetector Handshake(WatcherConfig config, HudReadings readings) => new(AlertIds.Handshake, config.Handshake, config.BuffBar, readings);

    public string AlertId => _id;
    public BuffMatcher Matcher => _matcher;

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events)
    {
        if (Update(_matcher.IsPresent(frame, scale), now))
            events.Add(new WatchEvent(WatchEventKind.Hud, _c.Message, now, _id, BuffBar.CropRect(frame, scale, _bar)));
    }

    /// <summary>Logic only (tests): one poll. Returns true when the alert fires.</summary>
    public bool Update(bool present, DateTime now)
    {
        bool fire = false;
        if (present)
        {
            _armed = true;
            _absentSince = null;
            _absentPolls = 0;
        }
        else
        {
            _absentSince ??= now;
            _absentPolls++;
            if (_armed && (now - _absentSince.Value).TotalSeconds >= _c.AbsentSeconds)
            {
                _armed = false;
                fire = true;
            }
        }
        if (present) _readings.Set(_id, HaveText);
        else if (_absentPolls >= _c.NoCardAfterPolls) _readings.Set(_id, NoneText);
        return fire;
    }

    public void CopyArmed(IDictionary<string, bool> into) => into[_id] = _armed;

    /// <summary>Frames stopped: the absence wait starts again (paused time does not count); armed is kept.</summary>
    public void Reset()
    {
        _absentSince = null;
        _absentPolls = 0;
        _readings.Set(_id, RowReading.None);
    }
}
