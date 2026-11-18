using System.Drawing;
using System.Drawing.Imaging;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Capture;

public enum CaptureResult { Ok, Minimized, Failed }

/// <summary>
/// Captures the game window with PrintWindow(PW_RENDERFULLCONTENT). This asks DWM for the
/// window's own content, so it works while other windows cover the game. PrintWindow always
/// renders the whole window; we keep only the client area. Buffers are reused between calls.
/// </summary>
public sealed class PrintWindowCapture : IDisposable
{
    Bitmap? _buffer;

    public CaptureResult Capture(IntPtr hwnd, Frame target)
    {
        if (NativeMethods.IsIconic(hwnd)) return CaptureResult.Minimized;
        if (!NativeMethods.GetWindowRect(hwnd, out var wr) || !NativeMethods.GetClientRect(hwnd, out var cr))
            return CaptureResult.Failed;
        if (wr.Width <= 0 || wr.Height <= 0 || cr.Width <= 0 || cr.Height <= 0) return CaptureResult.Failed;

        var origin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return CaptureResult.Failed;

        if (_buffer is null || _buffer.Width != wr.Width || _buffer.Height != wr.Height)
        {
            _buffer?.Dispose();
            _buffer = new Bitmap(wr.Width, wr.Height, PixelFormat.Format32bppArgb);
        }

        using (var g = Graphics.FromImage(_buffer))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                if (!NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT))
                    return CaptureResult.Failed;
            }
            finally { g.ReleaseHdc(hdc); }
        }

        var client = new Rectangle(origin.X - wr.Left, origin.Y - wr.Top, cr.Width, cr.Height);
        target.CopyFrom(_buffer, client);
        return CaptureResult.Ok;
    }

    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }
}
