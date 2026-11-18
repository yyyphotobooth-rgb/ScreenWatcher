using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ScreenWatcher.UI;

/// <summary>Colours and fonts from design/mockup-*.html (dark theme).</summary>
static class Theme
{
    public static readonly Color Background = Hex("#15181d");
    public static readonly Color Surface = Hex("#1e232b");
    public static readonly Color Divider = Hex("#2c333d");
    public static readonly Color InputBorder = Hex("#3a4350");
    public static readonly Color Text = Hex("#e8ebf0");
    public static readonly Color Muted = Hex("#aab3bf");
    public static readonly Color Accent = Hex("#7db8ff");
    public static readonly Color AccentHover = Hex("#a9d0ff");
    public static readonly Color Primary = Hex("#2b6fd6");
    public static readonly Color Disabled = Hex("#5d6672");
    // "Threshold reached" row (mockup row บัตรกันมอนเหลือ).
    public static readonly Color WarnBackground = Hex("#3a2a14");
    public static readonly Color WarnText = Hex("#ffc98a");
    public static readonly Color WarnValue = Hex("#ffe1bd");
    public static readonly Color WarnBorder = Hex("#b9772a");
    // Not in the mockups: error / event-state text.
    public static readonly Color Error = Hex("#ff9b8a");

    // The mockups use IBM Plex Sans Thai (a web font, not installed on Windows). Leelawadee UI
    // ships with Windows 10/11 and covers Thai. CSS px -> pt: px * 0.75.
    const string Family = "Leelawadee UI";
    public static readonly Font Body = new(Family, 11.25f);            // 15px
    public static readonly Font BodyBold = new(Family, 11.25f, FontStyle.Bold); // 15px / 600
    public static readonly Font Small = new(Family, 9.75f);            // 13px
    public static readonly Font Value = new(Family, 12.75f);           // 17px
    public static readonly Font ValueBold = new(Family, 12.75f, FontStyle.Bold);

