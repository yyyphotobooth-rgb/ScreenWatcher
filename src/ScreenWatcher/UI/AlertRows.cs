using System.ComponentModel;
using ScreenWatcher.Config;

namespace ScreenWatcher.UI;

/// <summary>
/// One row of the main window's alert table. <see cref="Implemented"/> rows work now; the others
/// are drawn disabled with "เร็วๆ นี้" until their detector is reliable (switch them on here).
/// Rows with a <see cref="Unit"/> have an editable threshold; <see cref="SideChooser"/> rows can
/// show the ซ้าย/ขวา chooser (two icons of the same potion type on the item bar).
/// </summary>
/// Rows with <see cref="Subs"/> have more lines: each with its own checkbox, label and "ตอนนี้" value
/// (own alert id and saved on/off), e.g. the buffs under the kill counter.
public sealed record AlertRowSpec(string Id, string Label, bool Implemented, string? Unit = null, int? DefaultThreshold = null,
    int MaxThreshold = 9999, bool SideChooser = false, int MinThreshold = 0, bool ResetButton = false, bool WideNow = false, int NowWidth = 72,
    SubLine[]? Subs = null)
{
    /// <summary>Row height at 96 dpi (36 per line when the row has sub-lines).</summary>
    public int Height => !Implemented ? 44 : Subs is { Length: > 0 } ? 36 * (Subs.Length + 1) : 48;
}

