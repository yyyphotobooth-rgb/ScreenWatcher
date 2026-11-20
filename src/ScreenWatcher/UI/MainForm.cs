using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Discord.Net;
using ScreenWatcher.Alerts;
using ScreenWatcher.Bot;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Diagnostics;
using ScreenWatcher.ExpTest;
using ScreenWatcher.Imaging;
using ScreenWatcher.Logging;

using ScreenWatcher.Update;

namespace ScreenWatcher.UI;

/// <summary>
/// One window, two pages (design/mockup-main-A.html and design/mockup-discord-settings.html).
/// The X button exits the app; minimise goes to the taskbar. There is no tray icon.
/// </summary>
public sealed class MainForm : Form
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly WatcherConfig _config;
    readonly AppSettings _settings;
    readonly TokenStore _tokens = new(TokenStore.DefaultPath);
    readonly AppLog _log = new(AppLog.DefaultPath);
    readonly Watcher _watcher;
    readonly DiscordBot _bot;
    readonly AlertService _alerts;
    readonly DiagnosticsHub _diag;
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    readonly ToolTip _tip = new() { AutoPopDelay = 15000 };
    long _measuredBytes = -1;
    int _sizeTick;
    readonly bool _noSend;
    readonly Dictionary<string, string> _loggedRows = [];

    // Main page
    readonly Panel _mainPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    readonly Label _watchState = MakeLabel("เฝ้าอยู่", Theme.Small, Theme.Muted, Theme.Surface, ContentAlignment.MiddleRight);
    readonly Label _gameValue = MakeLabel("กำลังตรวจ...", Theme.Body, Theme.Muted, Theme.Surface);
    readonly Label _gameDetail = MakeLabel("", Theme.Small, Theme.Muted, Theme.Surface);
    readonly Label _discordValue = MakeLabel("", Theme.Body, Theme.Muted, Theme.Surface);
    readonly Label _discordDetail = MakeLabel("", Theme.Small, Theme.Muted, Theme.Surface);
    readonly List<AlertRow> _rows = [];
    readonly DarkButton _pause = new() { Kind = ButtonKind.Primary, Text = "หยุดเฝ้าชั่วคราว" };
    readonly DarkButton _testMain = new() { Text = "ทดสอบส่ง" };
    readonly DarkButton _expButton = new() { Text = "ทดสอบ EXP" };
    readonly ExpTestService _exp;
    readonly DarkButton _openSettings = new() { Text = "ตั้งค่า" };
    readonly Label _lastAlert = MakeLabel("แจ้งเตือนล่าสุด: ยังไม่มี", Theme.Small, Theme.Muted, Theme.Background);
    // Update banner (hidden until GitHub has a newer release; installs only on the button click).
    readonly Card _updateBanner = new() { Visible = false };
    readonly Label _updateText = MakeLabel("", Theme.Body, Theme.Text, Theme.Surface);
    readonly DarkButton _updateButton = new() { Kind = ButtonKind.Primary, Text = "อัปเดต" };
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    UpdateInfo? _update;
    bool _updating;

    // Settings page
    readonly Panel _settingsPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background, AutoScroll = true, Visible = false };
    readonly DarkButton _back = new() { Kind = ButtonKind.Link, Text = "‹ กลับ" };
    readonly DarkButton _guide = new() { Text = "เปิดคู่มือสร้างบอท", SecondaryFill = Theme.Background };
    readonly InputBox _token = new() { Font = Theme.Body };
    readonly DarkButton _connect = new() { Kind = ButtonKind.Primary, Text = "เชื่อมต่อ" };
    readonly Label _botStatus = MakeLabel("", Theme.Small, Theme.Muted, Theme.Surface);
    readonly DarkCombo _channels = new() { Font = Theme.Body };
    readonly DarkButton _invite = new() { Text = "เชิญบอทเข้าเซิร์ฟเวอร์", SecondaryFill = Theme.Background, Visible = false };
    readonly Label _channelNote = MakeLabel("", Theme.Small, Theme.Muted, Theme.Surface);
    readonly DarkButton _testSettings = new() { Text = "ทดสอบส่ง", SecondaryFill = Theme.Background };
    readonly Label _testNote = MakeLabel("ส่งข้อความลองเข้ามือถือ", Theme.Small, Theme.Muted, Theme.Surface);
    readonly DarkButton[] _modeButtons =
    [
        new() { Text = CollectModes.Thai(CollectMode.Off), SecondaryFill = Theme.Background },
        new() { Text = CollectModes.Thai(CollectMode.Auto), SecondaryFill = Theme.Background },
        new() { Text = CollectModes.Thai(CollectMode.Always), SecondaryFill = Theme.Background },
    ];
    readonly Label _modeHint = MakeLabel("", Theme.Small, Theme.Muted, Theme.Surface, ContentAlignment.TopLeft);
    readonly DarkCheck _evidence = new() { BackColor = Theme.Surface };
    readonly Label _evidenceLabel = MakeLabel("เก็บภาพทุกครั้งที่เตือน", Theme.Body, Theme.Text, Theme.Surface);
    readonly Label _collectSize = MakeLabel("-", Theme.Small, Theme.Muted, Theme.Surface);
    readonly Label _evidenceSize = MakeLabel("-", Theme.Small, Theme.Muted, Theme.Surface);
    readonly Label _journalSize = MakeLabel("-", Theme.Small, Theme.Muted, Theme.Surface);
    readonly DarkButton _openFolder = new() { Text = "เปิดโฟลเดอร์", SecondaryFill = Theme.Background };
    readonly DarkButton _openEvidence = new() { Text = "เปิดโฟลเดอร์", SecondaryFill = Theme.Background };
    readonly DarkButton _openJournal = new() { Text = "เปิดโฟลเดอร์", SecondaryFill = Theme.Background };
    CollectMode _collectMode;
    bool _suppressChannelEvent;

    public MainForm(WatcherConfig config, bool noSend = false)
    {
        _config = config;
        _noSend = noSend;
        _settings = AppSettings.Load(AppSettings.DefaultPath);

        AutoScaleMode = AutoScaleMode.None; // layout below is scaled by DeviceDpi by hand
        Text = noSend ? AppFiles.Title + " (ไม่ส่ง Discord)" : AppFiles.Title;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
        ShowInTaskbar = true;
        ClientSize = new Size(S(460), S(800));

        _watcher = new Watcher(config);
        var d = config.Diagnostics;
        _diag = new DiagnosticsHub(config,
            new Journal(AppSettings.JournalRoot, (long)(d.JournalMaxMegabytes * 1024 * 1024), _log.Scrub),
            new EvidenceStore(AppSettings.EvidenceRoot, (long)(d.EvidenceMaxMegabytes * 1024 * 1024), d.EvidenceJpegQuality),
            RowText, RowThreshold, _watcher.CaptureForMiss, AlertRowCatalog.All.Select(r => r.Id).ToArray())
        {
            SaveEvidence = _settings.SaveAlertEvidence,
        };
        _diag.Evidence.Start();
        ScreenWatcher.Update.UpdateInstaller.CleanupTask?.ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            var r = t.Result;
            _diag.Journal.Write("update_cleanup", ("deleted", r.Deleted), ("attempts", r.Attempts), ("ms", (long)r.Elapsed.TotalMilliseconds), ("error", r.Error));
            _log.Write(r.Deleted ? "Old exe (.old) deleted" : $"Old exe (.old) NOT deleted: {r.Error}");
        }, TaskScheduler.Default);
        // EXP test: messages go through the alert queue (so --no-send only logs them).
        _exp = new ExpTestService(_watcher.ExpTest, new ExpTestStore(AppSettings.ExpTestRoot), m => _alerts!.Enqueue(m), _diag.Journal, config.ExpTest.ListCount);
        _exp.ResultReady += (r, file) => _log.Write($"EXP test result: {r.Text}" + (file is null ? " (not saved)" : $" -> {file}"));
        _watcher.ExpTest.StartDecided += (o, source) => _log.Write($"EXP test start ({source}): {o.Text}");
        _bot = new DiscordBot(_log,
            new SsCommandHandler(_watcher.Snapshot, () => _bot!.ChosenChannelId, o => _diag.OnCommand(SsCommandHandler.CommandName, o)),
            new MissCommandHandler(_diag.RecordMiss, () => _bot!.ChosenChannelId, o => _diag.OnCommand(MissCommandHandler.CommandName, o)),
            new ResetCommandHandler(_watcher.ResetInventoryFull, () => _bot!.ChosenChannelId, o => _diag.OnCommand(ResetCommandHandler.CommandName, o)),
            new ExpCommandHandler(_exp, () => _bot!.ChosenChannelId, config.ExpTest.DefaultMinutes, config.ExpTest.MinMinutes, config.ExpTest.MaxMinutes,
                o => _diag.OnCommand(ExpCommandHandler.CommandName, o)))
        {
            ChosenGuildId = _settings.DiscordGuildId,
            ChosenChannelId = _settings.DiscordChannelId,
        };
        _alerts = new AlertService(new AlertComposer(config), noSend ? new LogOnlySink(_log) : _bot, _settings.IsAlertEnabled, _log,
            config.Alerts.RetryDelays, config.Alerts.QueueLimit);

        BuildMainPage();
        BuildSettingsPage();
        Controls.Add(_settingsPage);
        Controls.Add(_mainPage);

        _watcher.StatusChanged += s => { _diag.OnStatus(s); OnUi(UpdateGameTile); };
        _watcher.EventRaised += OnWatchEvent;
        _watcher.Polled += _diag.OnPolled;
        _watcher.Rearmed += _diag.OnRearmed;
        _watcher.UnreadableRecovered += _diag.OnUnreadableRecovered;
        _watcher.LevelIgnored += _diag.OnLevelIgnored;
        _bot.StatusChanged += s => { _diag.OnDiscordStatus(s.State.ToString(), s.Reason); OnUi(() => { UpdateDiscordTile(); UpdateBotStatus(); RefreshChannels(); }); };
        _bot.Problem += _diag.OnDiscordProblem;
        _bot.ChannelsChanged += () => OnUi(RefreshChannels);
        _alerts.Completed += (m, ok) =>
        {
            if (!_noSend) _diag.OnAlertCompleted(m, ok);
            OnUi(() => _lastAlert.Text = $"แจ้งเตือนล่าสุด: {m.Text} · {m.Time.ToString("HH:mm", Inv)}" + (ok ? "" : " (ส่งไม่สำเร็จ)"));
        };
        _uiTimer.Tick += (_, _) => OnUiTick();
    }

    int S(float px) => (int)Math.Round(px * DeviceDpi / 96f);

    static Label MakeLabel(string text, Font font, Color fore, Color back, ContentAlignment align = ContentAlignment.MiddleLeft) =>
        new() { Text = text, Font = font, ForeColor = fore, BackColor = back, AutoSize = false, TextAlign = align, UseMnemonic = false, AutoEllipsis = true };

    // ---------------------------------------------------------------- main page

    void BuildMainPage()
    {
        int W = S(460), pad = S(18);
        var header = MakeHeader(S(45));
        var title = MakeLabel("ScreenWatcher", Theme.BodyBold, Theme.Text, Theme.Surface);
        title.SetBounds(pad, 0, S(220), S(44));
        _watchState.SetBounds(W - pad - S(180), 0, S(180), S(44));
        header.Controls.AddRange([title, _watchState]);
        _mainPage.Controls.Add(header);

        int y = S(45) + pad;
        int tileW = (W - pad * 2 - S(12)) / 2, tileH = S(80);
        var gameTile = MakeTile("เกม", _gameValue, _gameDetail, tileW, tileH);
        gameTile.Location = new Point(pad, y);
        var discordTile = MakeTile("Discord", _discordValue, _discordDetail, tileW, tileH);
        discordTile.Location = new Point(pad + tileW + S(12), y);
        _mainPage.Controls.AddRange([gameTile, discordTile]);
        y += tileH + S(14);

        // Alert table
        var table = new Card { ClipToShape = true };
        int tableW = W - pad * 2;
        var colHeader = new Panel { BackColor = Theme.Surface };
        colHeader.SetBounds(0, 0, tableW, S(34));
        colHeader.Paint += (_, e) => { using var p = new Pen(Theme.Divider); e.Graphics.DrawLine(p, 0, colHeader.Height - 1, colHeader.Width, colHeader.Height - 1); };
        var h1 = MakeLabel("แจ้งเตือนเมื่อ", Theme.Small, Theme.Muted, Theme.Surface);
        var h2 = MakeLabel("เตือนที่", Theme.Small, Theme.Muted, Theme.Surface, ContentAlignment.MiddleCenter);
        var h3 = MakeLabel("ตอนนี้", Theme.Small, Theme.Muted, Theme.Surface, ContentAlignment.MiddleRight);
        // Mockup: padding 0 14px, gap 12px, first label padded 30px (checkbox width + gap).
        int c3 = tableW - S(14) - S(72), c2 = c3 - S(12) - S(100);
        h1.SetBounds(S(14) + S(30), 0, c2 - S(12) - S(44), S(33));
        h2.SetBounds(c2, 0, S(52), S(33));
        h3.SetBounds(c3, 0, S(72), S(33));
        colHeader.Controls.AddRange([h1, h2, h3]);
        table.Controls.Add(colHeader);

        // Everything below the table: gap, buttons, gap, last-alert line, bottom padding.
        int below = S(14) + S(44) + S(10) + S(44) + S(14) + S(22) + pad;
        int rowsH = AlertRowCatalog.All.Sum(s => S(s.Height));
        // One page: the whole list is shown when the screen is tall enough; only on a short screen
        // the list gets its own scroll bar (the rest of the window stays put).
        var work = Screen.FromPoint(Location).WorkingArea;
        int roomForRows = work.Bottom - Location.Y - SystemInformation.CaptionHeight - S(16) - y - S(34) - below;
        bool scroll = rowsH > roomForRows;
        int listH = scroll ? Math.Max(S(48) * 3, roomForRows) : rowsH;
        var list = new Panel { BackColor = Theme.Surface, AutoScroll = scroll };
        list.SetBounds(0, S(34), tableW, listH);
        int rowW = scroll ? tableW - SystemInformation.VerticalScrollBarWidth : tableW;

        int ry = 0;
        foreach (var spec in AlertRowCatalog.All)
        {
            var row = new AlertRow(spec);
            int rh = S(spec.Height);
            row.SetBounds(0, ry, rowW, rh);
            ry += rh;
            if (spec.Implemented)
            {
                row.AlertEnabled = _settings.IsAlertEnabled(spec.Id);
                _watcher.Options.SetEnabled(spec.Id, row.AlertEnabled);
                if (spec.SubId is { } subId)
                {
                    row.SubAlertEnabled = _settings.IsAlertEnabled(subId);
                    _watcher.Options.SetEnabled(subId, row.SubAlertEnabled);
                    row.SubAlertToggled += (r, on) =>
                    {
                        _settings.SetAlertEnabled(subId, on);
                        _watcher.Options.SetEnabled(subId, on);
                        SaveSettings();
                        _log.Write($"Alert {subId}: {(on ? "on" : "off")}");
                        _diag.OnSetting($"alert.{subId}.enabled", on);
                    };
                }
                row.AlertToggled += (r, on) =>
                {
                    _settings.SetAlertEnabled(r.Spec.Id, on);
                    _watcher.Options.SetEnabled(r.Spec.Id, on);
                    SaveSettings();
                    _log.Write($"Alert {r.Spec.Id}: {(on ? "on" : "off")}");
                    _diag.OnSetting($"alert.{r.Spec.Id}.enabled", on);
                };
                if (spec.Unit is not null)
                {
                    row.Threshold = AlertRowCatalog.Threshold(_settings, spec);
                    _watcher.Options.SetThreshold(spec.Id, row.Threshold);
                    row.ThresholdChanged += (r, v) =>
                    {
                        _settings.Thresholds[r.Spec.Id] = v;
                        _watcher.Options.SetThreshold(r.Spec.Id, v);
                        SaveSettings();
                        _log.Write($"Alert {r.Spec.Id}: threshold {v}");
                        _diag.OnSetting($"alert.{r.Spec.Id}.threshold", v);
                    };
                }
                if (spec.ResetButton)
                    row.ResetClicked += _ =>
                    {
                        bool was = _watcher.ResetInventoryFull();
                        _log.Write($"Inventory full reset (button; was latched: {was})");
                        _diag.OnCommand("reset", was ? "button_reset" : "button_not_latched");
                    };
                if (spec.SideChooser)
                {
                    row.WatchRight = _settings.WatchRight.GetValueOrDefault(spec.Id);
                    _watcher.Options.SetWatchRight(spec.Id, row.WatchRight);
                    row.SideChanged += (r, right) =>
                    {
                        _settings.WatchRight[r.Spec.Id] = right;
                        _watcher.Options.SetWatchRight(r.Spec.Id, right);
                        SaveSettings();
                        _log.Write($"Alert {r.Spec.Id}: watch {(right ? "right" : "left")}");
                        _diag.OnSetting($"alert.{r.Spec.Id}.side", right ? "right" : "left");
                    };
                }
            }
            _rows.Add(row);
            list.Controls.Add(row);
        }
        _rows[^1].LastRow = true;
        table.Controls.Add(list);
        table.SetBounds(pad, y, tableW, S(34) + listH);
        _mainPage.Controls.Add(table);
        y += S(34) + listH + S(14);

        // Buttons: [หยุดเฝ้าชั่วคราว (flex)] [ทดสอบส่ง] [ตั้งค่า]
        int bh = S(44), gap = S(10);
        int settingsW = ButtonWidth(_openSettings), testW = ButtonWidth(_testMain);
        _openSettings.SetBounds(W - pad - settingsW, y, settingsW, bh);
        _testMain.SetBounds(_openSettings.Left - gap - testW, y, testW, bh);
        _pause.SetBounds(pad, y, _testMain.Left - gap - pad, bh);
        _mainPage.Controls.AddRange([_pause, _testMain, _openSettings]);
        y += bh + gap;
        // [ทดสอบ EXP] full width; while a test runs it shows the time left and cancels.
        _expButton.SetBounds(pad, y, W - pad * 2, bh);
        _mainPage.Controls.Add(_expButton);
        y += bh + S(14);

        _lastAlert.SetBounds(pad, y, W - pad * 2, S(22));
        _mainPage.Controls.Add(_lastAlert);
        y += S(22) + pad;
        ClientSize = new Size(W, scroll ? y : Math.Max(y, S(600)));

        _pause.Click += (_, _) =>
        {
            _watcher.Paused = !_watcher.Paused;
            _log.Write(_watcher.Paused ? "Paused" : "Resumed");
            _diag.OnSetting("paused", _watcher.Paused);
            UpdateWatchState();
        };
        _testMain.Click += async (_, _) => await TestSendAsync();
        _expButton.Click += async (_, _) => await ExpButtonAsync();
        _openSettings.Click += (_, _) => ShowPage(settings: true);
    }

    int ButtonWidth(DarkButton b) => TextRenderer.MeasureText(b.Text, b.Font).Width + S(32);

    Panel MakeHeader(int height)
    {
        var header = new Panel { Dock = DockStyle.Top, Height = height, BackColor = Theme.Surface };
        header.Paint += (_, e) => { using var p = new Pen(Theme.Divider); e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1); };
        return header;
    }

    Card MakeTile(string caption, Label value, Label detail, int w, int h)
    {
        var tile = new Card { Size = new Size(w, h) };
        var cap = MakeLabel(caption, Theme.Small, Theme.Muted, Theme.Surface);
        cap.SetBounds(S(14), S(10), w - S(28), S(20));
        value.SetBounds(S(14), S(30), w - S(28), S(24));
        detail.SetBounds(S(14), S(54), w - S(28), S(20));
        tile.Controls.AddRange([cap, value, detail]);
        return tile;
    }

    // ---------------------------------------------------------------- settings page

    void BuildSettingsPage()
    {
        // The page scrolls vertically; leave room for the scroll bar so no horizontal bar appears.
        int W = S(460) - SystemInformation.VerticalScrollBarWidth, pad = S(18), cardW = W - pad * 2, inner = cardW - S(28);
        var header = MakeHeader(S(68));
        _back.SetBounds(S(18), S(12), S(80), S(44));
        _back.BackColor = Theme.Surface;
        var title = MakeLabel("ตั้งค่า Discord", Theme.BodyBold, Theme.Text, Theme.Surface);
        title.SetBounds(_back.Right + S(12), S(12), S(260), S(44));
        header.Controls.AddRange([_back, title]);

        var content = new Panel { BackColor = Theme.Background, Location = new Point(0, S(68)), Width = W };
        int y = pad;

        // Step 1
        var step1 = StepCard("1", "สร้างบอทของคุณ (ทำครั้งเดียว)", cardW, out int sy);
        var d1 = MakeLabel("ทำตามคู่มือทีละขั้น ใช้เวลาราว 5 นาที แล้วเชิญบอทเข้าห้องส่วนตัวของคุณ", Theme.Small, Theme.Muted, Theme.Surface, ContentAlignment.TopLeft);
        d1.AutoEllipsis = false;
        d1.SetBounds(S(14), sy, inner, S(40));
        sy += S(40) + S(6);
        _guide.SetBounds(S(14), sy, ButtonWidth(_guide), S(44));
        sy += S(44) + S(14);
        step1.Controls.AddRange([d1, _guide]);
        step1.SetBounds(pad, y, cardW, sy);
        y += sy + S(14);

        // Step 2
        var step2 = StepCard("2", "วางรหัสบอท", cardW, out sy);
        int connectW = ButtonWidth(_connect);
        _token.Box.UseSystemPasswordChar = true;
        _token.Box.AccessibleName = "รหัสบอท";
        _token.SetBounds(S(14), sy, inner - S(10) - connectW, S(44));
        _connect.SetBounds(_token.Right + S(10), sy, connectW, S(44));
        sy += S(44) + S(10);
        _botStatus.SetBounds(S(14), sy, inner, S(20));
        sy += S(20) + S(6);
        var note = MakeLabel("รหัสนี้เก็บไว้ในเครื่องคุณเท่านั้น อย่าส่งให้คนอื่น", Theme.Small, Theme.Muted, Theme.Surface);
        note.SetBounds(S(14), sy, inner, S(20));
        sy += S(20) + S(14);
        step2.Controls.AddRange([_token, _connect, _botStatus, note]);
        step2.SetBounds(pad, y, cardW, sy);
        y += sy + S(14);

        // Step 3
        var step3 = StepCard("3", "เลือกห้องที่จะให้ส่งแจ้งเตือน", cardW, out sy);
        _channels.SetBounds(S(14), sy, inner, S(44));
        _channels.Combo.AccessibleName = "ห้องที่จะให้ส่งแจ้งเตือน";
        _invite.SetBounds(S(14), sy, ButtonWidth(_invite), S(44));
        sy += S(44) + S(6);
        _channelNote.SetBounds(S(14), sy, inner, S(20));
        sy += S(20) + S(6);
        int testW = ButtonWidth(_testSettings);
        _testSettings.SetBounds(S(14), sy, testW, S(44));
        _testNote.SetBounds(_testSettings.Right + S(10), sy, inner - testW - S(10), S(44));
        sy += S(44) + S(14);
        step3.Controls.AddRange([_channels, _invite, _channelNote, _testSettings, _testNote]);
        step3.SetBounds(pad, y, cardW, sy);
        y += sy + S(14);

        var ssHint = MakeLabel("พิมพ์ /ss ในห้องนั้น โปรแกรมจะส่งภาพจอเกมตอนนั้นกลับมา", Theme.Small, Theme.Muted, Theme.Background);
        ssHint.SetBounds(pad, y, cardW, S(20));
        y += S(20) + S(14);

        // เก็บข้อมูลเพื่อพัฒนา: collection mode, alert evidence, folder sizes (all files stay on this PC)
        var collect = new Card();
        var ctitle = MakeLabel("เก็บข้อมูลเพื่อพัฒนา", Theme.Body, Theme.Text, Theme.Surface);
        ctitle.SetBounds(S(14), S(14), inner, S(22));
        int cy = S(14) + S(22) + S(10);
        var modeLabel = MakeLabel("เก็บภาพ HUD", Theme.Small, Theme.Muted, Theme.Surface);
        modeLabel.SetBounds(S(14), cy, inner, S(20));
        cy += S(20) + S(6);
        int mw = (inner - S(8) * 2) / 3;
        for (int k = 0; k < _modeButtons.Length; k++) _modeButtons[k].SetBounds(S(14) + k * (mw + S(8)), cy, mw, S(40));
        cy += S(40) + S(6);
        _modeHint.AutoEllipsis = false;
        _modeHint.SetBounds(S(14), cy, inner, S(40));
        cy += S(40) + S(6);
        _evidence.SetBounds(S(14), cy + S(13), S(18), S(18));
        _evidenceLabel.SetBounds(S(14) + S(18) + S(12), cy, inner - S(30), S(44));
        cy += S(44) + S(8);
        int ofW = ButtonWidth(_openFolder);
        foreach (var (label, button) in new[] { (_collectSize, _openFolder), (_evidenceSize, _openEvidence), (_journalSize, _openJournal) })
        {
            label.SetBounds(S(14), cy, inner - ofW - S(10), S(40));
            button.SetBounds(cardW - S(14) - ofW, cy, ofW, S(40));
            cy += S(40) + S(6);
        }
        cy += S(8);
        collect.Controls.AddRange([ctitle, modeLabel, .. _modeButtons, _modeHint, _evidence, _evidenceLabel,
            _collectSize, _openFolder, _evidenceSize, _openEvidence, _journalSize, _openJournal]);
        collect.SetBounds(pad, y, cardW, cy);
        y += cy + pad;

        content.Height = y;
        content.Controls.AddRange([step1, step2, step3, ssHint, collect]);
        _settingsPage.Controls.Add(content);
        _settingsPage.Controls.Add(header);

        const string tip = "ไฟล์ทั้งหมดอยู่ในเครื่องนี้เท่านั้น ไม่มีการอัปโหลด\nภาพเต็มจออาจมีแชทในเกมติดมาด้วย";
        _tip.SetToolTip(ctitle, tip);
        _tip.SetToolTip(modeLabel, tip);
        foreach (var b in _modeButtons) _tip.SetToolTip(b, tip);
        _tip.SetToolTip(_evidenceLabel, tip);
        _tip.SetToolTip(_collectSize, tip + "\n" + AppSettings.CollectRoot);
        _tip.SetToolTip(_evidenceSize, tip + "\n" + AppSettings.EvidenceRoot);
        _tip.SetToolTip(_journalSize, "บันทึกเหตุการณ์ (ไม่มีภาพ) อยู่ในเครื่องนี้เท่านั้น\n" + AppSettings.JournalRoot);
        _tip.SetToolTip(_openFolder, AppSettings.CollectRoot);
        _tip.SetToolTip(_openEvidence, AppSettings.EvidenceRoot);
        _tip.SetToolTip(_openJournal, AppSettings.JournalRoot);
        _evidenceLabel.Click += (_, _) => _evidence.Checked = !_evidence.Checked;
        for (int k = 0; k < _modeButtons.Length; k++)
        {
            var mode = (CollectMode)k;
            _modeButtons[k].Click += (_, _) => SetCollectMode(mode);
        }

        _back.Click += (_, _) => ShowPage(settings: false);
        _guide.Click += (_, _) => OpenGuide();
        _connect.Click += async (_, _) => await ConnectAsync();
        _token.Box.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ConnectAsync(); } };
        _invite.Click += async (_, _) => await OpenInviteAsync();
        _testSettings.Click += async (_, _) => await TestSendAsync();
        _channels.Combo.SelectedIndexChanged += (_, _) => OnChannelPicked();
        _openFolder.Click += (_, _) => OpenFolder(AppSettings.CollectRoot);
        _openEvidence.Click += (_, _) => OpenFolder(AppSettings.EvidenceRoot);
        _openJournal.Click += (_, _) => OpenFolder(AppSettings.JournalRoot);
    }

    Card StepCard(string number, string title, int width, out int contentTop)
    {
        var card = new Card { Width = width };
        var n = MakeLabel(number, Theme.BodyBold, Theme.Accent, Theme.Surface);
        n.AutoEllipsis = false;
        n.SetBounds(S(14), S(14), S(20), S(22));
        var t = MakeLabel(title, Theme.Body, Theme.Text, Theme.Surface);
        t.SetBounds(S(14) + S(20) + S(6), S(14), width - S(28) - S(26), S(22));
        card.Controls.AddRange([n, t]);
        contentTop = S(14) + S(22) + S(10);
        return card;
    }

    void ShowPage(bool settings)
    {
        _settingsPage.Visible = settings;
        _mainPage.Visible = !settings;
        if (settings) { RefreshChannels(); UpdateBotStatus(); UpdateCollectSize(); _settingsPage.AutoScrollPosition = Point.Empty; }
    }

    // ---------------------------------------------------------------- lifecycle

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar (Windows 10 2004+ / 11). Affects only this window.
        int on = 1;
        try { _ = DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _log.Write("Started");
        _collectMode = _settings.Collect;
        _diag.AppStarted(_noSend, CollectModes.Name(_collectMode), _settings.SaveAlertEvidence);
        UpdateModeButtons();
        if (_collectMode != CollectMode.Off) SetCollecting(_collectMode);
        _evidence.Checked = _settings.SaveAlertEvidence;
        _evidence.CheckedChanged += (_, _) =>
        {
            _settings.SaveAlertEvidence = _diag.SaveEvidence = _evidence.Checked;
            SaveSettings();
            _log.Write("Alert evidence: " + (_evidence.Checked ? "on" : "off"));
            _diag.OnSetting("evidence", _evidence.Checked);
        };
        RemeasureFolder();
        UpdateGameTile();
        UpdateDiscordTile();
        UpdateBotStatus();
        UpdateWatchState();
        RefreshChannels();
        UpdateTokenPlaceholder();
        _uiTimer.Start();
        _watcher.Start();
        // New version: on start and every 6 h (one-file build only; silent on any error).
        if (UpdateInstaller.CanSelfUpdate)
        {
            _updateButton.Click += async (_, _) => await UpdateClickAsync();
            _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
            _updateTimer.Start();
            _ = CheckForUpdateAsync();
        }

        if (_noSend) _log.Write("--no-send: Discord is not connected and nothing is sent");
        else if (_tokens.Load() is { } token)
        {
            _log.AddSecret(token);
            await Task.Run(() => _bot.ConnectAsync(token));
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // X exits the app (no tray).
        _uiTimer.Dispose();
        _updateTimer.Dispose();
        _exp.Dispose();   // a running test is dropped (journalled)
        _watcher.Dispose();
        _alerts.Dispose();
        try { Task.Run(async () => await _bot.DisposeAsync()).Wait(TimeSpan.FromSeconds(4)); } catch (AggregateException) { }
        _diag.AppStopped();
        _diag.Dispose();
        _log.Write("Exited");
        base.OnFormClosed(e);
    }

    // ---------------------------------------------------------------- update

    async Task CheckForUpdateAsync()
    {
        if (_updating) return;
        UpdateInfo? info;
        try { info = await UpdateChecker.CheckAsync(UpdateVersion.Current); }
        catch (Exception) { return; }   // silent: no network, GitHub down, odd response
        if (info is null || _updating || IsDisposed) return;
        if (_update?.Version != info.Version) _log.Write($"Update available: {info.Tag}");
        _update = info;
        ShowUpdateBanner($"มีรุ่นใหม่ {info.Display(UpdateVersion.Current)}");
    }

    /// <summary>Shows the banner under the header (the first time: everything below moves down and the window grows).</summary>
    void ShowUpdateBanner(string text)
    {
        _updateText.Text = text;
        if (_updateBanner.Visible) return;
        int W = S(460), pad = S(18), h = S(52), shift = h + S(12);
        foreach (Control c in _mainPage.Controls)
            if (c.Top >= S(45)) c.Top += shift;
        int bw = ButtonWidth(_updateButton);
        _updateBanner.SetBounds(pad, S(45) + pad, W - pad * 2, h);
        _updateButton.SetBounds(_updateBanner.Width - S(10) - bw, S(8), bw, h - S(16));
        _updateText.SetBounds(S(14), 0, _updateButton.Left - S(24), h);
        if (_updateBanner.Controls.Count == 0) _updateBanner.Controls.AddRange([_updateText, _updateButton]);
        _mainPage.Controls.Add(_updateBanner);
        _updateBanner.Visible = true;
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + shift);
    }

    async Task UpdateClickAsync()
    {
        if (_update is not { } info || _updating) return;
        _updating = true;
        _updateButton.Enabled = false;
        _updateText.Text = $"กำลังดาวน์โหลดรุ่น {info.Display(UpdateVersion.Current)}...";
        _log.Write($"Update: downloading {info.Tag}");
        try
        {
            string exe = await UpdateInstaller.DownloadAndVerifyAsync(info);
            _updateText.Text = "ตรวจลายเซ็นผ่าน กำลังติดตั้ง...";
            UpdateInstaller.InstallAndRestart(exe, _noSend ? ["--no-send"] : []);
            _log.Write($"Update: {info.Tag} installed, restarting");
            Close();
        }
        catch (Exception ex)
        {
            string why = ex is UpdateFailedException ? ex.Message : "เกิดข้อผิดพลาด";
            _log.Write("Update failed: " + why, ex.InnerException ?? ex);
            _updateText.Font = Theme.Small;
            _updateText.Text = $"อัปเดตไม่สำเร็จ: {why} · ใช้รุ่นเดิมต่อ";
            _updateButton.Text = "ลองใหม่";
            _updateButton.Enabled = true;
            _updating = false;
        }
    }

    // ---------------------------------------------------------------- watcher → UI / alerts

    /// <summary>Polling thread. The frame is reused by the next poll; AlertService copies now.</summary>
    void OnWatchEvent(WatchEvent e, Frame? frame, HudScale scale)
    {
        _log.Write($"Event: {e.Kind}");
        bool enabled = _settings.IsAlertEnabled(AlertIds.For(e));
        var queued = _alerts.OnEvent(e, frame, scale);
        string result = _watcher.Paused ? AlertResults.Paused
            : !enabled ? AlertResults.RowDisabled
            : queued is null ? AlertResults.QueueFull
            : _noSend ? AlertResults.SendingOff
            : AlertResults.Pending;
        _diag.OnAlert(e, frame, result, queued);
        if (queued is not null)
            OnUi(() => _lastAlert.Text = $"แจ้งเตือนล่าสุด: {e.Message} · {e.Time.ToString("HH:mm", Inv)} (กำลังส่ง...)");
        OnUi(UpdateRows);
    }

    async Task ExpButtonAsync()
    {
        if (_watcher.ExpTest.Phase != ExpPhase.Idle)
        {
            _lastAlert.Text = _exp.Cancel(ExpTestService.SourceButton);
            UpdateExpButton();
            return;
        }
        int minutes = _config.ExpTest.DefaultMinutes;
        var task = _exp.StartAsync(minutes, ExpTestService.SourceButton);
        UpdateExpButton();
        string text = await task;
        _lastAlert.Text = text;
        UpdateExpButton();
    }

    void UpdateExpButton()
    {
        var r = _watcher.ExpTest;
        string text = r.Phase switch
        {
            ExpPhase.Starting => "กำลังอ่านค่า EXP... (กดเพื่อยกเลิก)",
            ExpPhase.Running => $"ยกเลิกทดสอบ EXP · เหลือ {ExpTexts.Clock(r.TimeLeft(DateTime.Now) ?? TimeSpan.Zero)}",
            ExpPhase.Finishing => "กำลังสรุปผลทดสอบ EXP... (กดเพื่อยกเลิก)",
            _ => $"ทดสอบ EXP · {_config.ExpTest.DefaultMinutes} นาที",
        };
        if (_expButton.Text != text) { _expButton.Text = text; _expButton.Invalidate(); }
        var kind = r.Phase == ExpPhase.Idle ? ButtonKind.Secondary : ButtonKind.Primary;
        if (_expButton.Kind != kind) { _expButton.Kind = kind; _expButton.Invalidate(); }
    }

    void OnUiTick()
    {
        UpdateExpButton();
        UpdateRows();
        if (++_sizeTick % 2 == 0 && _settingsPage.Visible) UpdateCollectSize();
    }

    void UpdateWatchState()
    {
        bool paused = _watcher.Paused;
        _watchState.Text = paused ? "หยุดชั่วคราว" : "เฝ้าอยู่";
        _pause.Text = paused ? "เฝ้าต่อ" : "หยุดเฝ้าชั่วคราว";
        UpdateRows();
    }

    void UpdateRows()
    {
        var st = _watcher.Status.State;
        foreach (var r in _rows)
        {
            switch (r.Spec.Id)
            {
                case AlertIds.Death:
                    if (_watcher.DeathActive) r.SetNow("ตาย", Theme.Error);
                    else if (st == GameState.Found && !_watcher.Paused) r.SetNow("ปกติ");
                    else r.SetNow("—");
                    break;
                case AlertIds.GameDropped:
                    if (_watcher.DropActive) r.SetNow("หลุด", Theme.Error);
                    else if (st is GameState.Found or GameState.Minimized or GameState.Blank or GameState.CaptureFailed) r.SetNow("ปกติ");
                    else r.SetNow("—");
                    break;
                default:
                    // HUD rows: the watcher keeps the latest stable reading per row.
                    if (!r.Spec.Implemented) break;
                    var now = _watcher.Readings.Get(r.Spec.Id);
                    r.SetReached(now.Reached);
                    r.SetChooserVisible(now.TwoFound);
                    if (r.Spec.Id == AlertIds.Unreadable) r.SetNowList(now.Text, now.Text is "ปกติ" or "—" ? null : Theme.WarnValue);
                    else if (r.Spec.Id == AlertIds.InventoryFull) r.SetNow(now.Text, now.Reached ? Theme.WarnValue : null);
                    else if (r.Spec.Id == AlertIds.WarPillar) r.SetNowList(now.Text, separator: " · ");
                    else r.SetNow(now.Text);
                    string? subText = r.Spec.SubId is { } subId ? _watcher.Readings.Get(subId).Text : null;
                    if (subText is not null) r.SetSubNow(subText);
                    if (_noSend)
                    {
                        string line = now.Text + (now.Reached ? " (reached)" : "") + (now.TwoFound ? " (two)" : "");
                        if (!_loggedRows.TryGetValue(r.Spec.Id, out var last) || last != line)
                        {
                            _loggedRows[r.Spec.Id] = line;
                            _log.Write($"Row {r.Spec.Id}: {line}");
                        }
                        if (subText is not null && (!_loggedRows.TryGetValue(r.Spec.SubId!, out var lastSub) || lastSub != subText))
                        {
                            _loggedRows[r.Spec.SubId!] = subText;
                            _log.Write($"Row {r.Spec.SubId}: {subText}");
                        }
                    }
                    break;
            }
        }
    }

    void UpdateGameTile()
    {
        var s = _watcher.Status;
        string size = $"{s.Width}×{s.Height}";
        (string value, Color colour, string detail) = s.State switch
        {
            GameState.Found => ($"พบแล้ว · {size}", Theme.Accent,
                s.ScaleMeasured ? $"สเกล HUD {s.Scale.X.ToString("F2", Inv)}" : $"สเกล HUD {s.Scale.X.ToString("F2", Inv)} (ยังวัดไม่ได้)"),
            GameState.NotFound => ("ไม่พบเกม", Theme.WarnText, "เปิดเกมแล้วโปรแกรมจะหาเอง"),
            GameState.Minimized => ("พบแล้ว · ย่ออยู่", Theme.WarnText, "ย่อหน้าต่างอยู่ มองไม่เห็นจอเกม"),
            GameState.Blank => ("พบแล้ว · ภาพดำ", Theme.WarnText, "จับภาพได้แต่เป็นสีดำ"),
            GameState.CaptureFailed => ("พบแล้ว · จับภาพไม่ได้", Theme.Error, s.Detail ?? ""),
            GameState.Error => ("ผิดพลาด", Theme.Error, s.Detail ?? ""),
            _ => ("กำลังตรวจ...", Theme.Muted, ""),
        };
        _gameValue.Text = value;
        _gameValue.ForeColor = colour;
        _gameDetail.Text = detail;
        _tip.SetToolTip(_gameDetail, detail);
        UpdateRows();
    }

    void UpdateDiscordTile()
    {
        var b = _bot.Status;
        (string value, Color colour, string detail) = b.State switch
        {
            BotState.Connected => ("เชื่อมต่อแล้ว", Theme.Accent, _settings.DiscordChannelId is null ? "ยังไม่ได้เลือกห้อง" : _settings.DiscordChannelLabel ?? ""),
            BotState.Connecting => ("กำลังเชื่อมต่อ...", Theme.Muted, b.Reason ?? ""),
            BotState.Error => ("ผิดพลาด", Theme.Error, b.Reason ?? ""),
            _ => ("ยังไม่ได้ตั้งค่า", Theme.WarnText, "กด ตั้งค่า เพื่อเริ่ม"),
        };
        _discordValue.Text = value;
        _discordValue.ForeColor = colour;
        _discordDetail.Text = detail;
        _tip.SetToolTip(_discordDetail, detail);
    }

    void UpdateBotStatus()
    {
        var b = _bot.Status;
        (_botStatus.Text, _botStatus.ForeColor) = b.State switch
        {
            BotState.Connected => ($"เชื่อมต่อแล้ว · บอทชื่อ \"{b.BotName}\"", Theme.Accent),
            BotState.Connecting => ("กำลังเชื่อมต่อ..." + (b.Reason is null ? "" : $" ({b.Reason})"), Theme.Muted),
            BotState.Error => ("เชื่อมต่อไม่ได้: " + (b.Reason ?? "ไม่ทราบสาเหตุ"), Theme.Error),
            _ => (_tokens.Exists ? "ยังไม่ได้เชื่อมต่อ" : "ยังไม่ได้ใส่รหัสบอท", Theme.Muted),
        };
        _connect.Enabled = b.State != BotState.Connecting;
    }

    void UpdateTokenPlaceholder() =>
        _token.Box.PlaceholderText = _tokens.Exists ? "บันทึกรหัสไว้แล้ว (วางรหัสใหม่เพื่อเปลี่ยน)" : "วางรหัสบอทที่นี่";

    // ---------------------------------------------------------------- Discord actions

    async Task ConnectAsync()
    {
        if (_noSend) { _botStatus.Text = "โหมด --no-send: ไม่เชื่อมต่อ Discord"; _botStatus.ForeColor = Theme.WarnText; return; }
        string token = _token.Box.Text.Trim();
        if (token.Length == 0)
        {
            token = _tokens.Load() ?? "";
            if (token.Length == 0) { _botStatus.Text = "กรุณาวางรหัสบอทก่อน"; _botStatus.ForeColor = Theme.WarnText; return; }
        }
        else
        {
            _log.AddSecret(token);
            try { _tokens.Save(token); }
            catch (Exception ex) { _log.Write("Could not save token", ex); }
            _token.Box.Text = "";
            UpdateTokenPlaceholder();
        }
        _connect.Enabled = false;
        await Task.Run(() => _bot.ConnectAsync(token));
        UpdateBotStatus();
    }

    void RefreshChannels()
    {
        var list = _bot.PostableChannels();
        bool connected = _bot.Status.State == BotState.Connected;
        _suppressChannelEvent = true;
        try
        {
            var combo = _channels.Combo;
            combo.BeginUpdate();
            combo.Items.Clear();
            foreach (var c in list) combo.Items.Add(c);
            int idx = list.ToList().FindIndex(c => c.ChannelId == _settings.DiscordChannelId);
            if (idx < 0 && !connected && _settings.DiscordChannelLabel is { } saved)
            {
                combo.Items.Add(saved);
                idx = 0;
            }
            combo.SelectedIndex = idx;
            combo.EndUpdate();
            // Kept enabled: a disabled ComboBox ignores the dark colours. Picking a saved label is a no-op.
        }
        finally { _suppressChannelEvent = false; }

        bool needInvite = connected && list.Count == 0;
        _invite.Visible = needInvite;
        _channels.Visible = !needInvite;
        _channelNote.Text = !connected ? "เชื่อมต่อบอทก่อน แล้วเลือกห้องที่นี่"
            : _bot.GuildCount == 0 ? "บอทยังไม่ได้อยู่ในเซิร์ฟเวอร์ไหน กดปุ่มเพื่อเชิญบอท"
            : list.Count == 0 ? "บอทยังส่งข้อความในห้องไหนไม่ได้ กดปุ่มเพื่อเชิญใหม่พร้อมสิทธิ์"
            : _settings.DiscordChannelId is null || _channels.Combo.SelectedIndex < 0 ? "เลือกห้องจากรายการ"
            : "บอทจะส่งแจ้งเตือนเข้าห้องนี้";
        UpdateDiscordTile();
    }

    void OnChannelPicked()
    {
        if (_suppressChannelEvent || _channels.Combo.SelectedItem is not ChannelChoice c) return;
        _settings.DiscordGuildId = c.GuildId;
        _settings.DiscordChannelId = c.ChannelId;
        _settings.DiscordChannelLabel = c.Label;
        SaveSettings();
        _bot.ChosenGuildId = c.GuildId;
        _bot.ChosenChannelId = c.ChannelId;
        _log.Write($"Channel chosen: {c.GuildId}/{c.ChannelId}");
        _diag.OnSetting("discord.channel", $"{c.GuildId}/{c.ChannelId}");
        _ = Task.Run(_bot.EnsureSlashCommandAsync);
        _channelNote.Text = "บอทจะส่งแจ้งเตือนเข้าห้องนี้";
        UpdateDiscordTile();
    }

    async Task OpenInviteAsync()
    {
        var url = await Task.Run(_bot.GetInviteUrlAsync);
        if (url is null) { _channelNote.Text = "สร้างลิงก์เชิญไม่ได้ ลองเชื่อมต่อใหม่"; return; }
        OpenUrl(url);
    }

    /// <summary>The "ทดสอบส่ง" Discord message.</summary>
    public const string TestSendText = "ทดสอบสัญญาณ ศูนย์บัญชาการได้ยินชัดเจน";

    async Task TestSendAsync()
    {
        string? problem = _bot.Status.State != BotState.Connected ? "ยังไม่ได้เชื่อมต่อ Discord"
            : _settings.DiscordChannelId is null ? "ยังไม่ได้เลือกห้อง" : null;
        if (problem is not null) { ShowTestResult(problem, ok: false); return; }

        _testMain.Enabled = _testSettings.Enabled = false;
        ShowTestResult("กำลังส่ง...", ok: true);
        var msg = new AlertMessage("test", TestSendText, null, null, DateTime.Now);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Task.Run(() => _bot.SendAsync(msg, cts.Token));
            ShowTestResult($"ส่งแล้ว · {DateTime.Now.ToString("HH:mm", Inv)}", ok: true);
            _log.Write("Test send OK");
        }
        catch (Exception ex)
        {
            _log.Write("Test send failed", ex);
            ShowTestResult("ส่งไม่สำเร็จ: " + ShortReason(ex), ok: false);
        }
        finally { _testMain.Enabled = _testSettings.Enabled = true; }
    }

    static string ShortReason(Exception ex) => ex switch
    {
        HttpException { DiscordCode: Discord.DiscordErrorCode.MissingPermissions } => "บอทไม่มีสิทธิ์ส่งในห้องนี้",
        HttpException { DiscordCode: Discord.DiscordErrorCode.UnknownChannel } => "ไม่พบห้องนี้แล้ว",
        HttpException { HttpCode: System.Net.HttpStatusCode.Forbidden } => "บอทไม่มีสิทธิ์ส่งในห้องนี้",
        OperationCanceledException or TimeoutException => "หมดเวลา",
        InvalidOperationException => "ยังไม่ได้เชื่อมต่อหรือไม่พบห้อง",
        _ => "เกิดข้อผิดพลาด",
    };

    void ShowTestResult(string text, bool ok)
    {
        _testNote.Text = text;
        _testNote.ForeColor = ok ? Theme.Muted : Theme.Error;
        _lastAlert.Text = "ทดสอบส่ง: " + text;
    }

    void OpenGuide()
    {
        string? path = AppFiles.GuidePath(_log);
        if (path is null) { _botStatus.Text = "ไม่พบไฟล์คู่มือ"; return; }
        OpenUrl(path);
    }

    void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { _log.Write("Could not open " + target, ex); }
    }

    // ---------------------------------------------------------------- collect data / diagnostics

    /// <summary>"ตอนนี้" of any row, for the journal (polling or Discord thread).</summary>
    string RowText(string id) => id switch
    {
        AlertIds.Death => _watcher.DeathActive ? "dead" : _watcher.Status.State == GameState.Found ? "ok" : "—",
        AlertIds.GameDropped => _watcher.DropActive ? "dropped" : _watcher.GameEverSeen ? "ok" : "—",
        _ => _watcher.Readings.Get(id).Text,
    };

    /// <summary>Alert threshold of a row for the journal (null for death / game dropped).</summary>
    int? RowThreshold(string id) => id switch
    {
        AlertIds.Death or AlertIds.GameDropped => null,
        AlertIds.GunAmmoEmpty or AlertIds.MissileEmpty => 0,
        AlertIds.Kill300 => _config.Kill.Target,
        _ => _watcher.ThresholdOf(id),
    };

    void SetCollectMode(CollectMode mode)
    {
        if (mode == _collectMode) return;
        _collectMode = mode;
        _settings.Collect = mode;
        SaveSettings();
        UpdateModeButtons();
        SetCollecting(mode);
        _log.Write("Collect mode: " + CollectModes.Name(mode));
        _diag.OnSetting("collect_mode", CollectModes.Name(mode));
    }

    void UpdateModeButtons()
    {
        for (int k = 0; k < _modeButtons.Length; k++)
        {
            _modeButtons[k].Kind = k == (int)_collectMode ? ButtonKind.Primary : ButtonKind.Secondary;
            _modeButtons[k].Invalidate();
        }
        _modeHint.Text = _collectMode switch
        {
            CollectMode.Auto => "เก็บเฉพาะช่วงที่น่าสนใจ เช่น น้ำมันต่ำ ของใกล้หมด มีการเตือน หรืออ่านค่าไม่ได้",
            CollectMode.Always => "เก็บภาพ HUD ตลอดเวลาที่เฝ้า (ไฟล์เยอะที่สุด)",
            _ => "ไม่เก็บภาพ HUD",
        };
    }

    void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _log.Write("Could not open " + path, ex); }
    }

    void SetCollecting(CollectMode mode)
    {
        DataCollector? next = null;
        if (mode != CollectMode.Off)
        {
            var c = _config.Collect;
            var store = new CollectStore(AppSettings.CollectRoot, (long)(c.MaxMegabytes * 1024 * 1024), c.QueueLimit);
            store.Start();
            if (mode == CollectMode.Auto)
            {
                var rules = new AutoCollectRules(_config, c.AllRegions.Select(r => r.Name).ToArray());
                next = new DataCollector(c, store, rules, _watcher.Ring, _config.Diagnostics.Auto.PreSeconds,
                    _config.Diagnostics.Auto.UnreadableFullFrameRepeatSeconds);
                next.AutoChanged += _diag.OnAutoCollect;
            }
            else next = new DataCollector(c, store);
        }
        var old = _watcher.SwapCollector(next);
        if (old is not null)
        {
            // Let the writer finish its queue off the UI thread, then re-measure the folder.
            Task.Run(() => { old.Dispose(); RemeasureFolder(); });
        }
        UpdateCollectSize();
    }

    void RemeasureFolder() => Task.Run(() =>
    {
        try { Interlocked.Exchange(ref _measuredBytes, CollectStore.MeasureFolder(AppSettings.CollectRoot)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        OnUi(UpdateCollectSize);
    });

    void UpdateCollectSize()
    {
        var active = _watcher.CurrentCollector;
        long bytes = active is not null ? active.Store.TotalBytes : Interlocked.Read(ref _measuredBytes);
        string size = bytes < 0 ? "-" : $"{bytes / (1024.0 * 1024.0):F1} MB";
        _collectSize.Text = $"ภาพ HUD: {size} / {_config.Collect.MaxMegabytes:F0} MB";
        var d = _config.Diagnostics;
        _evidenceSize.Text = $"ภาพตอนเตือน: {Mb(_diag.Evidence.TotalBytes)} / {d.EvidenceMaxMegabytes:F0} MB";
        _journalSize.Text = $"บันทึกเหตุการณ์: {Mb(_diag.Journal.TotalBytes)} / {d.JournalMaxMegabytes:F0} MB";
    }

    // ---------------------------------------------------------------- helpers

    static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";

    void SaveSettings()
    {
        try { _settings.Save(AppSettings.DefaultPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Write("Could not save settings", ex); }
    }

    void OnUi(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { /* closing */ }
    }
}
