using ScreenWatcher.Config;
using ScreenWatcher.Logging;

namespace ScreenWatcher.Diagnostics;

/// <summary>
/// Last-resort handling of an unhandled error (start-up or later): one "app_crash" journal record,
/// a log.txt line and a Thai message box, so the app never just vanishes without a trace.
/// Never throws.
/// </summary>
public static class CrashReport
{
    /// <summary>Default journal cap (detectors.json may not have been read yet).</summary>
    const long JournalCapBytes = 20L * 1024 * 1024;

    /// <summary>Writes the "app_crash" record to the journal under <paramref name="journalRoot"/> and a
    /// line to the log at <paramref name="logPath"/>.</summary>
    public static void Record(string journalRoot, string logPath, string stage, Exception ex)
    {
        try
        {
            var log = new AppLog(logPath);
            new Journal(journalRoot, JournalCapBytes, log.Scrub).Write("app_crash",
                ("build", DiagnosticsHub.BuildId), ("stage", stage), ("error", ex.GetType().FullName),
                ("message", ex.Message), ("stack", ex.ToString()));
            log.Write($"CRASH ({stage}): {ex}");
        }
        catch (Exception) { }
    }

    /// <summary>Thai message for the box (public for tests).</summary>
    public static string ThaiMessage(string stage, Exception ex) =>
        (stage == "startup" ? "ScreenWatcher เปิดไม่ได้ เพราะเกิดข้อผิดพลาด" : "ScreenWatcher เกิดข้อผิดพลาดและต้องปิดตัว") + "\n\n" +
        $"{ex.GetType().Name}: {ex.Message}\n\n" +
        $"บันทึกรายละเอียดไว้แล้วที่\n{AppSettings.JournalRoot}\nกรุณาส่งไฟล์วันนี้ให้ผู้พัฒนา";

    /// <summary>Records the error and shows the Thai message box.</summary>
    public static void Report(string stage, Exception ex)
    {
        Record(AppSettings.JournalRoot, AppLog.DefaultPath, stage, ex);
        try
        {
            MessageBox.Show(ThaiMessage(stage, ex), "ScreenWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception) { }
    }
}
