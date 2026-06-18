using System.Collections.Generic;

namespace Stellar.CooldownBar;

/// <summary>
/// Curated map: imagine <b>lockout</b> debuff base-id → the source imagine's skill id (for icon + ★ treatment).
///
/// <para>The game's <c>BuffTable.SkillId</c> is <b>0</b> for these lockout debuffs, so there is no data-driven
/// link from the lockout buff back to the imagine that applied it — even the ZDPS reference tool hardcodes the
/// association. This table supplies it, so a lockout renders its source imagine's artwork (via
/// <c>LoadImagineIcon</c>) and gets the ★ imagine-lockout treatment instead of showing as a plain debuff.</para>
///
/// <para>Extend as new imagines are identified: key = lockout buff base-id (the 2110xxx range), value = the
/// imagine's base skill id (the same id <c>GetImagineForSkill</c> / <c>LoadImagineIcon</c> resolve).</para>
/// </summary>
internal static class ImagineLockouts
{
    public static readonly IReadOnlyDictionary<int, int> Map = new Dictionary<int, int>
    {
        // "Time Stasis" — Tina's "Arcane! Time Acceleration Decree" lockout → imagine skill 3921.
        [2110056] = 3921,
    };
}
