using System.Diagnostics;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Detection;

namespace ScreenWatcher.UI;

/// <summary>Status line, "Collect data" row, timestamped event log, Pause/Resume. Close hides to
/// tray; tray menu has Exit.</summary>
public sealed class MainForm : Form
{
    const int MaxLogLines = 500;

    readonly Watcher _watcher;
    readonly Label _status = new() { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), Text = "Game: checking..." };
    readonly ListBox _log = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    readonly Button _pause = new() { Dock = DockStyle.Bottom, Height = 32, Text = "Pause" };
    readonly NotifyIcon _tray = new() { Text = "ScreenWatcher", Icon = SystemIcons.Application, Visible = true };
    readonly FlowLayoutPanel _collectRow = new() { Dock = DockStyle.Top, Height = 30, WrapContents = false, Padding = new Padding(3, 2, 0, 0) };
    readonly CheckBox _collect = new() { Text = "Collect data", AutoSize = true, Margin = new Padding(3, 5, 3, 0) };
    readonly Label _collectSize = new() { AutoSize = true, Margin = new Padding(6, 7, 3, 0), Text = "-" };
    readonly Button _openFolder = new() { Text = "Open folder", AutoSize = true, Height = 24 };
    readonly ToolTip _tip = new() { AutoPopDelay = 15000 };
    readonly System.Windows.Forms.Timer _sizeTimer = new() { Interval = 2000 };
    readonly WatcherConfig _config;
    readonly AppSettings _settings;
    long _measuredBytes = -1;
    bool _exiting;

    public MainForm(WatcherConfig config)
    {
        _config = config;
        _settings = AppSettings.Load(AppSettings.DefaultPath);
        Text = "ScreenWatcher";
        ClientSize = new Size(380, 300);
        MinimumSize = new Size(300, 200);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
        // Thai-capable UI font (Windows 10/11 ship Leelawadee UI); GDI falls back if missing.
        Font = new Font("Leelawadee UI", 9f);

        _collectRow.Controls.AddRange([_collect, _collectSize, _openFolder]);
        string tip = "Saves small PNG crops of HUD areas (and some full game frames) to\n"
            + AppSettings.CollectRoot + "\n"
            + "Saved images stay on this PC; nothing is uploaded.\n"
            + "Full frames may include in-game chat.";
        _tip.SetToolTip(_collect, tip);
        _tip.SetToolTip(_collectSize, tip);
        _tip.SetToolTip(_openFolder, AppSettings.CollectRoot);

        Controls.Add(_log);
        Controls.Add(_collectRow);
        Controls.Add(_status);
        Controls.Add(_pause);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();

        _pause.Click += (_, _) =>
        {
            _watcher!.Paused = !_watcher.Paused;
            _pause.Text = _watcher.Paused ? "Resume" : "Pause";
            Log(_watcher.Paused ? "Paused" : "Resumed");
        };

        _openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppSettings.CollectRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.CollectRoot}\"") { UseShellExecute = true });
        };
        _sizeTimer.Tick += (_, _) => { if (Visible) UpdateCollectSize(); };

        _watcher = new Watcher(config);
        _watcher.StatusChanged += s => OnUi(() => _status.Text = s);
        _watcher.EventRaised += e => OnUi(() => Log(e.Message, e.Time));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Log("Started");
        _collect.Checked = _settings.CollectData;
        if (_collect.Checked) SetCollecting(true);
        _collect.CheckedChanged += (_, _) =>
        {
            _settings.CollectData = _collect.Checked;
            try { _settings.Save(AppSettings.DefaultPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Could not save settings: " + ex.Message); }
            SetCollecting(_collect.Checked);
            Log(_collect.Checked ? "Collect data: on" : "Collect data: off");
        };
        RemeasureFolder();
        _sizeTimer.Start();
        _watcher.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _sizeTimer.Dispose();
        _watcher.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    void SetCollecting(bool on)
    {
        DataCollector? next = null;
        if (on)
        {
            var c = _config.Collect;
            var store = new CollectStore(AppSettings.CollectRoot, (long)(c.MaxMegabytes * 1024 * 1024), c.QueueLimit);
            store.Start();
            next = new DataCollector(c, store);
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
        _collectSize.Text = $"{size} / {_config.Collect.MaxMegabytes:F0} MB";
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    void Log(string message, DateTime? time = null)
    {
        _log.Items.Add($"[{time ?? DateTime.Now:HH:mm:ss}] {message}");
        while (_log.Items.Count > MaxLogLines) _log.Items.RemoveAt(0);
        _log.TopIndex = Math.Max(0, _log.Items.Count - 1);
    }

    void OnUi(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { /* closing */ }
    }
}
