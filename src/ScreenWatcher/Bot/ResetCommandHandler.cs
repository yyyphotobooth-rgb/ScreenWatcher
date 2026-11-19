namespace ScreenWatcher.Bot;

/// <summary>
/// /reset: un-latches the "ช่องเก็บของเต็ม" row (after the owner emptied the inventory), like the
/// row's "รีเซ็ต" button. Same channel rule as /ss: in any other channel it answers only the
/// person who typed it (ephemeral) and changes nothing. <paramref name="reset"/> returns whether
/// the row was latched.
/// </summary>
public sealed class ResetCommandHandler(Func<bool> reset, Func<ulong?> chosenChannelId, Action<string>? onResult = null)
{
    public const string CommandName = "reset";
    public const string CommandDescription = "รีเซ็ตแจ้งเตือนช่องเก็บของเต็ม (หลังเคลียร์ของแล้ว)";

    public const string WrongChannelText = "ใช้ /reset ได้เฉพาะในห้องที่ตั้งไว้ในโปรแกรม ScreenWatcher";
    public const string ResetText = "รีเซ็ตแล้ว ✅ จะแจ้งเตือน \"ช่องเก็บของเต็ม\" อีกครั้งเมื่อเต็ม";
    public const string NotLatchedText = "รีเซ็ตแล้ว ✅ (ตอนนี้ช่องเก็บของยังไม่ได้แจ้งว่าเต็ม)";
    public const string FailedText = "รีเซ็ตไม่สำเร็จ";

    /// <summary>Returns true when the interaction was a /reset command (handled), false otherwise.</summary>
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
        bool? was;
        try { was = reset(); }
        catch (Exception) { was = null; }
        Report(was is null ? "failed" : was.Value ? "reset" : "not_latched");
        await i.FollowupAsync(was is null ? FailedText : was.Value ? ResetText : NotLatchedText);
        return true;
    }

    void Report(string outcome)
    {
        try { onResult?.Invoke(outcome); } catch { }
    }
}
