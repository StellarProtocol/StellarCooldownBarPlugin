using Stellar.Abstractions.Diagnostics;

namespace Stellar.CooldownBar;

public sealed partial class Plugin
{
    private float _diagAccum;
    private int _diagBudget = 120;
    private bool _diagSubscribed;

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
        _services.Log.Info($"[CooldownBar][diag] attrs: haste={ps.TryGetAttribute(11120)} cdRed={ps.TryGetAttribute(11760)} cdAccel={ps.TryGetAttribute(11960)} imgAccel={ps.TryGetAttribute(11980)}");
        _services.Log.Info($"[CooldownBar][diag] activeCDs={SkillCDPatch.ActiveCDs.Count} activeBuffs={BuffTrackPatch.ActiveBuffs.Count} tiles={_tileCount}");

        foreach (var entry in SkillCDPatch.ActiveCDs.Values)
        {
            int b     = ResolveNamedBaseSkill(entry.SkillId);
            float rem = SkillCDPatch.GetRemainSec(entry);
            _services.Log.Info($"[CooldownBar][diag]  cd id={entry.SkillId} base={b} '{entry.SkillName}' total={entry.TotalSec:F1}s rem={rem:F1}s charges={entry.PendingCharges}/{entry.MaxCharges} tracked={b != 0 && _selection.IsCooldownTracked(b)}");
        }

        int debCount = 0;
        foreach (var entry in BuffTrackPatch.ActiveBuffs.Values)
        {
            if (entry.BuffType != 0) continue;
            debCount++;
            var cls = _attr.Classify(entry.BuffBaseId);
            _services.Log.Info($"[CooldownBar][diag]  debuff base={entry.BuffBaseId} '{entry.BuffName}' src={entry.SkillId} imagine={cls.IsImagine} rem={entry.RemainSec:F1}s tracked={_selection.IsDebuffTracked(entry.BuffBaseId)}");
        }
        _services.Log.Info($"[CooldownBar][diag] active debuffs={debCount}");
    }
}
