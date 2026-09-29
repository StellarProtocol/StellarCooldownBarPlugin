using System;
using System.Collections.Generic;

namespace Stellar.CooldownBar;

/// <summary>
/// Classifies a debuff as an Imagine lockout and resolves the Battle-Imagine CARD skill id its tile must show.
/// This is the exact decision CombatMeter's <c>Plugin.ResolveDebuffIcon</c> makes (plugin Plugin.Debuffs.cs), so the
/// two plugins render the same art for the same debuff:
/// <list type="number">
/// <item>source skill = the live wire source skill when non-zero, else the static <c>BuffTable.SkillId</c>;</item>
/// <item>if that skill is a Battle Imagine, the card is that imagine;</item>
/// <item>otherwise the curated <see cref="LockoutArcaneSkill"/> map is consulted.</item>
/// </list>
/// Lookups are injected so this is Unity-free + testable; results (including negatives) are memoized per
/// (base id, source skill).
/// </summary>
internal sealed class DebuffAttribution
{
    /// <summary>(IsImagine, ImagineSkillId) — ImagineSkillId is the card skill id to draw; 0 when not an imagine.</summary>
    public readonly record struct Result(bool IsImagine, int ImagineSkillId);

    private readonly Func<int, int> _buffSkillId;      // buff base-id -> BuffTable.SkillId (0 = none)
    private readonly Func<int, int> _imagineSkillOf;   // skill id -> resolved Battle-Imagine card skill id (0 = not an imagine)
    private readonly Dictionary<(int, int), Result> _memo = new();

    public DebuffAttribution(Func<int, int> buffSkillId, Func<int, int> imagineSkillOf)
    {
        _buffSkillId = buffSkillId;
        _imagineSkillOf = imagineSkillOf;
    }

    public Result Classify(int buffBaseId, int sourceSkillId)
    {
        var key = (buffBaseId, sourceSkillId);
        if (_memo.TryGetValue(key, out var cached)) return cached;
        int skillId = sourceSkillId != 0 ? sourceSkillId : _buffSkillId(buffBaseId);
        int card = skillId > 0 ? _imagineSkillOf(skillId) : 0;
        if (card <= 0 && LockoutArcaneSkill(buffBaseId) is { } lk)
            card = _imagineSkillOf(lk);
        var result = card > 0 ? new Result(true, card) : new Result(false, 0);
        _memo[key] = result;
        return result;
    }

    /// <summary>
    /// The skill id a debuff TILE draws its icon from: the resolved Battle-Imagine card for an imagine lockout, else
    /// the live source skill (the render layer then falls back to the buff's own icon). Handing a lockout's raw source
    /// skill to the tile instead of the card is the 2.2.3 bug that left Mechanical Failure on the generic icon.
    /// </summary>
    internal static int TileIconSkill(Result cls, int sourceSkillId) =>
        cls.IsImagine ? cls.ImagineSkillId : sourceSkillId;

    /// <summary>
    /// Curated imagine-lockout map: debuff base id -> the base Battle-Imagine skill it locks out. These debuffs carry
    /// <c>BuffTable.SkillId</c> 0 + the generic <c>buff_abnormal_icon_02</c> and name the arcane only in localized Desc
    /// text, and their live source (when present) is a slot-[0] summon variant (SkillTable has e.g. 111069
    /// "Arcane! Superconductor Surge", SlotPositionId [0]) that <c>GetImagineForSkill</c> cannot map — so point at the slot-[7,8] arcane instead.
    /// <para><b>MUST equal CombatMeter's <c>LockoutArcaneSkill</c></b> (Plugin.Debuffs.cs) — pinned by
    /// <c>LockoutMapDriftTests</c> against a fixture copied from CombatMeter origin/main. Change both together.</para>
    /// </summary>
    internal static int? LockoutArcaneSkill(int buffBaseId) =>
        LockoutArcaneMap.TryGetValue(buffBaseId, out int skill) ? skill : null;

    /// <summary>The curated map backing <see cref="LockoutArcaneSkill"/> (enumerable so the drift pin can compare it).</summary>
    internal static readonly IReadOnlyDictionary<int, int> LockoutArcaneMap = new Dictionary<int, int>
    {
        [2110049] = 3971,   // Mechanical Failure -> "Arcane! Superconductor Surge"
        [2110050] = 3957,   // Element Stasis     -> "Arcane! Fatal Spiral"
    };
}
