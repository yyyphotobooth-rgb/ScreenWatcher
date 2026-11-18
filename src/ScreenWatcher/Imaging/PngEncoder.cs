using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenWatcher.Imaging;

/// <summary>PNG encoding of frame regions (used only on events and /ss, never per poll).</summary>
public static class PngEncoder
{
    /// <summary>Background between composed crops (the app's dark theme background).</summary>
    public static readonly Color ComposeBackground = Color.FromArgb(21, 24, 29);

    /// <summary>A tightly packed BGRA image as PNG.</summary>
    public static byte[] EncodeBgra(byte[] pixels, int width, int height)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            int rowBytes = width * 4;
            for (int y = 0; y < height; y++) Marshal.Copy(pixels, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
        }
        finally { bmp.UnlockBits(data); }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>One rectangle of the frame (clipped to it) as PNG.</summary>
    public static byte[] Encode(Frame frame, Rectangle rect) => Compose(frame, [rect], 0);

    /// <summary>
    /// Several rectangles of the frame placed side by side (left to right, top-aligned,
    /// <paramref name="gap"/> px apart) in one PNG. Empty rectangles are skipped.
    /// </summary>
    public static byte[] Compose(Frame frame, IReadOnlyList<Rectangle> rects, int gap = 8)
    {
        var bounds = new Rectangle(0, 0, frame.Width, frame.Height);
        var parts = rects.Select(r => Rectangle.Intersect(r, bounds)).Where(r => r.Width > 0 && r.Height > 0).ToList();
        if (parts.Count == 0) throw new ArgumentException("Nothing to encode: every rectangle is outside the frame.");

        int width = parts.Sum(r => r.Width) + gap * (parts.Count - 1);
        int height = parts.Max(r => r.Height);
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(ComposeBackground);

        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            var px = frame.Pixels;
            int x0 = 0;
            foreach (var r in parts)
            {
                int rowBytes = r.Width * 4;
                byte[] row = new byte[rowBytes];
                for (int y = 0; y < r.Height; y++)
                {
                    px.Slice(((r.Y + y) * frame.Width + r.X) * 4, rowBytes).CopyTo(row);
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride + x0 * 4, rowBytes);
                }
                x0 += r.Width + gap;
            }
        }
        finally { bmp.UnlockBits(data); }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
