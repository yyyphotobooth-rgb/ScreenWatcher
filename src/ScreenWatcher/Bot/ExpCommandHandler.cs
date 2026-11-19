using System.Globalization;
using ScreenWatcher.ExpTest;

namespace ScreenWatcher.Bot;

/// <summary>
/// /exp: "ทดสอบ EXP". One optional text option: minutes (MinMinutes..MaxMinutes, default
/// DefaultMinutes) to start, "stop" to end the running test now with a result from the minutes
/// measured, "cancel" to cancel (no result), "list" for the list image. Same channel rule as
/// /ss: in any other channel it answers only the person who typed it (ephemeral) and does nothing.
/// </summary>
public sealed class ExpCommandHandler(IExpTests exp, Func<ulong?> chosenChannelId, int defaultMinutes, int minMinutes, int maxMinutes, Action<string>? onResult = null)
{
    public const string CommandName = "exp";
    public const string CommandDescription = "ทดสอบ EXP ตามเวลา (เทียบแมป)";
    public const string ValueOption = "value";
    public const string ValueDescription = "นาที (ค่าเริ่ม 10) หรือ stop = จบเลยและสรุปผล, cancel = ยกเลิก, list = ดูผล";

    public const string WrongChannelText = "ใช้ /exp ได้เฉพาะในห้องที่ตั้งไว้ในโปรแกรม ScreenWatcher";
    public string UsageText => $"ใช้ /exp แล้วใส่จำนวนนาที {minMinutes}-{maxMinutes}, cancel หรือ list";

    /// <summary>Returns true when the interaction was a /exp command (handled), false otherwise.</summary>
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

        string arg = (i.GetString(ValueOption) ?? "").Trim().ToLowerInvariant();
        if (arg is "cancel" or "ยกเลิก")
        {
            await i.DeferAsync();
            string text = exp.Cancel(ExpTestService.SourceDiscord);
            Report(text == ExpTexts.Cancelled ? "cancel" : "cancel_none");
            await i.FollowupAsync(text);
            return true;
        }
        if (arg is "stop" or "หยุด")
        {
            await i.DeferAsync();
            string text = exp.Stop(ExpTestService.SourceDiscord);
            Report(text == ExpTexts.StopTooShort ? "stop_short" : text == ExpTexts.NothingToStop ? "stop_none"
                : text == ExpTexts.StopAlreadyEnding ? "stop_ending" : "stop");
            await i.FollowupAsync(text);
            return true;
        }
        if (arg is "list" or "รายการ")
        {
            await i.DeferAsync();
            var (png, text) = exp.List();
            Report(png is null ? "list_empty" : "list");
            if (png is null) await i.FollowupAsync(text);
            else await i.FollowupWithFileAsync(png, $"exp_list_{DateTime.Now:yyyyMMdd_HHmmss}.png", text);
            return true;
        }
        int minutes = defaultMinutes;
        if (arg.Length > 0 && (!int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes < minMinutes || minutes > maxMinutes))
        {
            Report("bad_value");
            await i.RespondEphemeralAsync(UsageText);
            return true;
        }
        await i.DeferAsync();
        string reply = await exp.StartAsync(minutes, ExpTestService.SourceDiscord);
        Report(reply == ExpTexts.Started(minutes) ? "start" : "start_not");
        await i.FollowupAsync(reply);
        return true;
    }

    void Report(string outcome)
    {
        try { onResult?.Invoke(outcome); } catch { }
    }
}
