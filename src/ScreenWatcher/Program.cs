using ScreenWatcher.Config;
using ScreenWatcher.UI;

namespace ScreenWatcher;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var config = WatcherConfig.Load(Path.Combine(AppContext.BaseDirectory, WatcherConfig.FileName));
        Application.Run(new MainForm(config));
    }
}
