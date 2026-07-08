using System;
using System.Collections.Generic;

namespace Stellar.CooldownBar;

/// <summary>
/// Classifies a debuff base-id as an Imagine lockout (and resolves the source Imagine skill id) by following
/// BuffTable.SkillId → "is this skill a Battle Imagine?". Lookups are injected so this is Unity-free + testable;
/// results (including negatives) are memoized per base-id.
/// </summary>
internal sealed class DebuffAttribution
{
    /// <summary>(IsImagine, ImagineSkillId) — ImagineSkillId is 0 when not an imagine.</summary>
    public readonly record struct Result(bool IsImagine, int ImagineSkillId);

    private readonly Func<int, int> _buffSkillId;       // buff base-id -> source skill id (0 = none)
    private readonly Func<int, bool> _isImagineSkill;   // skill id -> is a Battle Imagine
    private readonly Dictionary<int, Result> _memo = new();

    public DebuffAttribution(Func<int, int> buffSkillId, Func<int, bool> isImagineSkill)
    {
        _buffSkillId = buffSkillId;
        _isImagineSkill = isImagineSkill;
    }

    public Result Classify(int buffBaseId)
    {
        if (_memo.TryGetValue(buffBaseId, out var cached)) return cached;
        int skillId = _buffSkillId(buffBaseId);
        var result = skillId > 0 && _isImagineSkill(skillId)
            ? new Result(true, skillId)
            : new Result(false, 0);
        _memo[buffBaseId] = result;
        return result;
    }
}
