using System.Runtime.InteropServices;
using ScreenWatcher.Imaging;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ScreenWatcher.Capture;

public enum CaptureResult { Ok, Minimized, Failed }

/// <summary>
/// Captures the game window with Windows.Graphics.Capture (DWM hands us the window's composed
/// content; no redraw request is sent to the game, unlike PrintWindow). Works while the window
/// is covered. Cursor capture is off and the yellow capture border is turned off where the OS
/// allows it.
///
/// Cost: the frame pool has 2 buffers and frames are only taken out on our ~1 s poll. While the
/// pool is full, DWM delivers nothing more, so the GPU copies about one or two frames per poll and
/// only the client rectangle is copied back to the CPU, into a reused staging texture and a reused
/// <see cref="Frame"/> buffer.
/// </summary>
public sealed class WgcCapture : IDisposable
{
    const int PoolBuffers = 2;
    static readonly TimeSpan FreshFrameWait = TimeSpan.FromMilliseconds(150);
    static readonly TimeSpan FirstFrameWait = TimeSpan.FromMilliseconds(1000);

    IntPtr _device, _context;
    IDirect3DDevice? _winrtDevice;

    IntPtr _hwnd;
    GraphicsCaptureItem? _item;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    SizeInt32 _poolSize;
    volatile bool _itemClosed;
    readonly AutoResetEvent _arrived = new(false);
    bool _firstFrame;

    IntPtr _staging;
    int _stagingW, _stagingH;

    /// <summary>Why the last capture failed (shown in the status line), or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>True if the capture border was turned off, false if the OS refused or lacks it, null before first start.</summary>
    public bool? BorderDisabled { get; private set; }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public CaptureResult Capture(IntPtr hwnd, Frame target)
    {
        LastError = null;
        if (NativeMethods.IsIconic(hwnd)) return CaptureResult.Minimized;
        if (!NativeMethods.GetClientRect(hwnd, out var cr) || cr.Width <= 0 || cr.Height <= 0)
            return Fail("no client area");
        var origin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return Fail("ClientToScreen failed");
        // WGC window content is the window's visible frame (DWM extended frame bounds), so the
        // client area sits at (client origin - frame origin) inside the captured surface.
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT frameRect,
                Marshal.SizeOf<NativeMethods.RECT>()) != 0)
            return Fail("frame bounds unavailable");

        try
        {
            if (!EnsureSession(hwnd)) return CaptureResult.Failed;

            using var frame = NextFrame();
            if (frame is null) return Fail("no frame from Windows.Graphics.Capture");

            var content = frame.ContentSize;
            if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
            {
                // Window was resized: resize the pool; the next poll gets a frame of the new size.
                _poolSize = content;
                _pool!.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, content);
            }

            int x = origin.X - frameRect.Left, y = origin.Y - frameRect.Top;
            int w = Math.Min(cr.Width, content.Width - x), h = Math.Min(cr.Height, content.Height - y);
            if (x < 0 || y < 0 || w <= 0 || h <= 0) return Fail($"client not inside frame ({x},{y} in {content.Width}x{content.Height})");

            CopyClient(frame, x, y, w, h, target);
            return CaptureResult.Ok;
        }
        catch (Exception ex)
        {
            CloseSession();
            return Fail(ex.Message);
        }
    }

    CaptureResult Fail(string why)
    {
        LastError = why;
        return CaptureResult.Failed;
    }

    bool EnsureSession(IntPtr hwnd)
    {
        if (_session is not null && hwnd == _hwnd && !_itemClosed) return true;
        CloseSession();

        if (!IsSupported) { LastError = "Windows.Graphics.Capture not supported on this PC"; return false; }

        if (_device == IntPtr.Zero)
        {
            var (device, context, winrt) = D3D11Interop.CreateDevice();
            _device = device;
            _context = context;
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(winrt);
            Marshal.Release(winrt);
        }

        IntPtr itemPtr = D3D11Interop.CreateCaptureItemForWindow(hwnd);
        try { _item = GraphicsCaptureItem.FromAbi(itemPtr); }
        finally { Marshal.Release(itemPtr); }
        _itemClosed = false;
        _item.Closed += (_, _) => _itemClosed = true;

        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, _poolSize);
        _pool.FrameArrived += (_, _) => _arrived.Set();
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        BorderDisabled = TryDisableBorder(_session);
        _hwnd = hwnd;
        _firstFrame = true;
        _session.StartCapture();
        return true;
    }

    static bool TryDisableBorder(GraphicsCaptureSession session)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureSession";
        if (!ApiInformation.IsPropertyPresent(cls, nameof(GraphicsCaptureSession.IsBorderRequired))) return false;
        try
        {
            // Unpackaged desktop apps are normally allowed; ask anyway as the API requires.
            var access = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
            if (access != Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed) return false;
            session.IsBorderRequired = false;
            return !session.IsBorderRequired;
        }
        catch { return false; }
    }

    /// <summary>
    /// Takes the newest queued frame, then waits briefly for one rendered after this call so the
    /// result is not up to a poll old. Falls back to the queued frame if the game is not drawing.
    /// </summary>
    Direct3D11CaptureFrame? NextFrame()
    {
        _arrived.Reset();
        Direct3D11CaptureFrame? latest = null;
        while (_pool!.TryGetNextFrame() is { } f)
        {
            latest?.Dispose();
            latest = f;
        }

        var wait = _firstFrame ? FirstFrameWait : FreshFrameWait;
        if (_arrived.WaitOne(wait) && _pool.TryGetNextFrame() is { } fresh)
        {
            latest?.Dispose();
            latest = fresh;
        }
        if (latest is not null) _firstFrame = false;
        return latest;
    }

    void CopyClient(Direct3D11CaptureFrame frame, int x, int y, int w, int h, Frame target)
    {
        if (_staging == IntPtr.Zero || _stagingW != w || _stagingH != h)
        {
            D3D11Interop.Release(ref _staging);
            _staging = D3D11Interop.CreateStaging(_device, w, h);
            _stagingW = w;
            _stagingH = h;
        }

        IntPtr surface = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        IntPtr tex = IntPtr.Zero;
        try
        {
            tex = D3D11Interop.GetTexture(surface);
            D3D11Interop.CopyRegion(_context, _staging, tex, x, y, w, h);
        }
        finally
        {
            D3D11Interop.Release(ref tex);
            Marshal.Release(surface);
        }

        var m = D3D11Interop.Map(_context, _staging);
        try { target.CopyFromBgra(m.Data, (int)m.RowPitch, w, h); }
        finally { D3D11Interop.Unmap(_context, _staging); }
    }

    void CloseSession()
    {
        _session?.Dispose();
        _session = null;
        _pool?.Dispose();
        _pool = null;
        _item = null;
        _hwnd = IntPtr.Zero;
    }

    public void Dispose()
    {
        CloseSession();
        D3D11Interop.Release(ref _staging);
        _winrtDevice?.Dispose();
        _winrtDevice = null;
        D3D11Interop.Release(ref _context);
        D3D11Interop.Release(ref _device);
        _arrived.Dispose();
    }
}
