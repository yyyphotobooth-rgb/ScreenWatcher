using ScreenWatcher.Config;
using ScreenWatcher.UI;

namespace ScreenWatcher;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var config = WatcherConfig.Parse(AppFiles.ReadText(WatcherConfig.FileName));
        // --no-send: watch and show everything, but never connect to Discord and never send
        // (alerts and row readings go to the log instead). For checking the readers live.
        bool noSend = args.Contains("--no-send", StringComparer.OrdinalIgnoreCase);
        Application.Run(new MainForm(config, noSend));
    }
}
