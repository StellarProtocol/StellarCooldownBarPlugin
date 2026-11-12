using Xunit;

namespace Stellar.CooldownBar.Tests;

/// <summary>
/// Pins the cooldown display rules (CdSmooth.cs). Regression: in game (2026-11-12) a charge skill sat at "0.0s" with an
/// empty fill for seconds because the display could only ever ratchet DOWN — a read higher than the screen was ignored,
/// and 0 was a dead end until a reset. Reads here are 100 ms apart, like RebuildSnapshot.
/// </summary>
public class CdSmoothTests
{
    private const long Read = 100;
    private static readonly object Entry = new();

    private static void Step(CdSmooth s, float raw, long now, int pending = 2, int max = 3) =>
        s.Step(Entry, 59f, pending, max, raw, now);

    [Fact]
    public void First_read_shows_the_read_at_real_time()
    {
        var s = new CdSmooth();
        Step(s, 40f, 1000);
        Assert.Equal(40f, s.Disp);
        Assert.Equal(1f, s.Rate);
    }

    [Fact]
    public void Steady_drain_measures_rate_one()
    {
        var s = new CdSmooth();
        Step(s, 40f, 1000);
        Step(s, 39.9f, 1000 + Read);
        Assert.Equal(1f, s.Rate, 2);
        Assert.Equal(39.9f, s.Disp, 2);
    }

    [Fact]
    public void Small_upward_jitter_does_not_tick_up()
    {
        var s = new CdSmooth();
        Step(s, 40f, 1000);
        Step(s, 40.1f, 1000 + Read);          // shown 39.9, read 0.2 higher — inside the 0.3 s tolerance
        Assert.Equal(39.9f, s.Disp, 2);
    }

    [Fact]
    public void Charge_skill_remain_jumps_up_then_continues_resyncs_instead_of_sticking_at_zero()
    {
        var s = new CdSmooth();
        long t = 1000;
        Step(s, 2f, t);
        // A bad read (e.g. charge state and progress read out of step) drops raw sharply: the measured rate clamps
        // to 5 and the display races ahead.
        t += Read; Step(s, 0.5f, t);
        Assert.Equal(5f, s.Rate);
        // Then the game's remaining jumps UP (same charge count) and keeps draining at real time.
        t += Read; Step(s, 30f, t);
        Assert.Equal(30f, s.Disp);            // resynced to the read, not left at the shown 0
        Assert.Equal(1f, s.Rate);
        for (int i = 0; i < 20; i++)
        {
            t += Read; float raw = 30f - (i + 1) * 0.1f;
            Step(s, raw, t);
            Assert.True(s.ShownAt(t) > 0f, $"display hit 0 at read {i} while the game reports {raw}s");
            Assert.Equal(raw, s.Disp, 1);
            Assert.Equal(1f, s.Rate, 1);
        }
    }

    [Fact]
    public void Display_that_ran_out_resyncs_at_the_next_read()
    {
        var s = new CdSmooth();
        Step(s, 0.6f, 1000);
        Step(s, 0.1f, 1000 + Read);           // rate 5 → screen hits 0 within the next 20 ms
        Assert.Equal(0f, s.ShownAt(1000 + 2 * Read));
        Step(s, 0.15f, 1000 + 2 * Read);      // game still has time left, even though barely above the screen
        Assert.Equal(0.15f, s.Disp);
        Assert.Equal(1f, s.Rate);
    }

    [Fact]
    public void Charge_change_resets_and_does_not_measure_across_it()
    {
        var s = new CdSmooth();
        Step(s, 0.2f, 1000, pending: 2);
        Step(s, 59f, 1000 + Read, pending: 1);
        Assert.Equal(59f, s.Disp);
        Assert.Equal(1f, s.Rate);
        Step(s, 58.9f, 1000 + 2 * Read, pending: 1);
        Assert.Equal(1f, s.Rate, 2);
    }

    [Fact]
    public void Tolerance_grows_with_rate_error()
    {
        Assert.Equal(0.3f, CdSmooth.Tolerance(0.1f, 1f));
        Assert.Equal(0.8f, CdSmooth.Tolerance(0.1f, 5f), 3);
    }
}
