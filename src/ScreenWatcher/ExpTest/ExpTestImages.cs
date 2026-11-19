using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.ExpTest;

/// <summary>The result picture (map label, level line at start and end, each captioned) and the
/// /exp list picture. Drawn off-screen with GDI+; nothing here touches the game.</summary>
public static class ExpTestImages
{
    static readonly Color Bg = PngEncoder.ComposeBackground;
    static readonly Color Text = Color.FromArgb(230, 233, 238), Muted = Color.FromArgb(150, 156, 166), Warn = Color.FromArgb(255, 201, 138), Good = Color.FromArgb(140, 220, 160);
    const string Family = "Leelawadee UI";
    const int Pad = 12;

    public static Bitmap ToBitmap(BgraImage img)
    {
        var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < img.Height; y++) Marshal.Copy(img.Pixels, y * img.Width * 4, data.Scan0 + y * data.Stride, img.Width * 4);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    static byte[] Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Whole-pixel zoom so a crop is about <paramref name="targetW"/> wide (1..3x).</summary>
    static int Zoom(int w, int targetW) => Math.Clamp(targetW / Math.Max(1, w), 1, 3);

    sealed record Block(string Caption, Color Colour, Bitmap? Image, int Zoom);

    /// <summary>One image: map label (start; end too when it changed), the level line at start and
    /// at end, each with a caption.</summary>
    public static byte[] Result(ExpResult r)
    {
        var blocks = new List<Block>();
        var bitmaps = new List<Bitmap>();
        Bitmap? Bmp(BgraImage? img)
        {
            if (img is null || img.IsEmpty) return null;
            var b = ToBitmap(img);
            bitmaps.Add(b);
            return b;
        }
        try
        {
            string T(DateTime t) => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var mapStart = Bmp(r.Start.Map?.Composite());
            bool changed = r.Flags.MapChanged;
            blocks.Add(new(changed ? "แมปตอนเริ่ม" : "แมป", Text, mapStart, 0));
            if (changed) blocks.Add(new("แมปตอนจบ (เปลี่ยนแมป)", Warn, Bmp(r.End?.Map?.Composite()), 0));
            blocks.Add(new($"เริ่ม {T(r.Start.Time)} · {r.Start.Text}", Text, Bmp(r.Start.LevelCrop), 0));
            if (r.End is { } e) blocks.Add(new($"จบ {T(e.Time)} · {e.Text}", Text, Bmp(e.LevelCrop), 0));
            else blocks.Add(new($"จบ · {ExpTexts.Unusable}", Warn, null, 0));

            string title = $"ทดสอบ EXP · {r.Minutes} นาที · " + (r.GainHundredths is { } g ? $"ได้ {ExpTexts.SignedPct(g)}" + (r.RatePerMin is { } rt ? $" · เฉลี่ย {ExpMath.RateText(rt)}% ต่อนาที" : "") : "ผลใช้ไม่ได้")
                + " · " + ExpTexts.Kills(r.Kills);
            string flags = string.Join(" · ", r.Flags.Words().Concat(r.Usable && r.Flags.NotComparable ? [ExpTexts.NotComparable] : []));

            using var titleFont = new Font(Family, 12f, FontStyle.Bold);
            using var font = new Font(Family, 10f);
            int contentW = blocks.Max(b => b.Image is null ? 0 : b.Image.Width * Zoom(b.Image.Width, 560));
            using var measure = new Bitmap(1, 1);
            using var mg = Graphics.FromImage(measure);
            int textW = (int)Math.Ceiling(Math.Max(mg.MeasureString(title, titleFont).Width, flags.Length == 0 ? 0 : mg.MeasureString(flags, font).Width));
            int capH = (int)Math.Ceiling(font.GetHeight(mg)) + 4, titleH = (int)Math.Ceiling(titleFont.GetHeight(mg)) + 6;
            int w = Math.Max(360, Math.Max(contentW, textW) + Pad * 2);
            int h = Pad + titleH + (flags.Length > 0 ? capH : 0) + 6;
            foreach (var b in blocks) h += capH + (b.Image is null ? 0 : b.Image.Height * Zoom(b.Image.Width, 560)) + 10;
            h += Pad - 10;

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            using var g2 = Graphics.FromImage(bmp);
            g2.Clear(Bg);
            g2.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g2.InterpolationMode = InterpolationMode.NearestNeighbor;
            g2.PixelOffsetMode = PixelOffsetMode.Half;
            int y = Pad;
            using (var br = new SolidBrush(Text)) g2.DrawString(title, titleFont, br, Pad, y);
            y += titleH;
            if (flags.Length > 0)
            {
                using var br = new SolidBrush(r.Flags.NotComparable ? Warn : Muted);
                g2.DrawString(flags, font, br, Pad, y);
                y += capH;
            }
            y += 6;
            foreach (var b in blocks)
            {
                using (var br = new SolidBrush(b.Colour)) g2.DrawString(b.Caption, font, br, Pad, y);
                y += capH;
                if (b.Image is { } img)
                {
                    int z = Zoom(img.Width, 560);
                    g2.DrawImage(img, new Rectangle(Pad, y, img.Width * z, img.Height * z));
                    y += img.Height * z;
                }
                y += 10;
            }
            return Png(bmp);
        }
        finally { foreach (var b in bitmaps) b.Dispose(); }
    }

