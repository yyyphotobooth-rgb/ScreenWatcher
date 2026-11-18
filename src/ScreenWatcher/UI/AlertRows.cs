using System.ComponentModel;
using ScreenWatcher.Config;

namespace ScreenWatcher.UI;

/// <summary>
/// One row of the main window's alert table. <see cref="Implemented"/> rows work now; the others
/// are drawn disabled with "เร็วๆ นี้" until their detector exists (switch them on here).
/// </summary>
public sealed record AlertRowSpec(string Id, string Label, bool Implemented, string? Unit = null, int? DefaultThreshold = null);

public static class AlertRowCatalog
{
    /// <summary>Rows in display order (task spec). Thresholds/units of the "coming soon" rows are
    /// placeholders; the mockup only shows น้ำมัน 10 %, บัตรกันมอน 3 ใบ, ขวดน้ำมัน 2 ขวด.</summary>
    public static readonly IReadOnlyList<AlertRowSpec> All =
    [
        new(AlertIds.Death, "ตาย", Implemented: true),
        new(AlertIds.GameDropped, "เกมส์หลุด", Implemented: true),
        new("gunAmmoEmpty", "กระสุนปืนกลหมด", Implemented: false),
        new("missileEmpty", "มิสไซล์หมด", Implemented: false),
        new("fuelLow", "น้ำมันต่ำกว่า", Implemented: false, "%", 10),
        new("monsterCard", "บัตรกันมอนเหลือ", Implemented: false, "ใบ", 3),
        new("fuelBottles", "ขวดน้ำมันเหลือ", Implemented: false, "ขวด", 2),
        new("hpPotions", "ยาเลือดเหลือ", Implemented: false, "ขวด", 5),
        new("shieldPotions", "ยาเกราะเหลือ", Implemented: false, "ขวด", 5),
        new("spPotions", "ยา SP เหลือ", Implemented: false, "ขวด", 5),
        new("kill300", "ยิงมอนครบ 300 ตัว", Implemented: false),
    ];
}

/// <summary>Table row: checkbox · label · optional threshold box + unit · "ตอนนี้" value.</summary>
sealed class AlertRow : Control
{
    public const string ComingSoon = "เร็วๆ นี้";

    readonly DarkCheck _check = new();
    readonly Label _label = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    readonly Label _reached = new() { AutoSize = false, TextAlign = ContentAlignment.TopLeft, Text = "ถึงเกณฑ์เตือนแล้ว", Visible = false, UseMnemonic = false };
    readonly InputBox? _threshold;
    readonly Label? _unit;
    readonly Label _now = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, UseMnemonic = false };
    bool _reachedOn;

    public AlertRowSpec Spec { get; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool LastRow { get; set; }

    /// <summary>User switched the alert on/off (implemented rows only).</summary>
    public event Action<AlertRow, bool>? AlertToggled;

    public AlertRow(AlertRowSpec spec)
    {
        Spec = spec;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        _label.Text = spec.Label;
        _label.Font = Theme.Body;
        _reached.Font = Theme.Small;
        _now.Font = Theme.Value;
        _label.Click += (_, _) => { if (_check.Enabled) _check.Checked = !_check.Checked; };
        Controls.AddRange([_check, _label, _reached, _now]);
        AccessibleName = spec.Label;
        _check.AccessibleName = spec.Label;

        if (spec.Unit is not null)
        {
            _threshold = new InputBox { Font = Theme.Body, Radius = 6 };
            _threshold.Box.TextAlign = HorizontalAlignment.Center;
            _threshold.Box.Text = spec.DefaultThreshold?.ToString() ?? "";
            _threshold.Box.AccessibleName = spec.Label + " (เกณฑ์เตือน)";
            _unit = new Label { AutoSize = false, Text = spec.Unit, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
            Controls.AddRange([_threshold, _unit]);
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

    /// <summary>The "ตอนนี้" column (implemented rows only).</summary>
    public void SetNow(string text, Color? colour = null)
    {
        if (!Spec.Implemented) return;
        _now.Text = text;
        _now.ForeColor = colour ?? (_reachedOn ? Theme.WarnValue : Theme.Text);
    }

    /// <summary>The mockup's orange "threshold reached" style. Exists for the later threshold
    /// detectors; nothing calls it with true yet.</summary>
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
        if (_reachedOn)
        {
            int lh = _label.Font.Height + S(2);
            int top = (h - lh - _reached.Font.Height) / 2;
            _label.SetBounds(x, top, labelRight - x, lh);
            _reached.SetBounds(x, top + lh, labelRight - x, _reached.Font.Height + S(2));
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