/// <summary>An extra line of a row: own alert id (settings key) and label.</summary>
public sealed record SubLine(string Id, string Label);

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
        new(AlertIds.HpPotions, "ยาเลือดเหลือ", Implemented: true, "ชิ้น", 5, SideChooser: true),
        new(AlertIds.ShieldPotions, "ยาเกราะเหลือ", Implemented: true, "ชิ้น", 5, SideChooser: true),
        new(AlertIds.SpPotions, "ยา SP เหลือ", Implemented: true, "ชิ้น", 5, SideChooser: true),
        // Lines 2 and 3: the EXP card buff / the handshake buff left the buff bar ("ตอนนี้": มีบัพ / ไม่มีบัพ).
        new(AlertIds.Kill300, "ยิงมอนครบ 300 ตัว", Implemented: true,
            Subs: [new(AlertIds.ExpCard, "บัพมอน 300 ตัว"), new(AlertIds.Handshake, "บัพจับมือ")]),
        // War pillars: one alert per pillar at N minutes left. "ตอนนี้": every timer left to right
        // ("00:15 · 00:42"; "00:15 +1" when they do not fit), ไม่มี, or ? (tooltip over a panel).
        new(AlertIds.WarPillar, "เสาวอ แจ้งเมื่อเหลือ", Implemented: true, "นาที", 10, MaxThreshold: 120, MinThreshold: 1, NowWidth: 118),
        // "ตอนนี้": ปกติ / เต็ม (รอรีเซ็ต) (latched until "รีเซ็ต" or /reset) / ? (message box not seen).
        new(AlertIds.InventoryFull, "ช่องเก็บของเต็ม", Implemented: true, ResetButton: true),
        // "ตอนนี้": Lv.N · P% (Lv.N when the percentage is unreadable), or ? when the level line cannot be read.
        new(AlertIds.LevelUp, "เลเวลอัพ", Implemented: true, WideNow: true),
        // "ตอนนี้": ปกติ, นิ่ง N นาที, or ? when the percentage cannot be read (detectors.json "expStall").
        new(AlertIds.ExpStall, "EXP ไม่ขยับเกิน", Implemented: true, "นาที", 5, MaxThreshold: 120, MinThreshold: 1, NowWidth: 100),
        // "ตอนนี้": ปกติ, or the short names of the rows over the limit (detectors.json "unreadable").
        new(AlertIds.Unreadable, "อ่านค่าไม่ได้เกิน", Implemented: true, "วินาที", 10, MaxThreshold: 3600, MinThreshold: 1),
    ];

    public static AlertRowSpec Get(string id) => All.First(r => r.Id == id);

    /// <summary>The threshold in use for a row: the saved one when valid, else the default.</summary>
    public static int Threshold(AppSettings settings, AlertRowSpec spec) =>
        settings.Thresholds.TryGetValue(spec.Id, out int v) && v >= spec.MinThreshold && v <= spec.MaxThreshold ? v : spec.DefaultThreshold ?? 0;
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
    readonly Label? _reset;
    readonly Label _now = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, UseMnemonic = false };
    // Extra lines (Spec.Subs): own checkbox, label and value each.
    readonly DarkCheck[] _subCheck = [];
    readonly Label[] _subLabel = [], _subNow = [];
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
    /// <summary>User clicked "รีเซ็ต" (rows with ResetButton).</summary>
    public event Action<AlertRow>? ResetClicked;
    /// <summary>User switched an extra line's alert on/off (rows with Subs): the line's alert id.</summary>
    public event Action<AlertRow, string, bool>? SubAlertToggled;

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

        if (spec.ResetButton)
        {
            _reset = new Label
            {
                AutoSize = false, Text = "รีเซ็ต", Font = Theme.Small, TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false,
                Cursor = Cursors.Hand, AccessibleName = spec.Label + " (รีเซ็ต)", AccessibleRole = AccessibleRole.PushButton,
            };
            _reset.Click += (_, _) => ResetClicked?.Invoke(this);
            Controls.Add(_reset);
        }

        if (spec.Subs is { Length: > 0 } subs && spec.Implemented)
        {
            // Build every line first and add them in one go: Controls.AddRange runs OnLayout, which
            // walks ALL of _subCheck/_subLabel/_subNow (adding line by line crashed 1.3.0 with 2 lines).
            var subCheck = new DarkCheck[subs.Length];
            var subLabel = new Label[subs.Length];
            var subNow = new Label[subs.Length];
            var added = new List<Control>(subs.Length * 3);
            for (int i = 0; i < subs.Length; i++)
            {
                var sub = subs[i];
                var check = subCheck[i] = new DarkCheck { AccessibleName = sub.Label };
                var label = subLabel[i] = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Text = sub.Label, Font = Theme.Body };
                subNow[i] = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, UseMnemonic = false, Text = "—", Font = Theme.Value };
                label.Click += (_, _) => check.Checked = !check.Checked;
                check.CheckedChanged += (_, _) => SubAlertToggled?.Invoke(this, sub.Id, check.Checked);
                added.AddRange([check, label, subNow[i]]);
            }
            (_subCheck, _subLabel, _subNow) = (subCheck, subLabel, subNow);
            Controls.AddRange([.. added]);
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

    int SubIndex(string id) => Spec.Subs is { } subs && _subCheck.Length == subs.Length ? Array.FindIndex(subs, s => s.Id == id) : -1;

    /// <summary>An extra line's alert switch (false for an unknown id).</summary>
    public bool GetSubAlertEnabled(string id) => SubIndex(id) is int i && i >= 0 && _subCheck[i].Checked;

    public void SetSubAlertEnabled(string id, bool on)
    {
        int i = SubIndex(id);
        if (i >= 0) _subCheck[i].Checked = on;
    }

    /// <summary>An extra line's "ตอนนี้" value.</summary>
    public void SetSubNow(string id, string text)
    {
        int i = SubIndex(id);
        if (i < 0 || _subNow[i].Text == text) return;
        _subNow[i].Text = text;
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
        if (!int.TryParse(_threshold!.Box.Text, out int v) || v < Spec.MinThreshold || v > Spec.MaxThreshold || v == _thresholdValue) return;
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

    /// <summary>"ตอนนี้" for a comma-separated list of names: all of them when they fit the column,
    /// else the first one and "+N" for the rest.</summary>
    public void SetNowList(string text, Color? colour = null, string separator = ", ")
    {
        var names = text.Split(separator);
        if (names.Length > 1 && TextRenderer.MeasureText(text, _now.Font).Width > _now.Width)
        {
            text = $"{names[0]} +{names.Length - 1}";
            // Still too long ("00:08 (100%) +2"): the first item without its bracket ("00:08 +2").
            int paren = names[0].IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0 && TextRenderer.MeasureText(text, _now.Font).Width > _now.Width)
                text = $"{names[0][..paren]} +{names.Length - 1}";
        }
        SetNow(text, colour);
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
        foreach (var l in _subLabel) l.ForeColor = Theme.Text;
        foreach (var l in _subNow) l.ForeColor = Theme.Text;
        if (_unit is not null) _unit.ForeColor = _reachedOn ? Theme.WarnText : live ? Theme.Muted : Theme.Disabled;
        if (_reset is not null) (_reset.BackColor, _reset.ForeColor) = (Theme.Primary, Theme.Text);
        if (_threshold is not null) { _threshold.BorderColor = _reachedOn ? Theme.WarnBorder : Theme.InputBorder; _threshold.Invalidate(); }
        // Chooser: the picked side is a filled pill, the other plain text.
        (_left.BackColor, _left.ForeColor) = !_watchRight ? (Theme.Primary, Theme.Text) : (bg, Theme.Muted);
        (_right.BackColor, _right.ForeColor) = _watchRight ? (Theme.Primary, Theme.Text) : (bg, Theme.Muted);
    }

    int S(float px) => (int)Math.Round(px * DeviceDpi / 96f);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // Mockup: padding 0 14px, gap 12px; checkbox 18; threshold column 100 (box 52 + unit; wide
        // enough for "กล่อง" on one line); now column 72.
        int pad = S(14), gap = S(12), h = Height;
        int x = pad;
        _check.SetBounds(x, (h - S(18)) / 2, S(18), S(18));
        x += S(18) + gap;
        int nowW = S(Spec.NowWidth), thrW = S(100);   // NowWidth 72 except "EXP ไม่ขยับเกิน" ("นิ่ง 120 นาที")
        int nowX = Width - pad - nowW;
        if (_subCheck.Length > 0)
        {
            // Lines of equal height, each: checkbox · label · value ("ตอนนี้" also takes the
            // threshold column: "ไม่มีบัพ" is long). The "ถึงเกณฑ์เตือนแล้ว" line is not shown here
            // (the orange background and value still are).
            int lh = h / (_subCheck.Length + 1), vx = nowX - gap - S(100), lx = x + S(18) + gap;
            _check.SetBounds(x, (lh - S(18)) / 2, S(18), S(18));
            _label.SetBounds(lx, 0, vx - gap - lx, lh);
            _now.SetBounds(vx, 0, Width - pad - vx, lh);
            for (int i = 0; i < _subCheck.Length; i++)
            {
                int y = lh * (i + 1);
                _subCheck[i].SetBounds(x, y + (lh - S(18)) / 2, S(18), S(18));
                _subLabel[i].SetBounds(lx, y, vx - gap - lx, lh);
                _subNow[i].SetBounds(vx, y, Width - pad - vx, lh);
            }
            _reached.Visible = false;
            return;
        }
        _now.SetBounds(nowX, 0, nowW, h);
        int labelRight = nowX - gap;
        if (_reset is not null)
        {
            // "รีเซ็ต" in the threshold column; "ตอนนี้" takes the rest of it ("เต็ม (รอรีเซ็ต)" is long).
            int tx = nowX - gap - thrW, bw = S(56);
            _reset.SetBounds(tx, (h - S(28)) / 2, bw, S(28));
            _now.SetBounds(tx + bw + S(6), 0, Width - pad - (tx + bw + S(6)), h);
            labelRight = tx - gap;
        }
        else if (Spec.WideNow && _threshold is null)
        {
            // No threshold: "ตอนนี้" also takes the threshold column ("Lv.88 · 50.61%" is long).
            int tx = nowX - gap - thrW;
            _now.SetBounds(tx, 0, Width - pad - tx, h);
            labelRight = tx - gap;
        }
        else if (_threshold is not null)
        {
            int tx = nowX - gap - thrW;
            _threshold.SetBounds(tx, (h - S(32)) / 2, S(52), S(32));
            _unit!.SetBounds(tx + S(52) + S(5), 0, thrW - S(57), h);
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
