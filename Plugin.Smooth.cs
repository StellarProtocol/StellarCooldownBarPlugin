using System.Collections.Generic;

namespace Stellar.CooldownBar;

// Smooth cooldown display between the 10 Hz game reads (user chose "smooth", 2026-11-12).
//
// The game is read at 10 Hz (RebuildSnapshot); the overlay Funcs draw every frame by extrapolating each tile's sample
// (TrackedTile.RemainAt). Buffs/debuffs drain at exactly 1.0 (server-clock based, nothing speeds them up). Cooldowns
// do NOT: a cooldown-haste effect (e.g. Tina's) can make a 1 s cooldown elapse in 0.5 s, so each read measures the
// drain rate from the previous read — (prevRaw - newRaw) / elapsed — clamped to [0, MaxRate], 1.0 on the first read.
//
// Never-go-up rule: between frames the DISPLAYED cooldown never increases, except on a real reset (a new OnCDBegin
// entry object, a TotalSec change, or a charge-count change). If a read says remaining is HIGHER than what is on screen
// (rate jitter / a haste ending), keep the lower on-screen value and continue at the newly measured rate; if it is
// LOWER, jump down to it. A tile whose extrapolation reaches 0 shows 0 and is dropped at the next read, so tile
// composition (and the arrival-order row) only changes at reads.
//
// State is one small class per active cooldown, created on first sight and pruned at each read (generation stamp), so
// the steady state allocates nothing.
public sealed partial class Plugin
{
    private const float MaxCdRate    = 5f;      // a sane ceiling for measured drain (5× real time)
    private const float MinRateDtSec = 0.02f;   // reads closer than this are too short to measure a rate from

    private sealed class CdSmooth
    {
        public SkillCdEntry? Entry;   // the OnCDBegin entry object — a new object = a new cooldown = reset
        public float TotalSec;
        public int   Pending, Max;    // charge state at the last read — a change = a real reset
        public float LastRaw;         // raw remaining at the last read the rate was measured against
        public long  LastReadTick;
        public float Disp;            // displayed remaining at SampleTick
        public long  SampleTick;
        public float Rate = 1f;
        public int   Gen;
    }

    private readonly Dictionary<int, CdSmooth> _cdSmooth = new();
    private readonly List<int> _cdSmoothStale = new();
    private int _cdGen;

    // One read of one cooldown: raw = SkillCDPatch.GetRemainSec (which also refreshed e.PendingCharges/MaxCharges).
    // Returns the display sample (remaining at `now`) and its drain rate for the TrackedTile.
    private void SampleCooldown(SkillCdEntry e, float raw, long now, out float disp, out float rate)
    {
        if (!_cdSmooth.TryGetValue(e.SkillId, out var s)) { s = new CdSmooth(); _cdSmooth[e.SkillId] = s; }
        s.Gen = _cdGen;

        bool reset = s.Entry != e || s.TotalSec != e.TotalSec || s.Pending != e.PendingCharges || s.Max != e.MaxCharges;
        if (reset)
        {
            s.Entry = e; s.TotalSec = e.TotalSec; s.Pending = e.PendingCharges; s.Max = e.MaxCharges;
            s.Rate = 1f;                 // first read of this cooldown: assume real time until a second read measures it
            s.Disp = raw;
            s.LastRaw = raw; s.LastReadTick = now;
        }
        else
        {
            // What is on screen right now, extrapolated with the rate it was drawn at (BEFORE re-measuring).
            float shown = s.Disp - s.Rate * ((now - s.SampleTick) / 1000f);
            if (shown < 0f) shown = 0f;

            float dt = (now - s.LastReadTick) / 1000f;
            if (dt >= MinRateDtSec)
            {
                float m = (s.LastRaw - raw) / dt;
                s.Rate = m < 0f ? 0f : m > MaxCdRate ? MaxCdRate : m;
                s.LastRaw = raw; s.LastReadTick = now;
            }
            s.Disp = raw < shown ? raw : shown;   // lower read → jump down; higher read → never go up, converge
        }
        s.SampleTick = now;
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
