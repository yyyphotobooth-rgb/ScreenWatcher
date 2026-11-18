using System.ComponentModel;
using ScreenWatcher.Config;

namespace ScreenWatcher.UI;

/// <summary>
/// One row of the main window's alert table. <see cref="Implemented"/> rows work now; the others
/// are drawn disabled with "เร็วๆ นี้" until their detector is reliable (switch them on here).
/// Rows with a <see cref="Unit"/> have an editable threshold; <see cref="SideChooser"/> rows can
/// show the ซ้าย/ขวา chooser (two icons of the same potion type on the item bar).
/// </summary>
public sealed record AlertRowSpec(string Id, string Label, bool Implemented, string? Unit = null, int? DefaultThreshold = null,
    int MaxThreshold = 9999, bool SideChooser = false);

public static class AlertRowCatalog
{
    /// <summary>Rows in display order (owner's spec).</summary>
    public static readonly IReadOnlyList<AlertRowSpec> All =
    [
        new(AlertIds.Death, "ตาย", Implemented: true),
        new(AlertIds.GameDropped, "เกมส์หลุด", Implemented: true),
        new(AlertIds.GunAmmoEmpty, "กระสุนปืนกลหมด", Implemented: true),
        new(AlertIds.MissileEmpty, "มิสไซล์หมด", Implemented: true),
        new(AlertIds.AmmoBox, "กล่องเติมกระสุนเหลือ", Implemented: true, "กล่อง", 5),
        new(AlertIds.FuelLow, "น้ำมันต่ำกว่า", Implemented: true, "%", 10, MaxThreshold: 100),
        new(AlertIds.FuelBottles, "ขวดน้ำมันเหลือ", Implemented: true, "ขวด", 2),
        new(AlertIds.MonsterCard, "บัตรกันมอนเหลือ", Implemented: true, "ใบ", 3),
        new(AlertIds.HpPotions, "ยาเลือดเหลือ", Implemented: true, "ขวด", 5, SideChooser: true),
        new(AlertIds.ShieldPotions, "ยาเกราะเหลือ", Implemented: true, "ขวด", 5, SideChooser: true),
        new(AlertIds.SpPotions, "ยา SP เหลือ", Implemented: true, "ขวด", 5, SideChooser: true),
        new(AlertIds.Kill300, "ยิงมอนครบ 300 ตัว", Implemented: true),
    ];

    public static AlertRowSpec Get(string id) => All.First(r => r.Id == id);

    /// <summary>The threshold in use for a row: the saved one when valid, else the default.</summary>
    public static int Threshold(AppSettings settings, AlertRowSpec spec) =>
        settings.Thresholds.TryGetValue(spec.Id, out int v) && v >= 0 && v <= spec.MaxThreshold ? v : spec.DefaultThreshold ?? 0;
}

/// <summary>Table row: checkbox · label (· ซ้าย/ขวา chooser) · optional threshold box + unit · "ตอนนี้" value.</summary>
sealed class AlertRow : Control
{
    public const string ComingSoon = "เร็วๆ นี้";

