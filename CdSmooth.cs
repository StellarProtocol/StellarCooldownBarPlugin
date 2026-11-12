namespace Stellar.CooldownBar;

/// <summary>
/// Smoothing state for ONE active cooldown between the 10 Hz game reads (see Plugin.Smooth.cs for the why). Pure
/// arithmetic — no Unity, no game types — so the display rules are unit-tested (tests/…/CdSmoothTests.cs).
/// <para>
/// Rules at each read (<see cref="Step"/>):
/// <list type="bullet">
/// <item>Reset (new entry object, TotalSec change, or charge-count change): show the read, rate 1.0, and restart the
/// rate baseline — a rate is never measured across a reset or a charge step.</item>
/// <item>Read LOWER than shown: jump down to it.</item>
/// <item>Read higher by at most <see cref="Tolerance"/> (extrapolation error): keep the lower shown value — no visible
/// tick-up from jitter.</item>
/// <item>Read higher by MORE than the tolerance, or the shown value has run out (0) while the read is still &gt; 0:
/// RESYNC — show the read, rate 1.0, restart the baseline. Without this, "never go up" was a one-way ratchet: the
/// min() of a noisy measured rate is biased low, every read could only pull the display down, and once it hit 0 it
/// stayed at "0.0s" until the cooldown actually ended (in-game 2026-11-12: a 59 s charge skill sat at 0.0s for
/// seconds, then jumped to 59 s on the charge change).</item>
/// </list>
/// </para>
/// </summary>
internal sealed class CdSmooth
{
    internal const float MaxRate      = 5f;     // a sane ceiling for measured drain (5× real time)
    internal const float MinRateDtSec = 0.02f;  // reads closer than this are too short to measure a rate from
    internal const float MinTolSec    = 0.3f;   // smallest upward correction treated as a real desync

    public object? Entry;          // the OnCDBegin entry object — a new object = a new cooldown = reset
    public float   TotalSec;
    public int     Pending, Max;   // charge state at the last read — a change = a real reset
    public float   LastRaw;        // raw remaining at the last read the rate was measured against
    public long    LastReadTick;
    public float   Disp;           // displayed remaining at SampleTick
    public long    SampleTick;
    public float   Rate = 1f;
    public int     Gen;            // read generation stamp (Plugin.PruneCdSmooth)

    /// <summary>Upward-correction tolerance for a read <paramref name="dtSec"/> after the previous one: the error an
    /// extrapolation at <paramref name="drawnRate"/> can build up against real time — max(0.3 s, 2 × dt × |rate − 1|).</summary>
    internal static float Tolerance(float dtSec, float drawnRate)
    {
        float d = drawnRate - 1f;
        float t = 2f * dtSec * (d < 0f ? -d : d);
        return t > MinTolSec ? t : MinTolSec;
    }

    /// <summary>Remaining at <paramref name="now"/> as currently drawn (≥ 0) — the TrackedTile extrapolation.</summary>
    internal float ShownAt(long now)
    {
        float shown = Disp - Rate * ((now - SampleTick) / 1000f);
        return shown > 0f ? shown : 0f;
    }

    /// <summary>One read. <paramref name="raw"/> must be &gt; 0 (callers drop a cooldown whose read is ≤ 0). Afterwards
    /// <see cref="Disp"/> at <see cref="SampleTick"/> = <paramref name="now"/> draining at <see cref="Rate"/> is the
    /// display sample.</summary>
    internal void Step(object entry, float totalSec, int pending, int max, float raw, long now)
    {
        bool reset = !ReferenceEquals(Entry, entry) || TotalSec != totalSec || Pending != pending || Max != max;
        if (reset)
        {
            Entry = entry; TotalSec = totalSec; Pending = pending; Max = max;
            Resync(raw, now);   // first read of this cooldown (or charge): assume real time until a second read measures it
            return;
        }

        float shown = ShownAt(now);   // what is on screen right now, at the rate it was drawn with (BEFORE re-measuring)
        float dt    = (now - LastReadTick) / 1000f;

        if (raw > shown + Tolerance(dt, Rate) || shown <= 0f)
        {
            Resync(raw, now);   // a real desync (or ran out early): the read wins, and the rate across it is meaningless
            return;
        }

        if (dt >= MinRateDtSec)
        {
            float m = (LastRaw - raw) / dt;
            Rate = m < 0f ? 0f : m > MaxRate ? MaxRate : m;
            LastRaw = raw; LastReadTick = now;
        }
        Disp = raw < shown ? raw : shown;   // lower read → jump down; slightly higher → keep the lower shown value
        SampleTick = now;
    }

    private void Resync(float raw, long now)
    {
        Rate = 1f;
        Disp = raw;
        LastRaw = raw; LastReadTick = now;
        SampleTick = now;
    }
}
