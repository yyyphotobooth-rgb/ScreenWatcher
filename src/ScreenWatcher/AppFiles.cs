using System.Reflection;
using ScreenWatcher.Config;
using ScreenWatcher.Logging;

namespace ScreenWatcher;

/// <summary>
/// The app's own read-only files (detectors.json, glyphs.json, icons.json, the Discord bot guide).
/// A normal build has them next to the exe; the single-file publish (dist\ScreenWatcher.exe) has
/// only the exe, so each file is also compiled in as an embedded resource. A file next to the exe
/// wins; otherwise the embedded copy (built from the same source file) is used.
/// </summary>
public static class AppFiles
{
    public const string GuideName = "discord-bot-guide.html";

    /// <summary>"ScreenWatcher 1.1" / "ScreenWatcher 1.1.1": the window title, from the project's &lt;Version&gt;.</summary>
    public static string Title
    {
        get
        {
            var v = typeof(AppFiles).Assembly.GetName().Version;
            return v is null ? "ScreenWatcher" : $"ScreenWatcher {ShortVersion(v)}";
        }
    }

    /// <summary>Major.Minor, plus .Patch only when Patch &gt; 0 (1.1.0 → "1.1", 1.1.1 → "1.1.1").</summary>
    public static string ShortVersion(Version v) => v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

    public static string ReadText(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(path)) return File.ReadAllText(path);
        using var s = OpenEmbedded(fileName) ?? throw new FileNotFoundException("Not next to the exe and not embedded", fileName);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>A path the browser can open: docs\ next to the exe, else the embedded guide written to
    /// the data folder (docs\discord-bot-guide.html, rewritten when it differs). Null if neither.</summary>
    public static string? GuidePath(AppLog? log = null)
    {
        string local = Path.Combine(AppContext.BaseDirectory, "docs", GuideName);
        if (File.Exists(local)) return local;
        try
        {
            using var s = OpenEmbedded(GuideName);
            if (s is null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            string target = Path.Combine(AppSettings.DataRoot, "docs", GuideName);
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Write("Could not write the guide", ex);
            return null;
        }
    }

    static Stream? OpenEmbedded(string fileName) =>
        typeof(AppFiles).Assembly.GetManifestResourceStream("ScreenWatcher.Files." + fileName);
}
