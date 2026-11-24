using ScreenWatcher.Config;
using ScreenWatcher.Diagnostics;
using ScreenWatcher.UI;

namespace ScreenWatcher;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // An unhandled error must never make the app vanish silently: journal "app_crash" + log.txt
        // + a Thai message box, then exit (start-up errors here, later UI-thread / background ones
        // through the handlers below).
        bool started = false;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => { CrashReport.Report(started ? "runtime" : "startup", e.Exception); Environment.Exit(1); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CrashReport.Report(started ? "runtime" : "startup", ex);
        };
        try
        {
            // After an update: wait for the previous process to exit, delete its .old exe.
            args = Update.UpdateInstaller.OnStartup(args);
            ApplicationConfiguration.Initialize();
            var config = WatcherConfig.Parse(AppFiles.ReadText(WatcherConfig.FileName));
            // --no-send: watch and show everything, but never connect to Discord and never send
            // (alerts and row readings go to the log instead). For checking the readers live.
            bool noSend = args.Contains("--no-send", StringComparer.OrdinalIgnoreCase);
            var form = new MainForm(config, noSend);
            form.Shown += (_, _) => started = true;
            Application.Run(form);
        }
        catch (Exception ex)
        {
            CrashReport.Report(started ? "runtime" : "startup", ex);
            Environment.Exit(1);   // background threads started by the form must not keep a hidden process alive
        }
    }
}
