using System.Text;
using ScreenWatcher.Config;

namespace ScreenWatcher.Capture;

/// <summary>Finds the game's top-level window by title using EnumWindows (read-only).</summary>
public sealed class GameWindowFinder(WindowConfig config)
{
    IntPtr _last;

    public static bool IsGameTitle(string title, WindowConfig config) =>
        title.StartsWith(config.TitlePrefix, StringComparison.Ordinal) &&
        !config.ExcludeTitles.Contains(title, StringComparer.Ordinal);

    /// <summary>Returns the game window handle, or IntPtr.Zero if no visible game window exists.</summary>
    public IntPtr Find()
    {
        // Cheap path: the window found last time is still valid and still titled as the game.
        if (_last != IntPtr.Zero && NativeMethods.IsWindow(_last) && NativeMethods.IsWindowVisible(_last) &&
            IsGameTitle(GetTitle(_last), config))
            return _last;

        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((h, _) =>
        {
            if (!NativeMethods.IsWindowVisible(h)) return true;
            if (!IsGameTitle(GetTitle(h), config)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);

        _last = found;
        return found;
    }

    static string GetTitle(IntPtr h)
    {
        int len = NativeMethods.GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