    public static Color Hex(string s) => ColorTranslator.FromHtml(s);

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        if (radius <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Rounded "card" (background #1e232b, radius 8) like the mockups' tiles and steps.</summary>
class Card : Panel
{
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = Theme.Surface;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Border { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Radius { get; set; } = 8;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundRect(r, Radius * DeviceDpi / 96f);
        using var b = new SolidBrush(Fill);
        e.Graphics.FillPath(b, path);
        if (Border is { } bc)
        {
            using var pen = new Pen(bc);
            e.Graphics.DrawPath(pen, path);
        }
    }

    /// <summary>Clip children to the rounded corners (the alert table, whose rows fill it edge to edge).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ClipToShape { get; set; }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!ClipToShape || Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), Radius * DeviceDpi / 96f);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }
}

public enum ButtonKind { Primary, Secondary, Link }

/// <summary>Flat rounded button: primary (#2b6fd6), secondary (#1e232b/#15181d + #3a4350 border) or link (‹ กลับ).</summary>
class DarkButton : Control
{
    bool _hover, _down;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;
    /// <summary>Secondary fill; the mockups use #1e232b on the page and #15181d inside cards.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SecondaryFill { get; set; } = Theme.Surface;

    public DarkButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        Font = Theme.Body;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        if (Parent is Card c) { using var pb = new SolidBrush(c.Fill); g.FillRectangle(pb, ClientRectangle); }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundRect(r, 8 * DeviceDpi / 96f);
        Color text = Theme.Text;
        Font font = Font;
        switch (Kind)
        {
            case ButtonKind.Primary:
            {
                var fill = !Enabled ? Theme.Divider : _down ? Darken(Theme.Primary, 0.15f) : _hover ? Lighten(Theme.Primary, 0.1f) : Theme.Primary;
                using var b = new SolidBrush(fill);
                g.FillPath(b, path);
                text = Enabled ? Color.White : Theme.Disabled;
                break;
            }
            case ButtonKind.Secondary:
            {
                var fill = _down ? Lighten(SecondaryFill, 0.08f) : _hover && Enabled ? Lighten(SecondaryFill, 0.05f) : SecondaryFill;
                using var b = new SolidBrush(fill);
                g.FillPath(b, path);
                using var pen = new Pen(Focused ? Theme.Accent : Theme.InputBorder);
                g.DrawPath(pen, path);
                text = Enabled ? Theme.Text : Theme.Disabled;
                break;
            }
            default:
                text = !Enabled ? Theme.Disabled : _hover ? Theme.AccentHover : Theme.Accent;
                break;
        }
        TextRenderer.DrawText(g, Text, font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    static Color Lighten(Color c, float f) => Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * f), (int)(c.G + (255 - c.G) * f), (int)(c.B + (255 - c.B) * f));
    static Color Darken(Color c, float f) => Color.FromArgb(c.A, (int)(c.R * (1 - f)), (int)(c.G * (1 - f)), (int)(c.B * (1 - f)));
}

/// <summary>18x18 checkbox drawn for the dark theme.</summary>
class DarkCheck : Control
{
    bool _checked;

    public event EventHandler? CheckedChanged;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    public DarkCheck()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; } base.OnKeyDown(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(Width, Height) - 1;
        var r = new RectangleF((Width - s) / 2f, (Height - s) / 2f, s, s);
        using var path = Theme.RoundRect(r, 3 * DeviceDpi / 96f);
        Color fill = !Enabled ? Theme.Divider : _checked ? Theme.Primary : Theme.Background;
        Color border = !Enabled ? Theme.InputBorder : _checked ? Theme.Primary : Focused ? Theme.Accent : Theme.Muted;
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        using (var p = new Pen(border)) g.DrawPath(p, path);
        if (_checked)
        {
            using var p = new Pen(Enabled ? Color.White : Theme.Disabled, Math.Max(2f, s / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(p, [new PointF(r.X + s * 0.24f, r.Y + s * 0.52f), new PointF(r.X + s * 0.43f, r.Y + s * 0.71f), new PointF(r.X + s * 0.77f, r.Y + s * 0.31f)]);
        }
    }
}

/// <summary>A bordered input box (#15181d, #3a4350 border, radius 6/8) hosting a borderless TextBox.</summary>
class InputBox : Control
{
    public TextBox Box { get; } = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Background, ForeColor = Theme.Text };

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Theme.InputBorder;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Radius { get; set; } = 8;

    public InputBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
    }

    protected override void OnFontChanged(EventArgs e) { Box.Font = Font; base.OnFontChanged(e); }
    protected override void OnEnabledChanged(EventArgs e)
    {
        Box.Enabled = Enabled;
        Box.ForeColor = Enabled ? Theme.Text : Theme.Disabled;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = (int)(Box.TextAlign == HorizontalAlignment.Center ? 4 * DeviceDpi / 96f : 12 * DeviceDpi / 96f);
        Box.SetBounds(pad, (Height - Box.PreferredHeight) / 2, Math.Max(1, Width - pad * 2), Box.PreferredHeight);
    }

    protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent is Card c ? c.Fill : Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius * DeviceDpi / 96f);
        using (var b = new SolidBrush(Theme.Background)) g.FillPath(b, path);
        var border = Box.Focused ? Theme.Accent : Enabled ? BorderColor : Theme.Divider;
        using var p = new Pen(border);
        g.DrawPath(p, path);
    }
}

/// <summary>Dark drop-down list (owner-drawn) inside a bordered box.</summary>
class DarkCombo : Control
{
    public ComboBox Combo { get; } = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        DrawMode = DrawMode.OwnerDrawFixed,
        BackColor = Theme.Background,
        ForeColor = Theme.Text,
    };

    public DarkCombo()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Controls.Add(Combo);
        Combo.DrawItem += DrawItem;
        Combo.GotFocus += (_, _) => Invalidate();
        Combo.LostFocus += (_, _) => Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        Combo.Font = Font;
        Combo.ItemHeight = (int)(Font.GetHeight() + 10 * DeviceDpi / 96f);
        base.OnFontChanged(e);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = (int)(4 * DeviceDpi / 96f);
        Combo.SetBounds(pad, (Height - Combo.Height) / 2, Width - pad * 2, Combo.Height);
    }

    void DrawItem(object? sender, DrawItemEventArgs e)
    {
        bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var b = new SolidBrush(selected ? Theme.Primary : Theme.Background)) e.Graphics.FillRectangle(b, e.Bounds);
        string text = e.Index >= 0 ? Combo.GetItemText(Combo.Items[e.Index]) ?? "" : "";
        var r = Rectangle.Inflate(e.Bounds, -(int)(8 * DeviceDpi / 96f), 0);
        TextRenderer.DrawText(e.Graphics, text, Combo.Font, r, Combo.Enabled ? Theme.Text : Theme.Disabled,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent is Card c ? c.Fill : Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * DeviceDpi / 96f);
        using (var b = new SolidBrush(Theme.Background)) g.FillPath(b, path);
        using var p = new Pen(Combo.Focused ? Theme.Accent : Theme.InputBorder);
        g.DrawPath(p, path);
    }
}
