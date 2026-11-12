using System.Collections.Generic;

namespace Stellar.CooldownBar;

// Smooth cooldown display between the 10 Hz game reads (user chose "smooth", 2026-11-12).
//
// The game is read at 10 Hz (RebuildSnapshot); the overlay Funcs draw every frame by extrapolating each tile's sample
// (TrackedTile.RemainAt). Buffs/debuffs drain at exactly 1.0 (server-clock based, nothing speeds them up). Cooldowns
// do NOT: a cooldown-haste effect (e.g. Tina's) can make a 1 s cooldown elapse in 0.5 s, so each read measures the
// drain rate from the previous read — (prevRaw - newRaw) / elapsed — clamped to [0, MaxRate], 1.0 on the first read.
//
// No-tick-up rule (CdSmooth.cs has the exact rules + tests): the DISPLAYED cooldown doesn't tick up from small
// extrapolation error — a read higher than the screen by ≤ max(0.3 s, 2 × dt × |rate − 1|) keeps the lower on-screen
// value; a LOWER read jumps down. A bigger upward gap, or the screen reaching 0 while the game still reports time left,
// RESYNCS to the read at rate 1.0 (the first cut never resynced, so a charge skill could sit at "0.0s" for seconds).
// A reset (new OnCDBegin entry, TotalSec change, charge-count change) also restarts at the read, rate 1.0. A cooldown
// whose READ is ≤ 0 is dropped at that read (RebuildSnapshot), so tile composition only changes at reads.
//
// State is one small class per active cooldown, created on first sight and pruned at each read (generation stamp), so
// the steady state allocates nothing.
public sealed partial class Plugin
{
    private readonly Dictionary<int, CdSmooth> _cdSmooth = new();
    private readonly List<int> _cdSmoothStale = new();
    private int _cdGen;

    // One read of one cooldown: raw = SkillCDPatch.GetRemainSec (which also refreshed e.PendingCharges/MaxCharges).
    // Returns the display sample (remaining at `now`) and its drain rate for the TrackedTile.
    private void SampleCooldown(SkillCdEntry e, float raw, long now, out float disp, out float rate)
    {
        if (!_cdSmooth.TryGetValue(e.SkillId, out var s)) { s = new CdSmooth(); _cdSmooth[e.SkillId] = s; }
        s.Gen = _cdGen;
        s.Step(e, e.TotalSec, e.PendingCharges, e.MaxCharges, raw, now);
        disp = s.Disp;
        rate = s.Rate;
    }

    // After a read: drop state for cooldowns that produced no tile this read (ended, untracked, or filtered).
    private void PruneCdSmooth()
    {
        _cdSmoothStale.Clear();
        foreach (var kv in _cdSmooth)
            if (kv.Value.Gen != _cdGen) _cdSmoothStale.Add(kv.Key);
        for (int i = 0; i < _cdSmoothStale.Count; i++) _cdSmooth.Remove(_cdSmoothStale[i]);
    }
}
