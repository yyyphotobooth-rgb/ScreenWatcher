using System.Buffers;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ScreenWatcher.Detection;

namespace ScreenWatcher.Collection;

/// <summary>One image to write. <see cref="Pixels"/> is tightly packed BGRA rented from
/// <see cref="ArrayPool{T}.Shared"/>; the store returns it after writing (or dropping) the job.</summary>
public sealed record SaveJob(
    byte[] Pixels, int Width, int Height, DateTime Time, string Region, SaveReason Reason, string Suffix,
    int ClientWidth, int ClientHeight, HudScale Scale, string? Rule = null);

/// <summary>
/// Owns the collect folder: &lt;root&gt;\yyyy-MM-dd\*.png plus one index.csv per day. All file
/// I/O runs on its own background thread (never on the polling path). Keeps a running total of
/// this mode's files and, past the cap, deletes this mode's oldest PNGs first (and a day's
/// index.csv once that day has no PNG left). Only files found inside date folders under the
/// root, with this mode's names, are ever deleted.
/// </summary>
public sealed partial class CollectStore : IDisposable
{
    public const string IndexFileName = "index.csv";
    const string IndexHeader = "timestamp,region,file,client_width,client_height,scale_x,scale_y,reason";

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DayFolderPattern();

    readonly string _root;
    readonly long _capBytes;
    readonly BlockingCollection<SaveJob> _queue;
    readonly LinkedList<(string Path, long Size)> _oldestFirst = new();
    readonly Dictionary<string, int> _pngsPerDay = new(StringComparer.OrdinalIgnoreCase);
    Thread? _thread;
    long _totalBytes;
    bool _initialized;

    public CollectStore(string root, long capBytes, int queueLimit = 24)
    {
        _root = Path.GetFullPath(root);
        _capBytes = capBytes;
        _queue = new BlockingCollection<SaveJob>(Math.Max(1, queueLimit));
    }

    public string Root => _root;
    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    int _filesWritten, _filesDeleted, _jobsDropped;
    long _writeMs;
    public int FilesWritten => Volatile.Read(ref _filesWritten);
    public int FilesDeleted => Volatile.Read(ref _filesDeleted);
    public int JobsDropped => Volatile.Read(ref _jobsDropped);
    /// <summary>Total milliseconds spent encoding and writing (writer thread).</summary>
    public long WriteMilliseconds => Interlocked.Read(ref _writeMs);
    public string? LastError { get; private set; }

    /// <summary>Starts the writer thread (it scans the existing folder first).</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "collect-writer", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Queues a job without blocking. Returns false (and frees the buffer) when the queue is full.</summary>
    public bool TryEnqueue(SaveJob job)
    {
        bool ok;
        try { ok = !_queue.IsAddingCompleted && _queue.TryAdd(job); }
        catch (InvalidOperationException) { ok = false; }
        if (!ok)
        {
            ArrayPool<byte>.Shared.Return(job.Pixels);
            Interlocked.Increment(ref _jobsDropped);
        }
        return ok;
    }

