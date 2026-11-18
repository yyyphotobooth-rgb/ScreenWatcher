using System.Drawing;

namespace ScreenWatcher.Detection;

public enum WatchEventKind { Death, GameDropped, Hud }

/// <summary>An event to alert about. <c>Hud</c> events (ammo, fuel, kill counter, items) carry the
/// alert row id they belong to and the frame rectangle of the HUD part concerned. <c>Image</c>,
/// when set, is the crop to send instead of cutting <c>Crop</c> out of the current frame (kill
/// counter: the last crop in which the number was read, because the panel is gone by then).</summary>
public sealed record WatchEvent(WatchEventKind Kind, string Message, DateTime Time, string? AlertId = null, Rectangle Crop = default,
    CropImage? Image = null);

/// <summary>A kept HUD crop: tightly packed BGRA (owned, never reused) and the value read on it.</summary>
public sealed record CropImage(byte[] Pixels, int Width, int Height, int Value);

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
