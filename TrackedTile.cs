using System;

namespace Stellar.CooldownBar;

/// <summary>What a tile represents.</summary>
internal enum TileKind
{
    /// <summary>A skill cooldown (cyan).</summary>
    Cooldown,
    /// <summary>A self-debuff (red).</summary>
    Debuff,
    /// <summary>A tracked buff (green).</summary>
    Buff,
}

/// <summary>
/// Render snapshot for one active tile, rebuilt at each 10 Hz game read. It holds a SAMPLE, not a frozen value: the
/// remaining seconds at <see cref="SampleTick"/> (<c>Environment.TickCount64</c> — the one clock the bar uses, same as
/// <c>BuffTrackEntry.SnapTick</c>) and the rate it drains at, so the overlay Funcs extrapolate a smooth countdown every
/// frame with plain arithmetic (no game reads, no allocation) between reads. Buffs/debuffs drain at 1.0 (server-clock
/// based); a cooldown drains at the rate measured between reads (cooldown-haste skills tick faster).
/// <see cref="RemAtSample"/> &lt; 0 = permanent (no expiry). Icon textures are resolved in the render layer from
/// <see cref="IconSkillId"/>/<see cref="Id"/> (kept out of the snapshot so it stays Unity-free). <see cref="Fallback"/>
/// marks the tick-count clock (renders a '*' prefix).
/// </summary>
internal readonly record struct TrackedTile(
    TileKind Kind,
    int      Id,
    bool     IsImagine,
    int      IconSkillId,
    float    RemAtSample,
    long     SampleTick,
    float    TotalSec,
    float    Rate,
    int      ChargeCount,
    bool     Fallback)
{
    public bool Permanent => RemAtSample < 0f;

    /// <summary>Remaining seconds at <paramref name="now"/> (≥ 0), or -1 for a permanent tile.</summary>
    public float RemainAt(long now)
    {
        if (RemAtSample < 0f) return -1f;
        float r = RemAtSample - Rate * ((now - SampleTick) / 1000f);
        return r > 0f ? r : 0f;
    }

    /// <summary>Foot-bar fill: a cooldown fills UP as it completes (1 = ready); a buff/debuff drains (1 = full).
    /// Permanent → 1.</summary>
    public float FillAt(long now)
    {
        if (RemAtSample < 0f) return 1f;
        float frac = RemainAt(now) / Math.Max(0.001f, TotalSec);
        float f = Kind == TileKind.Cooldown ? 1f - frac : frac;
        return f < 0f ? 0f : f > 1f ? 1f : f;
    }
}
