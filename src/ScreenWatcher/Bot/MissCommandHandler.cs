namespace ScreenWatcher.Bot;

/// <summary>What /miss recorded: whether a game frame (and ring crops) were saved.</summary>
public sealed record MissResult(bool FrameSaved, int CropsSaved);

/// <summary>
/// /miss [note]: "an alert should have fired and did not". Same channel rule as /ss. Records the
/// last seconds of every HUD region, the current full frame and a journal record (with the note and
/// every row's reading) on this PC, then replies in Thai that it was recorded. Without a game window
/// only the journal record is written.
/// </summary>
public sealed class MissCommandHandler(Func<string?, MissResult> record, Func<ulong?> chosenChannelId, Action<string>? onResult = null)
{
    public const string CommandName = "miss";
    public const string CommandDescription = "แจ้งว่าควรมีแจ้งเตือนแต่ไม่มี (บันทึกภาพย้อนหลังไว้ในเครื่อง)";
    public const string NoteOption = "note";
    public const string NoteDescription = "เกิดอะไรขึ้น (ไม่ใส่ก็ได้)";

    public const string WrongChannelText = "ใช้ /miss ได้เฉพาะในห้องที่ตั้งไว้ในโปรแกรม ScreenWatcher";
    public const string RecordedText = "บันทึกแล้ว ✅ เก็บภาพจอเกมและภาพ HUD ย้อนหลังไว้ในเครื่องแล้ว";
    public const string RecordedNoGameText = "บันทึกแล้ว ✅ (ไม่พบหน้าต่างเกม จึงบันทึกเป็นข้อความอย่างเดียว)";
    public const string FailedText = "บันทึกไม่สำเร็จ";

    /// <summary>Returns true when the interaction was a /miss command (handled), false otherwise.</summary>
    public async Task<bool> HandleAsync(ISlashInteraction i)
    {
        if (!string.Equals(i.CommandName, CommandName, StringComparison.Ordinal)) return false;

        var chosen = chosenChannelId();
        if (chosen is null || i.ChannelId != chosen)
        {
            Report("wrong_channel");
            await i.RespondEphemeralAsync(WrongChannelText);
            return true;
        }

        await i.DeferAsync();
        string? note = i.GetString(NoteOption);
        MissResult? r;
        try { r = await Task.Run(() => record(note)); }
        catch (Exception) { r = null; }

        Report(r is null ? "failed" : r.FrameSaved ? "recorded" : "recorded_no_game");
        await i.FollowupAsync(r is null ? FailedText : r.FrameSaved ? RecordedText : RecordedNoGameText);
        return true;
    }

    void Report(string outcome)
    {
        try { onResult?.Invoke(outcome); } catch { }
    }
}
