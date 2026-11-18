using ScreenWatcher.Config;
using ScreenWatcher.Detection;

namespace ScreenWatcher.UI;

/// <summary>Status line, timestamped event log, Pause/Resume. Close hides to tray; tray menu has Exit.</summary>
public sealed class MainForm : Form
{
    const int MaxLogLines = 500;

    readonly Watcher _watcher;
    readonly Label _status = new() { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), Text = "Game: checking..." };
    readonly ListBox _log = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    readonly Button _pause = new() { Dock = DockStyle.Bottom, Height = 32, Text = "Pause" };
    readonly NotifyIcon _tray = new() { Text = "ScreenWatcher", Icon = SystemIcons.Application, Visible = true };
    bool _exiting;

    public MainForm(WatcherConfig config)
    {
        Text = "ScreenWatcher";
        ClientSize = new Size(380, 300);
        MinimumSize = new Size(300, 200);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
        // Thai-capable UI font (Windows 10/11 ship Leelawadee UI); GDI falls back if missing.
        Font = new Font("Leelawadee UI", 9f);

        Controls.Add(_log);
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

        _watcher = new Watcher(config);
        _watcher.StatusChanged += s => OnUi(() => _status.Text = s);
        _watcher.EventRaised += e => OnUi(() => Log(e.Message, e.Time));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Log("Started");
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
        _watcher.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
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