    readonly DarkCheck _check = new();
    readonly Label _label = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    readonly Label _reached = new() { AutoSize = false, TextAlign = ContentAlignment.TopLeft, Text = "ถึงเกณฑ์เตือนแล้ว", Visible = false, UseMnemonic = false };
    readonly Label _left = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Text = "ซ้าย", Visible = false, UseMnemonic = false, Cursor = Cursors.Hand };
    readonly Label _right = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Text = "ขวา", Visible = false, UseMnemonic = false, Cursor = Cursors.Hand };
    readonly InputBox? _threshold;
    readonly Label? _unit;
    readonly Label _now = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, UseMnemonic = false };
    bool _reachedOn, _chooserOn, _watchRight;
    int _thresholdValue;

    public AlertRowSpec Spec { get; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool LastRow { get; set; }

    /// <summary>User switched the alert on/off (implemented rows only).</summary>
    public event Action<AlertRow, bool>? AlertToggled;
    /// <summary>User typed a new valid threshold.</summary>
    public event Action<AlertRow, int>? ThresholdChanged;
    /// <summary>User picked ซ้าย (false) or ขวา (true).</summary>
    public event Action<AlertRow, bool>? SideChanged;

    public AlertRow(AlertRowSpec spec)
    {
        Spec = spec;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        _label.Text = spec.Label;
        _label.Font = Theme.Body;
        _reached.Font = Theme.Small;
        _left.Font = _right.Font = Theme.Small;
        _now.Font = Theme.Value;
        _label.Click += (_, _) => { if (_check.Enabled) _check.Checked = !_check.Checked; };
        Controls.AddRange([_check, _label, _reached, _left, _right, _now]);
        AccessibleName = spec.Label;
        _check.AccessibleName = spec.Label;
        _left.AccessibleName = spec.Label + " (ซ้าย)";
        _right.AccessibleName = spec.Label + " (ขวา)";
        _left.Click += (_, _) => PickSide(false);
        _right.Click += (_, _) => PickSide(true);

        if (spec.Unit is not null)
        {
            _thresholdValue = spec.DefaultThreshold ?? 0;
            _threshold = new InputBox { Font = Theme.Body, Radius = 6 };
            _threshold.Box.TextAlign = HorizontalAlignment.Center;
            _threshold.Box.MaxLength = 4;
            _threshold.Box.Text = _thresholdValue.ToString();
            _threshold.Box.AccessibleName = spec.Label + " (เกณฑ์เตือน)";
            _unit = new Label { AutoSize = false, Text = spec.Unit, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
            Controls.AddRange([_threshold, _unit]);
            if (spec.Implemented)
            {
                _threshold.Box.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true; };
                _threshold.Box.TextChanged += (_, _) => OnThresholdText();
                _threshold.Box.Leave += (_, _) => _threshold.Box.Text = _thresholdValue.ToString();
            }
        }

        if (spec.Implemented)
        {
            _check.CheckedChanged += (_, _) => AlertToggled?.Invoke(this, _check.Checked);
            _now.Text = "—";
        }
        else
        {
            _check.Checked = true;
            _check.Enabled = false;
            if (_threshold is not null) _threshold.Enabled = false;
            _now.Text = ComingSoon;
            _now.Font = Theme.Small;
        }
        ApplyColours();
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AlertEnabled
    {
        get => _check.Checked;
        set => _check.Checked = value;
    }

    /// <summary>The row's alert threshold (rows with a unit). Setting it does not raise ThresholdChanged.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Threshold
    {
        get => _thresholdValue;
        set
        {
            _thresholdValue = value;
            if (_threshold is not null && _threshold.Box.Text != value.ToString()) _threshold.Box.Text = value.ToString();
        }
    }

    /// <summary>Which of two same-type potion icons is watched (false = left). Does not raise SideChanged.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool WatchRight
    {
        get => _watchRight;
        set { _watchRight = value; ApplyColours(); }
    }

    void OnThresholdText()
    {
        if (!int.TryParse(_threshold!.Box.Text, out int v) || v < 0 || v > Spec.MaxThreshold || v == _thresholdValue) return;
        _thresholdValue = v;
        ThresholdChanged?.Invoke(this, v);
    }

    void PickSide(bool right)
    {
        if (_watchRight == right) return;
        _watchRight = right;
        ApplyColours();
        SideChanged?.Invoke(this, right);
    }

    /// <summary>The "ตอนนี้" column (implemented rows only).</summary>
    public void SetNow(string text, Color? colour = null)
    {
        if (!Spec.Implemented) return;
        if (_now.Text != text) _now.Text = text;
        _now.ForeColor = colour ?? (_reachedOn ? Theme.WarnValue : Theme.Text);
    }

    /// <summary>The mockup's orange "threshold reached" style.</summary>
    public void SetReached(bool on)
    {
        if (_reachedOn == on) return;
        _reachedOn = on;
        _reached.Visible = on;
        _now.Font = on ? Theme.ValueBold : Spec.Implemented ? Theme.Value : Theme.Small;
        ApplyColours();
        PerformLayout();
        Invalidate();
    }

    /// <summary>Shows the small ซ้าย/ขวา chooser (two icons of this potion type are on the bar).</summary>
    public void SetChooserVisible(bool on)
    {
        on &= Spec.SideChooser && Spec.Implemented;
        if (_chooserOn == on) return;
        _chooserOn = on;
        _left.Visible = _right.Visible = on;
        PerformLayout();
    }

    void ApplyColours()
    {
        var bg = _reachedOn ? Theme.WarnBackground : Theme.Surface;
        bool live = Spec.Implemented;
        foreach (Control c in Controls) c.BackColor = bg;
        _label.ForeColor = live ? Theme.Text : Theme.Disabled;
        _reached.ForeColor = Theme.WarnText;
        _now.ForeColor = _reachedOn ? Theme.WarnValue : live ? Theme.Text : Theme.Disabled;
        if (_unit is not null) _unit.ForeColor = _reachedOn ? Theme.WarnText : live ? Theme.Muted : Theme.Disabled;
        if (_threshold is not null) { _threshold.BorderColor = _reachedOn ? Theme.WarnBorder : Theme.InputBorder; _threshold.Invalidate(); }
        // Chooser: the picked side is a filled pill, the other plain text.
        (_left.BackColor, _left.ForeColor) = !_watchRight ? (Theme.Primary, Theme.Text) : (bg, Theme.Muted);
        (_right.BackColor, _right.ForeColor) = _watchRight ? (Theme.Primary, Theme.Text) : (bg, Theme.Muted);
    }

    int S(float px) => (int)Math.Round(px * DeviceDpi / 96f);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // Mockup: padding 0 14px, gap 12px; checkbox 18; threshold column 92; now column 72.
        int pad = S(14), gap = S(12), h = Height;
        int x = pad;
        _check.SetBounds(x, (h - S(18)) / 2, S(18), S(18));
        x += S(18) + gap;
        int nowW = S(72), thrW = S(92);
        int nowX = Width - pad - nowW;
        _now.SetBounds(nowX, 0, nowW, h);
        int labelRight = nowX - gap;
        if (_threshold is not null)
        {
            int tx = nowX - gap - thrW;
            _threshold.SetBounds(tx, (h - S(32)) / 2, S(56), S(32));
            _unit!.SetBounds(tx + S(56) + S(6), 0, thrW - S(62), h);
            labelRight = tx - gap;
        }
        if (_reachedOn || _chooserOn)
        {
            // Two lines: the label, then "ถึงเกณฑ์เตือนแล้ว" and/or the ซ้าย/ขวา chooser.
            int lh = _label.Font.Height + S(2), sh = _reached.Font.Height + S(2);
            int top = Math.Max(0, (h - lh - sh) / 2);
            _label.SetBounds(x, top, labelRight - x, lh);
            int sx = x;
            if (_chooserOn)
            {
                int cw = S(40);
                _left.SetBounds(sx, top + lh, cw, sh);
                _right.SetBounds(sx + cw + S(4), top + lh, cw, sh);
                sx += cw * 2 + S(4) + S(8);
            }
            if (_reachedOn) _reached.SetBounds(sx, top + lh, Math.Max(0, labelRight - sx), sh);
        }
        else
        {
            _label.SetBounds(x, 0, labelRight - x, h);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_reachedOn ? Theme.WarnBackground : Theme.Surface);
        if (!LastRow)
        {
            using var p = new Pen(Theme.Divider);
            e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }
}
