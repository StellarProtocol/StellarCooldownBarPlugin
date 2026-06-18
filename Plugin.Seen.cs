using System;
using Stellar.Abstractions.Domain;

namespace Stellar.CooldownBar;

// Per-tick: observe everything into the seen registry (for the picker), auto-track imagine lockouts, and
// build the active+tracked TrackedTile[] snapshot the overlay renders.
public sealed partial class Plugin
{
    private long NowMs(out bool fallback)
    {
        long server = _services.CombatSnapshot.ServerNowMs;
        fallback = server == 0;
        return fallback ? Environment.TickCount : server;
    }

    private void RebuildSnapshot()
    {
        long now = NowMs(out bool fallback);
        int n = 0;
        var cds = _services.CombatSnapshot.LocalCooldowns;
        for (int i = 0; i < cds.Count && n < MaxTiles; i++) TryAddCooldown(cds[i], now, fallback, ref n);
        var buffs = _services.CombatSnapshot.LocalBuffs;
        for (int i = 0; i < buffs.Count && n < MaxTiles; i++) TryAddDebuff(buffs[i], now, fallback, ref n);
        // Sort active window [0,n): cooldowns before debuffs, each ascending by remaining (most-urgent left).
        if (n > 1) Array.Sort(_tiles, 0, n, TileComparer.Instance);
        _tileCount = n;
    }

    private void TryAddCooldown(in SkillCooldown cd, long now, bool fallback, ref int n)
    {
        // LocalCooldowns are keyed by the leveled SkillFightLevel id (baseSkillId*100 + level); resolve to the base
        // skill so name (GetSkill) + icon (LoadSkillIcon) — which live on SkillTable[base] — work, and leveled
        // variants collapse to one entry. 0 = internal marker / talent-trigger with no name (e.g. "场地标记") → skip.
        int baseId = ResolveNamedBaseSkill(cd.SkillId);
        if (baseId == 0) return;
        _seen.Observe(TileKind.Cooldown, baseId);
        if (!_selection.IsCooldownTracked(baseId)) return;

        var imagine = _services.ResonanceData.GetImagineForSkill(cd.SkillId);
        bool isImagine = imagine is { ChargeCount: > 1 };
        int effDur = EffectiveDur(cd, isImagine);
        long curRem = (cd.BeginTimeMs + effDur) - now;   // remaining on the CURRENT recharge window

        // Multi-charge skills (imagines): the wire only moves `begin` ON CAST and gives no live charge count, and
        // after the first charge's window expires it tells us NOTHING about the remaining charges still recharging.
        // So simulate the sequential recharge ourselves (count down N*perCharge → 0 continuously) — see ImagineSim.
        if (isImagine && imagine is { } imi)
        {
            int perCharge = effDur > 0 ? effDur : imi.RechargeMs;
            var rc = _imgCalc.Update(baseId, cd.BeginTimeMs, perCharge, imi.ChargeCount, now);
            if (!rc.Active) return;   // full → ready
            _tiles[n++] = new TrackedTile(TileKind.Cooldown, baseId, false, baseId,
                rc.FullFraction, rc.ToFullMs, rc.ChargesAvailable, fallback);
            return;
        }

        // Single-charge / normal skill: plain remaining on the one cooldown window, minus banked cd-acceleration
        // (Tina's buff etc.) — see ApplyAccelBank.
        long effRem = ApplyAccelBank(baseId, cd.BeginTimeMs, now, curRem, isImagine);
        if (effRem <= 0) return;
        float fill = 1f - effRem / (float)Math.Max(1, effDur);
        _tiles[n++] = new TrackedTile(TileKind.Cooldown, baseId, false, baseId,
            Clamp01(fill), (int)effRem, cd.Kind == SkillCooldownKind.Charge ? cd.ChargeCount : 0, fallback);
    }

    // cd-ACCELERATION banking. The cd-accel attrs (11960 skill / 11980 imagine, read live from the entity map and
    // reverting when the buff ends) speed the countdown RATE while active — the game advances the cooldown by
    // dt*(1+accel/10000) and BANKS the saved time, so it stays ahead during AND after the buff. We integrate the
    // saved time per cooldown: bank += dt * accel/10000 each refresh, then show rawRem - bank. accel=0 → no growth
    // (no stuck); a new cast (begin change) resets the bank. Keyed by the resolved base skill id.
    private readonly System.Collections.Generic.Dictionary<int, (long Begin, long LastTick, double BankMs)> _accelBank = new();
    private long ApplyAccelBank(int baseId, long begin, long now, long rawRemMs, bool isImagine)
    {
        long accel = ReadEntityCdAttr(CdAccelSkillAttr) + (isImagine ? ReadEntityCdAttr(CdAccelImagineAttr) : 0);
        if (!_accelBank.TryGetValue(baseId, out var s) || s.Begin != begin)
        {
            s = (begin, now, 0.0);   // new cast (or first sight) → fresh bank
        }
        else
        {
            long dt = now - s.LastTick;
            if (dt > 0 && dt <= 2000 && accel > 0) s.BankMs += dt * (accel / 10000.0);   // cap dt so a refresh gap can't over-bank
            s.LastTick = now;
        }
        _accelBank[baseId] = s;
        return rawRemMs - (long)s.BankMs;
    }