    void Run()
    {
        try { Initialize(); } catch (Exception ex) { LastError = ex.Message; }
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            try { Write(job); }
            catch (Exception ex) { LastError = ex.Message; }
        }
    }

    /// <summary>Scans the existing collect folder (size total and oldest-first list).</summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        if (!Directory.Exists(_root)) return;
        var pngs = new List<FileInfo>();
        long total = 0;
        foreach (var day in new DirectoryInfo(_root).EnumerateDirectories())
        {
            if (!DayFolderPattern().IsMatch(day.Name)) continue;
            foreach (var f in day.EnumerateFiles())
            {
                if (f.Name.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase)) total += f.Length;
                else if (f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    pngs.Add(f);
                    total += f.Length;
                }
            }
        }
        foreach (var f in pngs.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal))
            Track(f.FullName, f.Length);
        Interlocked.Exchange(ref _totalBytes, total);
        EnforceCap();
    }

    void Track(string path, long size)
    {
        _oldestFirst.AddLast((path, size));
        string day = Path.GetDirectoryName(path)!;
        _pngsPerDay[day] = _pngsPerDay.GetValueOrDefault(day) + 1;
    }

    /// <summary>Encodes and writes one job, appends its index row, then enforces the cap. Returns the PNG path.</summary>
    public string Write(SaveJob job)
    {
        long t0 = Environment.TickCount64;
        try
        {
            Initialize();
            string dayDir = Path.Combine(_root, job.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dayDir);
            string reason = ReasonText(job.Reason);
            string baseName = $"{job.Time.ToString("HHmmss_fff", CultureInfo.InvariantCulture)}_{job.Region}_{reason}{job.Suffix}";
            string path = Path.Combine(dayDir, baseName + ".png");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dayDir, $"{baseName}_{i}.png");

            using (var bmp = new Bitmap(job.Width, job.Height, PixelFormat.Format32bppRgb))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, job.Width, job.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    int rowBytes = job.Width * 4;
                    for (int y = 0; y < job.Height; y++)
                        Marshal.Copy(job.Pixels, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
                }
                finally { bmp.UnlockBits(data); }
                bmp.Save(path, ImageFormat.Png);
            }
            long size = new FileInfo(path).Length;
            Track(path, size);
            Interlocked.Add(ref _totalBytes, size);
            Interlocked.Increment(ref _filesWritten);

            string index = Path.Combine(dayDir, IndexFileName);
            bool isNew = !File.Exists(index);
            string row = string.Join(',',
                job.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                job.Region,
                Path.GetFileName(path),
                job.ClientWidth.ToString(CultureInfo.InvariantCulture),
                job.ClientHeight.ToString(CultureInfo.InvariantCulture),
                job.Scale.X.ToString("F4", CultureInfo.InvariantCulture),
                job.Scale.Y.ToString("F4", CultureInfo.InvariantCulture),
                job.Rule is null ? reason : reason + "+" + job.Rule) + "\r\n";
            string text = isNew ? IndexHeader + "\r\n" + row : row;
            File.AppendAllText(index, text);
            Interlocked.Add(ref _totalBytes, text.Length); // ASCII only

            EnforceCap();
            return path;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(job.Pixels);
            Interlocked.Add(ref _writeMs, Environment.TickCount64 - t0);
        }
    }

    public static string ReasonText(SaveReason r) => r switch
    {
        SaveReason.Change => "change",
        SaveReason.Heartbeat => "heartbeat",
        SaveReason.Event => "event",
        SaveReason.Pre => "pre",
        _ => "periodic",
    };

    /// <summary>While over the cap, deletes this mode's oldest PNGs (only inside the root's date folders).</summary>
    public void EnforceCap()
    {
        while (TotalBytes > _capBytes && _oldestFirst.First is { } node)
        {
            _oldestFirst.RemoveFirst();
            var (path, size) = node.Value;
            if (!IsOwnFile(path)) continue;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Interlocked.Increment(ref _filesDeleted);
                }
                Interlocked.Add(ref _totalBytes, -size);
            }
            catch (IOException ex) { LastError = ex.Message; continue; }
            catch (UnauthorizedAccessException ex) { LastError = ex.Message; continue; }

            string day = Path.GetDirectoryName(path)!;
            int left = _pngsPerDay.GetValueOrDefault(day) - 1;
            if (left > 0) { _pngsPerDay[day] = left; continue; }
            _pngsPerDay.Remove(day);
            string index = Path.Combine(day, IndexFileName);
            try
            {
                if (File.Exists(index))
                {
                    long len = new FileInfo(index).Length;
                    File.Delete(index);
                    Interlocked.Add(ref _totalBytes, -len);
                }
                if (!Directory.EnumerateFileSystemEntries(day).Any()) Directory.Delete(day);
            }
            catch (IOException ex) { LastError = ex.Message; }
            catch (UnauthorizedAccessException ex) { LastError = ex.Message; }
        }
    }

    /// <summary>A PNG directly inside a yyyy-MM-dd folder directly inside the root.</summary>
    bool IsOwnFile(string path)
    {
        string full = Path.GetFullPath(path);
        string? day = Path.GetDirectoryName(full);
        return day is not null
            && full.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(day), _root, StringComparison.OrdinalIgnoreCase)
            && DayFolderPattern().IsMatch(Path.GetFileName(day));
    }

    /// <summary>Size of this mode's files in a collect folder (for the UI while collection is off).</summary>
    public static long MeasureFolder(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        foreach (var day in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (!DayFolderPattern().IsMatch(day.Name)) continue;
            foreach (var f in day.EnumerateFiles())
                if (f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    || f.Name.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase))
                    total += f.Length;
        }
        return total;
    }

    /// <summary>Stops accepting jobs and lets the writer finish what is queued (waits up to 5 s).</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread?.Join(TimeSpan.FromSeconds(5));
        if (_thread is null || !_thread.IsAlive)
        {
            while (_queue.TryTake(out var job)) ArrayPool<byte>.Shared.Return(job.Pixels);
            _queue.Dispose();
        }
    }
}
