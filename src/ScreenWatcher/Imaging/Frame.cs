using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenWatcher.Imaging;

public readonly record struct Rgb(double R, double G, double B)
{
    public double Max => Math.Max(R, Math.Max(G, B));
    public override string ToString() => $"({R:F0},{G:F0},{B:F0})";
}

/// <summary>
/// A copy of the game's client area in 32bpp BGRA. The pixel buffer is reused between
/// captures of the same size so polling does not allocate.
/// </summary>
public sealed class Frame
{
    byte[] _px = [];

    public int Width { get; private set; }
    public int Height { get; private set; }

    public static Frame FromBitmap(Bitmap bitmap, Rectangle source)
    {
        var f = new Frame();
        f.CopyFrom(bitmap, source);
        return f;
    }

    public void CopyFrom(Bitmap bitmap, Rectangle source)
    {
        source.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (source.Width <= 0 || source.Height <= 0) throw new ArgumentException("Empty source rectangle.");

        Width = source.Width;
        Height = source.Height;
        int rowBytes = Width * 4;
        if (_px.Length != rowBytes * Height) _px = new byte[rowBytes * Height];

        var data = bitmap.LockBits(source, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, _px, y * rowBytes, rowBytes);
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>Mean colour of the rectangle (clipped to the frame); null if nothing is inside.</summary>
    public Rgb? Mean(int x, int y, int w, int h)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
        if (x1 <= x0 || y1 <= y0) return null;

        long r = 0, g = 0, b = 0;
        for (int yy = y0; yy < y1; yy++)
        {
            int i = (yy * Width + x0) * 4;
            for (int xx = x0; xx < x1; xx++, i += 4)
            {
                b += _px[i]; g += _px[i + 1]; r += _px[i + 2];
            }
        }
        double n = (double)(x1 - x0) * (y1 - y0);
        return new Rgb(r / n, g / n, b / n);
    }

    public Rgb? MeanAround(int cx, int cy, int w, int h) => Mean(cx - w / 2, cy - h / 2, w, h);

    /// <summary>True when (almost) every sampled pixel is black: the capture was blocked or not rendered.</summary>
    public bool LooksBlank(int step = 16, int maxChannel = 8, double minNonBlankFraction = 0.01)
    {
        int total = 0, lit = 0;
        for (int y = 0; y < Height; y += step)
            for (int x = 0; x < Width; x += step)
            {
                int i = (y * Width + x) * 4;
                total++;
                if (_px[i] > maxChannel || _px[i + 1] > maxChannel || _px[i + 2] > maxChannel) lit++;
            }
        return total == 0 || lit < total * minNonBlankFraction;
    }
}
