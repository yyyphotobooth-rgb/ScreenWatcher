using System.Drawing;

namespace ScreenWatcher.Detection;

public enum WatchEventKind { Death, GameDropped, Hud }

/// <summary>An event to alert about. <c>Hud</c> events (ammo, fuel, kill counter, items) carry the
/// alert row id they belong to and the frame rectangle of the HUD part concerned.</summary>
public sealed record WatchEvent(WatchEventKind Kind, string Message, DateTime Time, string? AlertId = null, Rectangle Crop = default);

/// <summary>
/// A detector that looks at captured client-area frames and raises at most one event per poll
/// (death). HUD value readers implement <see cref="Hud.IHudDetector"/> instead.
/// </summary>
public interface IFrameDetector
{
    /// <summary>Called once per poll with a fresh frame and the measured HUD scale (config
    /// coordinates are the 1.0 layout). Returns an event when one fires.</summary>
    WatchEvent? Process(Imaging.Frame frame, HudScale scale, DateTime now);

    /// <summary>Called when frames stop arriving (paused, minimised, game gone) so hold timers restart.</summary>
    void ResetTiming();
}
