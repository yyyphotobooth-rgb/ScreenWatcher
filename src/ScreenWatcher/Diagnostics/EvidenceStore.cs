using System.Buffers;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ScreenWatcher.Diagnostics;

/// <summary>One image to write. <see cref="Pixels"/> is tightly packed BGRA rented from
/// <see cref="ArrayPool{T}.Shared"/>; the store returns it after writing (or dropping) it.</summary>
public sealed record EvidenceImage(string FileName, byte[] Pixels, int Width, int Height, bool Jpeg);

/// <summary>Images written together, into the folder of <see cref="Time"/>'s day.</summary>
public sealed record EvidenceJob(DateTime Time, IReadOnlyList<EvidenceImage> Images);

/// <summary>
/// Owns the evidence folder: &lt;root&gt;\yyyy-MM-dd\*.jpg|*.png (alert evidence and /miss
/// bundles). Encoding and writing run on its own background thread. Keeps a running total of its
/// own files and, past the cap, deletes the oldest first. Only .jpg/.png files directly inside
/// date folders directly inside the root are ever deleted.
/// </summary>
public sealed partial class EvidenceStore : IDisposable
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DayFolderPattern();

    readonly string _root;
    readonly long _capBytes;
    readonly int _jpegQuality;
    readonly BlockingCollection<EvidenceJob> _queue;
    readonly LinkedList<(string Path, long Size)> _oldestFirst = new();
    readonly object _gate = new();
    Thread? _thread;
    long _totalBytes;
    bool _initialized;
    int _pending;

    public EvidenceStore(string root, long capBytes, int jpegQuality, int queueLimit = 8)
    {
        _root = Path.GetFullPath(root);
        _capBytes = capBytes;
        _jpegQuality = Math.Clamp(jpegQuality, 1, 100);
        _queue = new BlockingCollection<EvidenceJob>(Math.Max(1, queueLimit));
    }

    public string Root => _root;
    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    public int FilesWritten { get; private set; }
    public int FilesDeleted { get; private set; }
    public int JobsDropped { get; private set; }
    public string? LastError { get; private set; }

    public static string DayFolder(DateTime t) => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Path of an image relative to the root ("yyyy-MM-dd/name"), as the journal links it.</summary>
    public static string RelativePath(DateTime t, string fileName) => DayFolder(t) + "/" + fileName;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "evidence-writer", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Queues a job without blocking. Returns the relative paths it will write, or an empty
    /// list (and frees the buffers) when the queue is full.</summary>
    public IReadOnlyList<string> TryEnqueue(EvidenceJob job)
    {
        bool ok;
        Interlocked.Increment(ref _pending);
        try { ok = !_queue.IsAddingCompleted && _queue.TryAdd(job); }
        catch (InvalidOperationException) { ok = false; }
        if (!ok)
        {
            Interlocked.Decrement(ref _pending);
            foreach (var i in job.Images) ArrayPool<byte>.Shared.Return(i.Pixels);
            JobsDropped++;
            return [];
        }
        return job.Images.Select(i => RelativePath(job.Time, i.FileName)).ToArray();
    }

    /// <summary>Waits until every queued job was written (tests, shutdown).</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pending) > 0)
        {
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    void Run()
    {
        try { lock (_gate) Initialize(); } catch (Exception ex) { LastError = ex.Message; }
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            try { Write(job); }
            catch (Exception ex) { LastError = ex.Message; }
            finally { Interlocked.Decrement(ref _pending); }
        }
    }

    void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        if (!Directory.Exists(_root)) return;
        var files = new List<FileInfo>();
        foreach (var day in new DirectoryInfo(_root).EnumerateDirectories())
            if (DayFolderPattern().IsMatch(day.Name))
                files.AddRange(day.EnumerateFiles().Where(IsImage));
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            _oldestFirst.AddLast((f.FullName, f.Length));
            Interlocked.Add(ref _totalBytes, f.Length);
        }
        EnforceCap();
    }

    static bool IsImage(FileInfo f) =>
        f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Encodes and writes one job, then enforces the cap. Returns the full paths written.</summary>
    public List<string> Write(EvidenceJob job)
    {
        var written = new List<string>();
        lock (_gate)
        {
            try
            {
                Initialize();
                string dayDir = Path.Combine(_root, DayFolder(job.Time));
                Directory.CreateDirectory(dayDir);
                foreach (var img in job.Images)
                {
                    string path = Path.Combine(dayDir, img.FileName);
                    Save(img, path);
                    long size = new FileInfo(path).Length;
                    _oldestFirst.AddLast((path, size));
                    Interlocked.Add(ref _totalBytes, size);
                    FilesWritten++;
                    written.Add(path);
                }
                EnforceCap();
            }
            finally
            {
                foreach (var i in job.Images) ArrayPool<byte>.Shared.Return(i.Pixels);
            }
        }
        return written;
    }

    void Save(EvidenceImage img, string path)
    {
        using var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            int rowBytes = img.Width * 4;
            for (int y = 0; y < img.Height; y++)
                Marshal.Copy(img.Pixels, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
        }
        finally { bmp.UnlockBits(data); }

        if (!img.Jpeg) { bmp.Save(path, ImageFormat.Png); return; }
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)_jpegQuality);
        bmp.Save(path, codec, p);
    }

    void EnforceCap()
    {
        while (TotalBytes > _capBytes && _oldestFirst.First is { } node)
        {
            _oldestFirst.RemoveFirst();
            var (path, size) = node.Value;
            if (!IsOwnFile(path)) continue;
            try
            {
                if (File.Exists(path)) { File.Delete(path); FilesDeleted++; }
                Interlocked.Add(ref _totalBytes, -size);
                string day = Path.GetDirectoryName(path)!;
                if (!Directory.EnumerateFileSystemEntries(day).Any()) Directory.Delete(day);
            }
            catch (IOException ex) { LastError = ex.Message; }
            catch (UnauthorizedAccessException ex) { LastError = ex.Message; }
        }
    }

    bool IsOwnFile(string path)
    {
        string full = Path.GetFullPath(path);
        string? day = Path.GetDirectoryName(full);
        return day is not null
            && IsImage(new FileInfo(full))
            && string.Equals(Path.GetDirectoryName(day), _root, StringComparison.OrdinalIgnoreCase)
            && DayFolderPattern().IsMatch(Path.GetFileName(day));
    }

    /// <summary>Size of the store's own files in a folder (for the UI).</summary>
    public static long MeasureFolder(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        foreach (var day in new DirectoryInfo(root).EnumerateDirectories())
            if (DayFolderPattern().IsMatch(day.Name))
                foreach (var f in day.EnumerateFiles())
                    if (IsImage(f)) total += f.Length;
        return total;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread?.Join(TimeSpan.FromSeconds(5));
        if (_thread is null || !_thread.IsAlive)
        {
            while (_queue.TryTake(out var job))
                foreach (var i in job.Images) ArrayPool<byte>.Shared.Return(i.Pixels);
            _queue.Dispose();
        }
    }
}
