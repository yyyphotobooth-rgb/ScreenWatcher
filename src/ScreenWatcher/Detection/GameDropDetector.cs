using ScreenWatcher.Config;

namespace ScreenWatcher.Detection;

/// <summary>
/// Game dropped = the game window was seen earlier and is now missing for N consecutive checks
/// (any reason: crash, disconnect, closed). Fires once; re-arms when the window reappears.
/// </summary>
public sealed class GameDropDetector(DropConfig config)
{
    bool _seen, _fired;
    int _missing;

    /// <summary>The window has been seen at least once.</summary>
    public bool Seen => _seen;

    /// <summary>The drop event fired and the window has not come back yet.</summary>
    public bool Fired => _fired;

    public WatchEvent? Update(bool windowFound, DateTime now)
    {
        if (windowFound)
        {
            _seen = true;
            _fired = false;
            _missing = 0;
            return null;
        }

        if (!_seen || _fired) return null;
        if (++_missing < config.MissingChecksToFire) return null;

        _fired = true;
        return new WatchEvent(WatchEventKind.GameDropped, config.Message, now);
    }
}
