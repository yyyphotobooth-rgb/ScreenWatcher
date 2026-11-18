namespace ScreenWatcher.Bot;

/// <summary>The bits of a Discord slash-command interaction the app uses (a fake in tests).</summary>
public interface ISlashInteraction
{
    string CommandName { get; }
    ulong? ChannelId { get; }
    /// <summary>Reply visible only to the person who typed the command.</summary>
    Task RespondEphemeralAsync(string text);
    /// <summary>"Bot is thinking..." (must happen within 3 s of the command).</summary>
    Task DeferAsync();
    Task FollowupAsync(string text);
    Task FollowupWithFileAsync(byte[] png, string fileName, string text);
}

/// <summary>
/// /ss: replies in the chosen channel with the game's current full client frame, or
/// "ไม่พบหน้าต่างเกม" when the game is not found. In any other channel it answers only the person
/// who typed it (ephemeral) and posts nothing.
/// </summary>
public sealed class SsCommandHandler(Func<SnapshotResult> snapshot, Func<ulong?> chosenChannelId)
{
    public const string CommandName = "ss";
    public const string CommandDescription = "ส่งภาพจอเกมตอนนี้";

    public const string NotFoundText = "ไม่พบหน้าต่างเกม";
    public const string MinimizedText = "หน้าต่างเกมถูกย่ออยู่ จับภาพไม่ได้";
    public const string FailedText = "จับภาพจอเกมไม่สำเร็จ";
    public const string WrongChannelText = "ใช้ /ss ได้เฉพาะในห้องที่ตั้งไว้ในโปรแกรม ScreenWatcher";
    public const string ImageText = "ภาพจอเกมตอนนี้";

    /// <summary>Returns true when the interaction was a /ss command (handled), false otherwise.</summary>
    public async Task<bool> HandleAsync(ISlashInteraction i)
    {
        if (!string.Equals(i.CommandName, CommandName, StringComparison.Ordinal)) return false;

        var chosen = chosenChannelId();
        if (chosen is null || i.ChannelId != chosen)
        {
            await i.RespondEphemeralAsync(WrongChannelText);
            return true;
        }

        await i.DeferAsync();
        SnapshotResult r;
        try { r = await Task.Run(snapshot); }
        catch (Exception) { r = new SnapshotResult(GameState.Error, null); }

        if (r.Png is { Length: > 0 } png)
            await i.FollowupWithFileAsync(png, $"ss_{DateTime.Now:yyyyMMdd_HHmmss}.png", ImageText);
        else
            await i.FollowupAsync(r.State switch
            {
                GameState.NotFound => NotFoundText,
                GameState.Minimized => MinimizedText,
                _ => FailedText,
            });
        return true;
    }
}
