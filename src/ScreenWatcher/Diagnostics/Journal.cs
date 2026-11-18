using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenWatcher.Diagnostics;

/// <summary>
/// Event journal for field trials: &lt;root&gt;\yyyy-MM-dd.jsonl, one JSON object per line
/// (<c>{"t":"2026-11-18T12:00:00.123+07:00","type":"alert",...}</c>). Always on. Keeps a running
/// total of its own files and, past the cap, deletes the oldest day files first (only files named
/// yyyy-MM-dd.jsonl directly inside the root; the current day's file is never deleted; if it alone
/// is over the cap, writing stops until the next day). Every line passes through
/// <c>scrub</c> (the bot token is replaced) before it is written. Thread-safe; never throws.
/// </summary>
public sealed partial class Journal
{
    public const string Extension = ".jsonl";

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}\.jsonl$", RegexOptions.IgnoreCase)]
    private static partial Regex DayFilePattern();

    readonly object _gate = new();
    readonly string _root;
    readonly long _capBytes;
    readonly Func<string, string> _scrub;
    readonly Func<DateTimeOffset> _clock;
    readonly SortedDictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
    long _total;
    bool _initialized;
    string? _fullDay;

    public Journal(string root, long capBytes, Func<string, string>? scrub = null, Func<DateTimeOffset>? clock = null)
    {
        _root = Path.GetFullPath(root);
        _capBytes = capBytes;
        _scrub = scrub ?? (s => s);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string Root => _root;
    public long TotalBytes { get { lock (_gate) return _total; } }
    public int RecordsWritten { get; private set; }
    public int FilesDeleted { get; private set; }
    public string? LastError { get; private set; }

    public static string FileNameFor(DateTimeOffset t) => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Extension;

    /// <summary>Writes one record. Field values: string, bool, numbers, null, string[] / IEnumerable&lt;string&gt;, double[],
    /// or IReadOnlyDictionary&lt;string, string&gt; (written as a nested object).</summary>
    public void Write(string type, params (string Key, object? Value)[] fields)
    {
        try
        {
            var now = _clock();
            string line = _scrub(Format(now, type, fields));
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            lock (_gate)
            {
                Initialize();
                string name = FileNameFor(now);
                if (_fullDay == name) return;
                Directory.CreateDirectory(_root);
                string path = Path.Combine(_root, name);
                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    fs.Write(bytes);
                _files[name] = _files.GetValueOrDefault(name) + bytes.Length;
                _total += bytes.Length;
                RecordsWritten++;
                EnforceCap(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>One record as a single JSON line (no newline). Public for tests.</summary>
    public static string Format(DateTimeOffset time, string type, IReadOnlyList<(string Key, object? Value)> fields)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("t", time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            w.WriteString("type", type);
            foreach (var (key, value) in fields) WriteValue(w, key, value);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    static void WriteValue(Utf8JsonWriter w, string key, object? value)
    {
        switch (value)
        {
            case null: w.WriteNull(key); break;
            case string s: w.WriteString(key, s); break;
            case bool b: w.WriteBoolean(key, b); break;
            case int i: w.WriteNumber(key, i); break;
            case long l: w.WriteNumber(key, l); break;
            case double d: w.WriteNumber(key, Math.Round(d, 4)); break;
            case IReadOnlyDictionary<string, string> map:
                w.WriteStartObject(key);
                foreach (var (k, v) in map) w.WriteString(k, v);
                w.WriteEndObject();
                break;
            case IEnumerable<double> numbers:
                w.WriteStartArray(key);
                foreach (var v in numbers) w.WriteNumberValue(Math.Round(v, 4));
                w.WriteEndArray();
                break;
            case IEnumerable<string> list:
                w.WriteStartArray(key);
                foreach (var v in list) w.WriteStringValue(v);
                w.WriteEndArray();
                break;
            default: w.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        if (!Directory.Exists(_root)) return;
        foreach (var f in new DirectoryInfo(_root).EnumerateFiles())
            if (DayFilePattern().IsMatch(f.Name))
            {
                _files[f.Name] = f.Length;
                _total += f.Length;
            }
    }

    /// <summary>Deletes the oldest day files (never <paramref name="current"/>) while over the cap.</summary>
    void EnforceCap(string current)
    {
        while (_total > _capBytes)
        {
            string? oldest = _files.Keys.FirstOrDefault(k => !k.Equals(current, StringComparison.OrdinalIgnoreCase));
            if (oldest is null)
            {
                _fullDay = current; // only today's file is left and it is over the cap: stop for today
                return;
            }
            long size = _files[oldest];
            _files.Remove(oldest);
            _total -= size;
            try
            {
                File.Delete(Path.Combine(_root, oldest));
                FilesDeleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; }
        }
    }

    /// <summary>Size of the journal's own files in a folder (for the UI).</summary>
    public static long MeasureFolder(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        foreach (var f in new DirectoryInfo(root).EnumerateFiles())
            if (DayFilePattern().IsMatch(f.Name)) total += f.Length;
        return total;
    }
}