    private void TryAddDebuff(in ActiveBuff b, long now, bool fallback, ref int n)
    {
        var info = _services.GameData.Combat.GetBuff(b.BaseId);
        if (info is not { } bi || !bi.IsDebuff || bi.Name.Length == 0) return;   // skip non-debuffs + nameless internals

        _seen.Observe(TileKind.Debuff, b.BaseId);
        var cls = _attr.Classify(b.BaseId);
        if (cls.IsImagine) _selection.AutoTrackImagine(b.BaseId);   // headline default; opt-out honoured

        if (!_selection.IsDebuffTracked(b.BaseId)) return;
        long rem = (b.CreateTimeMs + b.DurationMs) - now;
        if (rem <= 0) return;
        float fill = 1f - rem / (float)Math.Max(1, b.DurationMs);
        _tiles[n++] = new TrackedTile(TileKind.Debuff, b.BaseId, cls.IsImagine, cls.ImagineSkillId,
            Clamp01(fill), (int)rem, 0, fallback);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    // Cooldown reduction (NOT haste). The real, per-character/gear reduction lives in these FLOAT-stored attrs
    // (the stats probe now reads them via TryGetAttr<float>). The wire SkillCD's own per-cast fields
    // (sub_cd_ratio field 9 / sub_cd_fixed field 10) take precedence when the server populates them; otherwise we
    // fall back to the player attrs:
    //        11760 技能冷却缩减万分比 — cd reduction RATIO (per-10000)
    //        11750 技能冷却缩减毫秒   — cd reduction FLAT (ms)
    //        11960 / 11980          — cd-ACCELERATION (skill / imagine), per-10000 — incl. temp buffs (Tina's)
    // effDur = dur*(1 - (ratio+accel)/10000) - fixedMs. This is a VARIABLE value: the old hardcoded 10% only
    // matched gear where 11760 happened to be 1000; reading the live attr is correct for all imagines (e.g. Tina
    // 150s table → ~140s when 11760≈667), and tracks/reverts Tina's cd-accel buff because the read is live.
    internal const int CdReductionAttr      = 11760;   // ratio, per-10000
    internal const int CdReductionFixedAttr = 11750;   // flat, ms
    internal const int CdAccelSkillAttr     = 11960;   // skill cd-acceleration, per-10000
    internal const int CdAccelImagineAttr   = 11980;   // imagine cd-acceleration, per-10000
    private int EffectiveDur(in SkillCooldown cd, bool isImagine)
    {
        // GEAR cooldown reduction only: wire sub_cd fields when populated, else the player's 11760 (ratio) / 11750
        // (flat) read from the wire-captured entity attr map. These are gear-stable, so the recharge is correct
        // per-character (Muku/Boyce and Tina/Airona alike). The cd-ACCELERATION attrs (11960/11980 — Tina's buff)
        // are deliberately NOT applied: that buff speeds the countdown RATE, not the total duration, so folding it
        // into effDur both mis-modelled it AND stuck (its value never reverts in the wire map). Rate-based accel is
        // the deferred item.
        long ratio   = cd.SubCdRatio   != 0 ? cd.SubCdRatio   : ReadEntityCdAttr(CdReductionAttr);
        long fixedMs = cd.SubCdFixedMs != 0 ? cd.SubCdFixedMs : ReadEntityCdAttr(CdReductionFixedAttr);
        int afterRatio = Stellar.Abstractions.Domain.GameData.ImagineCooldownCalc.EffectiveDuration(
            cd.DurationMs, cd.ValidCdTimeMs, ratio / 10000f);
        int eff = afterRatio - (int)fixedMs;
        return eff < 1 ? 1 : eff;
    }

    // Read a cooldown attribute from the wire-captured LOCAL-entity attr map (EntityDetail.GetAttributes). This is
    // the only source that carries the gear cd-reduction (11760/11750): PlayerStats/MainEntity reports them
    // unreadable as Int64/Int32/Float. Returns 0 when absent.
    private long ReadEntityCdAttr(int attrId)
    {
        var local = _services.CombatSnapshot.LocalEntityId;
        return !local.IsNone && _services.EntityDetail.GetAttributes(local).TryGetValue(attrId, out var v) ? v : 0;
    }

    // Sequential-recharge simulator for multi-charge imagines — shared with CombatMeter so the two can't drift.
    private readonly Stellar.Abstractions.Domain.GameData.ImagineCooldownCalc _imgCalc = new();

    // Resolve a cooldown's leveled SkillFightLevel id (baseSkillId*100 + level) to its base skill id, IF that base
    // has a readable name. Returns the named base id, or 0 when neither the leveled id nor its base resolves to a
    // named SkillTable row (internal markers / talent-triggers like "场地标记" — filtered from the picker).
    // Memoized — but ONLY on success: the hot-update SkillTable can finish loading AFTER the first cooldown
    // arrives (e.g. casting immediately on relaunch), so caching a miss would permanently hide that cooldown for
    // the session. Leaving misses uncached lets the lookup retry each frame until the table is ready, then lock in.
    private readonly System.Collections.Generic.Dictionary<int, int> _baseSkillMemo = new();
    private int ResolveNamedBaseSkill(int cdId)
    {
        if (_baseSkillMemo.TryGetValue(cdId, out var cached)) return cached;
        var combat = _services.GameData.Combat;
        int resolved = 0;
        if (combat.GetSkill(cdId) is { Name.Length: > 0 }) resolved = cdId;
        else { int b = cdId / 100; if (b > 0 && combat.GetSkill(b) is { Name.Length: > 0 }) resolved = b; }
        if (resolved != 0) _baseSkillMemo[cdId] = resolved;   // cache successes only — see remark above
        return resolved;
    }

    private sealed class TileComparer : System.Collections.Generic.IComparer<TrackedTile>
    {
        public static readonly TileComparer Instance = new();
        public int Compare(TrackedTile a, TrackedTile b)
        {
            if (a.Kind != b.Kind) return a.Kind.CompareTo(b.Kind);   // Cooldown(0) before Debuff(1)
            return a.RemainingMs.CompareTo(b.RemainingMs);
        }
    }
}
