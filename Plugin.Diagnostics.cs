using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.CooldownBar;

// Diagnostic logging, gated on StellarDiagnostics.IsEnabled (STELLAR_DIAGNOSTICS=1 / `test` deploy mode). Dumps
// the live LocalCooldowns + LocalBuffs the snapshot sees — ids, resolved base + name, remaining, tracked-state —
// so cases like "imagine on cooldown but no tile" can be diagnosed from the log instead of guessed at.
public sealed partial class Plugin
{
    private float _diagAccum;
    private int _diagBudget = 120;   // cap log volume (~4 min at 2s cadence)
    private bool _diagSubscribed;

    // Haste / cooldown attributes (FightAttrTable): 11120 急速, 11930 急速万分比, 11750 技能冷却缩减毫秒 (cd reduction
    // ms), 11760 技能冷却缩减万分比 (cd reduction per-10000), 11960 冷却加速万分比, 11980 独立计时战斗资源冷却加速万分比.
    private static readonly int[] HasteAttrs = { 11120, 11121, 11930, 11750, 11760, 11960, 11980, 107 };

    private void LogSnapshotDiag(float dt)
    {
        if (!StellarDiagnostics.IsEnabled || _diagBudget <= 0) return;
        if (!_diagSubscribed) { foreach (var a in HasteAttrs) _services.PlayerStats.Subscribe(a); _diagSubscribed = true; }
        _diagAccum += dt;
        if (_diagAccum < 2f) return;
        _diagAccum = 0f;
        _diagBudget--;

        var ps = _services.PlayerStats;
        _services.Log.Info($"[CooldownBar][diag] attrs: haste11120={ps.TryGetAttribute(11120)} haste11930={ps.TryGetAttribute(11930)} cdRedMs11750={ps.TryGetAttribute(11750)} cdRed11760={ps.TryGetAttribute(11760)} cdAccel11960={ps.TryGetAttribute(11960)} imgAccel11980={ps.TryGetAttribute(11980)}");

        long now = NowMs(out bool fb);
        var cds = _services.CombatSnapshot.LocalCooldowns;
        _services.Log.Info($"[CooldownBar][diag] now={now} fb={fb} cds={cds.Count} tiles={_tileCount}");
        for (int i = 0; i < cds.Count; i++)
        {
            var cd = cds[i];
            int b = ResolveNamedBaseSkill(cd.SkillId);
            var name = b != 0 && _services.GameData.Combat.GetSkill(b) is { } s ? s.Name : "(unnamed)";
            long rem = (cd.BeginTimeMs + cd.DurationMs) - now;
            bool tracked = b != 0 && _selection.IsCooldownTracked(b);
            bool isImg = _services.ResonanceData.GetImagineForSkill(cd.SkillId) is { ChargeCount: > 1 };
            int effDur = EffectiveDur(cd, isImg);
            long effRem = (cd.BeginTimeMs + effDur) - now;   // cooldown-reduction-adjusted remaining (what the bar shows)
            _services.Log.Info($"[CooldownBar][diag]  cd id={cd.SkillId} base={b} '{name}' kind={cd.Kind} chg={cd.ChargeCount} begin={cd.BeginTimeMs} dur={cd.DurationMs} validCd={cd.ValidCdTimeMs} subRatio={cd.SubCdRatio} subFixed={cd.SubCdFixedMs} effDur={effDur} rem={rem} effRem={effRem} accel={cd.AccelerateCdRatio} tracked={tracked}");
            if (b != 0) LogImagineCandidates(cd.SkillId, b, effDur);
        }

        var buffs = _services.CombatSnapshot.LocalBuffs;
        int deb = 0;
        for (int i = 0; i < buffs.Count; i++)
        {
            var bf = buffs[i];
            var info = _services.GameData.Combat.GetBuff(bf.BaseId);
            bool isDeb = info is { IsDebuff: true };
            if (isDeb) deb++;
            var im = _attr.Classify(bf.BaseId);
            long rem = (bf.CreateTimeMs + bf.DurationMs) - now;
            _services.Log.Info($"[CooldownBar][diag]  buff base={bf.BaseId} '{info?.Name}' isDebuff={isDeb} src={info?.SkillId} imagine={im.IsImagine} dur={bf.DurationMs} rem={rem} tracked={_selection.IsDebuffTracked(bf.BaseId)}");
        }
        _services.Log.Info($"[CooldownBar][diag] buffs={buffs.Count} debuffs={deb}");
    }

    // Imagine candidates for the per-charge basis — compare against the game's action-bar number.
    // byLevel = GetImagineForSkill(skillId) (the leveled id; suspected mis-resolution);
    // byBase  = GetImagineForSkill(baseId) (the /100 base, e.g. 3921 Tina) — its EnergyChargeTime is the
    // candidate the game's per-charge window seems to match. effDur = current basis (wire dur × reduction).
    private void LogImagineCandidates(int skillId, int baseId, int effDur)
    {
        var byBase  = baseId != 0 ? _services.ResonanceData.GetImagineForSkill(baseId) : null;
        _services.Log.Info($"[CooldownBar][diag]   imagine byBase({baseId})='{byBase?.Name}' cooldown={byBase?.CooldownMs} charges={byBase?.ChargeCount} | cd-attrs(entity): red11760={ReadEntityCdAttr(11760)} fixed11750={ReadEntityCdAttr(11750)} accel11960={ReadEntityCdAttr(11960)} accel11980={ReadEntityCdAttr(11980)} | effDur={effDur}");
    }
}
