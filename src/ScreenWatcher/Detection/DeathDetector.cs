using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection;

/// <summary>
/// Death = gauge dark AND dialog present, continuously for at least HoldSeconds.
/// Fires once per death; re-arms only when the HP fill is seen again.
/// </summary>
public sealed class DeathDetector(DeathConfig config) : IFrameDetector
{
    readonly DeathFrameAnalyzer _analyzer = new(config);
    readonly TimeSpan _hold = TimeSpan.FromSeconds(config.HoldSeconds);
    DateTime? _since;

    public bool Armed { get; private set; } = true;

    public WatchEvent? Process(Frame frame, DateTime now) => Process(frame, HudScale.One, now);

    public WatchEvent? Process(Frame frame, HudScale scale, DateTime now) => Update(_analyzer.Analyze(frame, scale), now);

    public WatchEvent? Update(DeathObservation o, DateTime now)
    {
        if (!Armed)
        {
            if (o.HpFilled) Armed = true;
            return null;
        }

        if (!(o.GaugeDead && o.DialogPresent))
        {
            _since = null;
            return null;
        }

        _since ??= now;
        if (now - _since.Value < _hold) return null;

        Armed = false;
        _since = null;
        return new WatchEvent(WatchEventKind.Death, config.Message, now);
    }

    public void ResetTiming() => _since = null;
}
