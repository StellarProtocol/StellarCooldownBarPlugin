namespace Stellar.CooldownBar;

/// <summary>What a tile represents.</summary>
internal enum TileKind
{
    /// <summary>A skill cooldown (cyan).</summary>
    Cooldown,
    /// <summary>A self-debuff (red).</summary>
    Debuff,
}

/// <summary>
/// Immutable per-frame render snapshot for one active tile. The overlay's Funcs read this; icon textures are
/// resolved in the render layer from <see cref="IconSkillId"/>/<see cref="Id"/> (kept out of the snapshot so it
/// stays Unity-free). <see cref="Fill01"/> is the completion fraction (1 = done). <see cref="Fallback"/> marks
/// the tick-count clock (renders a '*' prefix).
/// </summary>
internal readonly record struct TrackedTile(
    TileKind Kind,
    int      Id,
    bool     IsImagine,
    int      IconSkillId,
    float    Fill01,
    int      RemainingMs,
    int      ChargeCount,
    bool     Fallback);
