using System.Globalization;
using System.Text;

namespace ScreenWatcher.Logging;

/// <summary>
/// Small text log (%LOCALAPPDATA%\ScreenWatcher\log.txt, rolled to log.old.txt past 1 MB).
/// Registered secrets (the bot token) are replaced by "***" in every line before it is kept or
/// written, so a library message or exception text that happens to contain it never leaks.
/// Thread-safe; never throws.
/// </summary>
public sealed class AppLog
{
    readonly object _gate = new();
    readonly List<string> _secrets = [];
    readonly string? _path;
    readonly long _maxBytes;

    public AppLog(string? path, long maxBytes = 1024 * 1024)
    {
        _path = path;
        _maxBytes = maxBytes;
    }

    public static string DefaultPath => Path.Combine(Config.AppSettings.DataRoot, "log.txt");

    /// <summary>Raised after each line is written (already scrubbed).</summary>
    public event Action<string>? LineWritten;

    public void AddSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 6) return;
        lock (_gate) if (!_secrets.Contains(secret)) _secrets.Add(secret);
    }

    public void RemoveSecret(string? secret)
    {
        if (secret is null) return;
        lock (_gate) _secrets.Remove(secret);
    }

    public string Scrub(string text)
    {
        lock (_gate)
        {
            foreach (var s in _secrets) text = text.Replace(s, "***", StringComparison.Ordinal);
            return text;
        }
    }

    public void Write(string message)
    {
        string line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {Scrub(message)}";
        lock (_gate)
        {
            if (_path is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var fi = new FileInfo(_path);
                    if (fi.Exists && fi.Length > _maxBytes)
                    {
                        string old = Path.Combine(fi.DirectoryName!, Path.GetFileNameWithoutExtension(_path) + ".old" + fi.Extension);
                        File.Copy(_path, old, overwrite: true);
                        File.WriteAllText(_path, "");
                    }
                    File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        try { LineWritten?.Invoke(line); } catch { /* a listener must never break logging */ }
    }

    public void Write(string message, Exception ex) => Write($"{message}: {ex.GetType().Name}: {ex.Message}");
}
