using System.Globalization;
using System.Runtime.InteropServices;
using ScreenWatcher.Alerts;
using ScreenWatcher.Diagnostics;

namespace ScreenWatcher.ExpTest;

/// <summary>What the Discord command (and the window's button) can ask of the EXP test.</summary>
public interface IExpTests
{
    /// <summary>Starts a test; the returned text is the reply (started / refused / busy).</summary>
    Task<string> StartAsync(int minutes, string source);
    /// <summary>Cancels the test; the returned text is the Thai confirmation.</summary>
    string Cancel(string source);
    /// <summary>Ends the running test now and reports the minutes measured (the result follows in
    /// the channel); the returned text is the Thai reply.</summary>
    string Stop(string source);
    /// <summary>The list image (null when there is nothing to list) and its text.</summary>
    (byte[]? Png, string Text) List();
}

/// <summary>
/// Glue around <see cref="ExpTestRunner"/>: journal records (start, refused, cancel, result,
/// dropped), result image + files, and the channel messages. A test started from Discord gets its
/// start reply there (the command's own reply); one started from the window's button sends the
/// start / refused / cancelled text to the channel. Results always go to the channel.
/// </summary>
public sealed class ExpTestService : IExpTests, IDisposable
{
    public const string AlertId = "exp_test";
    public const string SourceButton = "button", SourceDiscord = "discord";

    readonly ExpTestRunner _runner;
    readonly ExpTestStore _store;
    readonly Func<AlertMessage, bool> _send;
    readonly Journal? _journal;
    readonly Func<DateTime> _clock;
    readonly int _listCount;

    /// <summary>Raised after a result was saved and queued (any thread).</summary>
    public event Action<ExpResult, string?>? ResultReady;

    public ExpTestService(ExpTestRunner runner, ExpTestStore store, Func<AlertMessage, bool> send, Journal? journal, int listCount, Func<DateTime>? clock = null)
    {
        _runner = runner;
        _store = store;
        _send = send;
        _journal = journal;
        _listCount = listCount;
        _clock = clock ?? (() => DateTime.Now);
        _runner.StartDecided += OnStartDecided;
        _runner.Finished += OnFinished;
    }

    public ExpTestRunner Runner => _runner;

    /// <summary>Set when results are composed and saved synchronously (tests); otherwise on the thread pool.</summary>
    public bool Synchronous { get; init; }

    public async Task<string> StartAsync(int minutes, string source)
    {
        var outcome = await _runner.RequestStart(minutes, source, _clock());
        if (outcome.Status is ExpStartStatus.Busy or ExpStartStatus.AlreadyStarting)
            _journal?.Write("exp_test", ("event", "start_busy"), ("source", source));
        return outcome.Text;
    }

    void OnStartDecided(ExpStartOutcome o, string source)
    {
        if (o.Status == ExpStartStatus.Started && o.Start is { } s)
            _journal?.Write("exp_test", ("event", "start"), ("source", source), ("minutes", _runner.Minutes), ("level", s.Level), ("pct", s.PctHundredths / 100.0));
        else
            _journal?.Write("exp_test", ("event", "refused"), ("source", source));
        if (source != SourceDiscord) Send(o.Text, null);
    }

    public string Cancel(string source)
    {
        bool was = _runner.Cancel();
        _journal?.Write("exp_test", ("event", was ? "cancel" : "cancel_none"), ("source", source));
        if (was && source != SourceDiscord) Send(ExpTexts.Cancelled, null);
        return was ? ExpTexts.Cancelled : ExpTexts.NothingToCancel;
    }

    public string Stop(string source)
    {
        var (status, minutes, planned) = _runner.Stop(_clock());
        _journal?.Write("exp_test", ("event", status switch
        {
            ExpStopStatus.Stopping => "stop",
            ExpStopStatus.TooShort => "stop_short",
            ExpStopStatus.AlreadyEnding => "stop_ending",
            _ => "stop_none",
        }), ("source", source), ("minutes", status == ExpStopStatus.Stopping ? minutes : null));
        string text = status switch
        {
            ExpStopStatus.Stopping => ExpTexts.Stopping(minutes),
            ExpStopStatus.TooShort => ExpTexts.StopTooShort(planned),
            ExpStopStatus.AlreadyEnding => ExpTexts.StopAlreadyEnding,
            _ => ExpTexts.NothingToStop,
        };
        // Discord: the command's own reply carries the text. Button: the channel gets it. (A too-short stop is the final message.)
        if (source != SourceDiscord && status is ExpStopStatus.Stopping or ExpStopStatus.TooShort) Send(text, null);
        return text;
    }

    public (byte[]? Png, string Text) List()
    {
        var rows = _store.LastComparable(_listCount);
        return rows.Count == 0 ? (null, ExpTexts.ListEmpty) : (ExpTestImages.List(rows), ExpTexts.ListCaption);
    }

    void OnFinished(ExpResult r)
    {
        if (Synchronous) Complete(r);
        else _ = Task.Run(() => Complete(r));
    }

    void Complete(ExpResult r)
    {
        byte[]? png = null;
        string? file = null;
        try
        {
            if (!r.TooLittle)   // a stopped test with no usable change: text only, nothing stored
            {
                png = ExpTestImages.Result(r);
                file = _store.Save(r, png);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException) { }
        _journal?.Write("exp_test", ("event", "result"), ("id", r.Id), ("source", r.Source), ("minutes", r.Minutes),
            ("start", r.Start.Text), ("end", r.End?.Text), ("gain", r.GainHundredths is { } g ? g / 100.0 : null),
            ("kills", r.Kills), ("stopped", r.Stopped), ("flags", r.Flags.Words().ToArray()), ("usable", r.Usable), ("comparable", r.Comparable),
            ("map", r.Map is { } m ? $"{m.Why} {m.NameDiff}/{m.ZoneDiff}" : null), ("file", file is null ? null : Path.GetFileName(file)));
        Send(r.Text, png);
        try { ResultReady?.Invoke(r, file); } catch (Exception) { }
    }

    void Send(string text, byte[]? png)
    {
        var now = _clock();
        _send(new AlertMessage(AlertId, text, png, png is null ? null : $"exp_{now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.png", now));
    }

    /// <summary>App closing: a running test is dropped (journalled, nothing sent).</summary>
    public void Dispose()
    {
        if (_runner.Phase != ExpPhase.Idle && _runner.Cancel()) _journal?.Write("exp_test", ("event", "dropped"), ("reason", "app_closed"));
        _runner.StartDecided -= OnStartDecided;
        _runner.Finished -= OnFinished;
    }
}