    /// <summary>/exp list: one row per result (already sorted), each with its map label crop, gain per
    /// 10 minutes, kills, level, date and time, and flags.</summary>
    public static byte[] List(IReadOnlyList<ExpListRow> rows)
    {
        using var font = new Font(Family, 10f);
        using var bold = new Font(Family, 11f, FontStyle.Bold);
        const int mapW = 300, rowH = 64;
        int[] widths = [30, mapW + 12, 130, 160, 130, 90];
        int[] col = new int[7];
        col[0] = Pad;
        for (int k = 1; k < 7; k++) col[k] = col[k - 1] + widths[k - 1];
        int w = col[6] + 300, headH = 30;
        int h = Pad + headH + rows.Count * rowH + Pad;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Bg);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var text = new SolidBrush(Text);
        using var muted = new SolidBrush(Muted);
        using var good = new SolidBrush(Good);
        using var warn = new SolidBrush(Warn);
        using var line = new Pen(Color.FromArgb(45, 50, 58));
        string[] heads = ["#", "แมป", "EXP ต่อนาที", "เวลาต่อ 1 เลเวล", "ยิงมอนต่อนาที", "เลเวล", "วันเวลา · ระยะ"];
        for (int k = 0; k < heads.Length; k++) g.DrawString(heads[k], font, muted, col[k], Pad + 4);
        int y = Pad + headH;
        for (int i = 0; i < rows.Count; i++, y += rowH)
        {
            var r = rows[i];
            g.DrawLine(line, Pad, y, w - Pad, y);
            g.DrawString((i + 1).ToString(CultureInfo.InvariantCulture), bold, text, col[0], y + 20);
            if (r.MapPng is { Length: > 0 } png)
            {
                using var map = new Bitmap(new MemoryStream(png));
                double s = Math.Min(1.0, Math.Min((double)mapW / map.Width, (rowH - 8.0) / map.Height));
                int mw = (int)Math.Round(map.Width * s), mh = (int)Math.Round(map.Height * s);
                g.InterpolationMode = s < 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                g.DrawImage(map, new Rectangle(col[1] + mapW - mw, y + (rowH - mh) / 2, mw, mh));
            }
            else g.DrawString("-", font, muted, col[1], y + 22);
            g.DrawString(r.Data.RatePerMin is { } rate ? (rate < 0 ? "-" : "+") + ExpMath.RateText(Math.Abs(rate)) + "%" : "-", bold, good, col[2], y + 20);
            g.DrawString(r.Data.MinutesPerLevel is { } per ? ExpMath.Duration(per) : "-", font, text, col[3], y + 22);
            g.DrawString(r.Data.KillsPerMin is { } q ? ExpTexts.KillsPerMin(q) : ExpTexts.KillsUnknown, font, text, col[4], y + 22);
            string lv = r.Data.EndLevel is { } el && el != r.Data.StartLevel ? $"Lv.{r.Data.StartLevel}→{el}" : $"Lv.{r.Data.StartLevel}";
            g.DrawString(lv, font, text, col[5], y + 22);
            g.DrawString($"{r.Data.StartTime.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)} · {r.Data.Minutes} นาที", font, text, col[6], y + 12);
            if (r.Data.ShownFlags.Length > 0) g.DrawString(string.Join(" · ", r.Data.ShownFlags), font, warn, col[6], y + 34);
        }
        return Png(bmp);
    }
}
