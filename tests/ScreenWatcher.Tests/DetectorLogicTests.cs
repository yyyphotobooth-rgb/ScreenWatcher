using ScreenWatcher.Detection;

namespace ScreenWatcher.Tests;

/// <summary>Fire-once / re-arm logic for detectors A and B with fake inputs.</summary>
public class DetectorLogicTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);
    static readonly DeathObservation Dead = new(GaugeDead: true, HpFilled: false, DialogPresent: true);
    static readonly DeathObservation Alive = new(GaugeDead: false, HpFilled: true, DialogPresent: false);
    static readonly DeathObservation DarkGaugeOnly = new(GaugeDead: true, HpFilled: false, DialogPresent: false);
    static readonly DeathObservation DialogOnly = new(GaugeDead: false, HpFilled: false, DialogPresent: true);

    static DeathDetector NewDeath() => new(TestData.Config.Death);
    static GameDropDetector NewDrop() => new(TestData.Config.Drop);

    /// <summary>Feeds one observation per step; returns the step indexes where an event fired.</summary>
    static List<int> Feed(DeathDetector d, IEnumerable<DeathObservation> seq, double stepSeconds = 0.5)
    {
        var fired = new List<int>();
        int i = 0;
        foreach (var o in seq)
        {
            if (d.Update(o, T0.AddSeconds(i * stepSeconds)) is { } e)
            {
                Assert.Equal(WatchEventKind.Death, e.Kind);
                Assert.Equal("ยานถูกยิงตก!", e.Message);
                fired.Add(i);
            }
            i++;
        }
        return fired;
    }

    static IEnumerable<DeathObservation> Repeat(DeathObservation o, int n) => Enumerable.Repeat(o, n);

    [Fact]
    public void Death_fires_only_after_hold_time()
    {
        // 0.5 s steps: step 3 is 1.5 s after step 0.
        Assert.Equal([3], Feed(NewDeath(), Repeat(Dead, 4)));
        Assert.Empty(Feed(NewDeath(), Repeat(Dead, 3)));
    }

    [Fact]
    public void Death_fires_once_while_still_dead()
    {
        Assert.Equal([3], Feed(NewDeath(), Repeat(Dead, 40)));
    }

    [Fact]
    public void Death_needs_both_gauge_and_dialog()
    {
        Assert.Empty(Feed(NewDeath(), Repeat(DarkGaugeOnly, 10)));
        Assert.Empty(Feed(NewDeath(), Repeat(DialogOnly, 10)));
    }

    [Fact]
    public void Interruption_restarts_the_hold_timer()
    {
        var seq = Repeat(Dead, 3).Append(DarkGaugeOnly).Concat(Repeat(Dead, 4));
        // Run restarts at step 4; fires at step 4 + 3 = 7.
        Assert.Equal([7], Feed(NewDeath(), seq));
    }

    [Fact]
    public void Death_does_not_refire_until_hp_fill_returns()
    {
        var seq = Repeat(Dead, 5)                 // fires at 3
            .Concat(Repeat(DarkGaugeOnly, 2))     // dialog clicked away, still dark: not re-armed
            .Concat(Repeat(Dead, 6));             // dead again without HP ever returning: no event
        Assert.Equal([3], Feed(NewDeath(), seq));
    }

    [Fact]
    public void Death_rearms_after_hp_fill_returns()
    {
        var seq = Repeat(Dead, 5)        // fires at 3
            .Concat(Repeat(Alive, 2))    // steps 5,6 re-arm
            .Concat(Repeat(Dead, 5));    // steps 7..11, fires at 10
        Assert.Equal([3, 10], Feed(NewDeath(), seq));
    }

    [Fact]
    public void Reset_timing_restarts_hold_but_keeps_armed_state()
    {
        var d = NewDeath();
        Assert.Null(d.Update(Dead, T0));
        Assert.Null(d.Update(Dead, T0.AddSeconds(1)));
        d.ResetTiming();
        Assert.Null(d.Update(Dead, T0.AddSeconds(2)));      // run restarts here
        Assert.Null(d.Update(Dead, T0.AddSeconds(3)));
        Assert.NotNull(d.Update(Dead, T0.AddSeconds(3.5)));  // 1.5 s after restart
    }

    /// <summary>One check per step; returns the step indexes where an event fired.</summary>
    static List<int> FeedDrop(GameDropDetector d, params bool[] found)
    {
        var fired = new List<int>();
        for (int i = 0; i < found.Length; i++)
            if (d.Update(found[i], T0.AddSeconds(i)) is { } e)
            {
                Assert.Equal(WatchEventKind.GameDropped, e.Kind);
                Assert.Equal("ขาดการติดต่อกับยาน (เกมส์หลุด)", e.Message);
                fired.Add(i);
            }
        return fired;
    }

    [Fact]
    public void Drop_never_fires_if_game_never_seen()
    {
        Assert.Empty(FeedDrop(NewDrop(), false, false, false, false));
    }

    [Fact]
    public void Drop_fires_after_two_consecutive_misses_once()
    {
        Assert.Equal([2], FeedDrop(NewDrop(), true, false, false, false, false, false));
    }

    [Fact]
    public void Single_miss_does_not_fire_and_resets()
    {
        Assert.Empty(FeedDrop(NewDrop(), true, false, true, false, true, false, true));
    }

    [Fact]
    public void Drop_rearms_when_window_reappears()
    {
        Assert.Equal([2, 6], FeedDrop(NewDrop(), true, false, false, false, true, false, false, false));
    }
}
