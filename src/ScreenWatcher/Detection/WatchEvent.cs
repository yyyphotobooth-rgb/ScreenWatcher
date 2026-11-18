namespace ScreenWatcher.Detection;

public enum WatchEventKind { Death, GameDropped }

public sealed record WatchEvent(WatchEventKind Kind, string Message, DateTime Time);

/// <summary>
/// A detector that looks at captured client-area frames. New detectors (e.g. ammo counters)
/// implement this and are added to the list the Watcher runs each poll.
/// </summary>
public interface IFrameDetector
{
    /// <summary>Called once per poll with a fresh frame and the measured HUD scale (config
    /// coordinates are the 1.0 layout). Returns an event when one fires.</summary>
    WatchEvent? Process(Imaging.Frame frame, HudScale scale, DateTime now);

    /// <summary>Called when frames stop arriving (paused, minimised, game gone) so hold timers restart.</summary>
    void ResetTiming();
}
