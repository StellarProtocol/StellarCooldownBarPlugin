using System;
using System.Collections.Generic;
using UnityEngine;

namespace Stellar.CooldownBar;

public sealed partial class Plugin
{
    private const float TileW      = 44f;   // CdTileIcon in WindowBuilder.CooldownTile.cs
    private const float TileHGap   = 6f;   // horizontal gap between tiles
    private const float TileRowH   = 62f;  // icon(44) + vlg.spacing(2) + secs text(~16)
    private const float TileVGap   = 6f;   // vertical gap between tile rows
    private const float ChromePadH = 24f;  // 2 × 12px borderless left+right chrome padding
    private const float BaseH      = 130f; // single-row baseline window height
    private const int   MaxRows    = 3;    // rows in the UI

    // Arrival-order tracking: maps (kind, id) → monotonic sequence number assigned on first appearance.
    // Pruned each tick so a re-activated tile gets a fresh (later) number instead of keeping its old position.
    private readonly Dictionary<(TileKind, int), int> _arrivalOrder = new();
    private readonly (TileKind, int)[] _activeBuf = new (TileKind, int)[MaxTiles];
    private readonly List<(TileKind, int)> _staleKeys = new();
    private int _arrivalSeq;
    private TileComparer? _tileComparer;

    private int _tilesPerRow;     // tiles that fit in current width (capped at MaxTiles/MaxRows)
    private int _rowsVisible;     // rows that fit in current height (1 or MaxRows)
    private int _totalTileCount;  // raw active count before display cap (for overflow indicator)

    // _bar.Rect is stale during resize (TickResize writes sizeDelta directly, bypassing SetRect).
    // Cache the root RectTransform and read rect.width/height directly for live values.
    private RectTransform? _barRt;
    private float BarWidth  => (_barRt ??= GameObject.Find("cooldownbar.main")?.GetComponent<RectTransform>()) != null ? _barRt!.rect.width  : _bar.Rect.Width;
    private float BarHeight => (_barRt ??= GameObject.Find("cooldownbar.main")?.GetComponent<RectTransform>()) != null ? _barRt!.rect.height : _bar.Rect.Height;

    private void UpdateArrivalOrder(int n)
    {
        for (int i = 0; i < n; i++)
        {
            var key = (_tiles[i].Kind, _tiles[i].Id);
            _activeBuf[i] = key;
            if (!_arrivalOrder.ContainsKey(key)) _arrivalOrder[key] = _arrivalSeq++;
        }
        _staleKeys.Clear();
        foreach (var k in _arrivalOrder.Keys)
        {
            bool found = false;
            for (int i = 0; i < n; i++) if (_activeBuf[i].Equals(k)) { found = true; break; }
            if (!found) _staleKeys.Add(k);
        }
        foreach (var k in _staleKeys) _arrivalOrder.Remove(k);
    }

    private void RebuildSnapshot()
    {
        BuffTrackPatch.RefreshActiveBuffs();
        int n = 0;
        long now = Environment.TickCount64;   // the bar's one clock (TrackedTile.SampleTick, BuffTrackEntry.SnapTick)
        _cdGen++;                             // Plugin.Smooth.cs — stamps live cooldown state; the rest is pruned below

        bool skillExclude  = _selection.SkillMode  == TrackMode.ExcludeBelow;
        bool debuffExclude = _selection.DebuffMode == TrackMode.ExcludeBelow;
        bool buffExclude   = _selection.BuffMode   == TrackMode.ExcludeBelow;

        foreach (var entry in SkillCDPatch.ActiveCDs.Values)
        {
            if (n >= MaxTiles) break;
            int baseId = ResolveNamedBaseSkill(entry.SkillId);
            if (baseId == 0) continue;
            if (_seenSkillIds.Add(baseId)) _seenDirty = true;   // fold into the Skills filter list (before the exclude gate, so excluded skills stay toggleable)
            bool inList = _selection.IsCooldownTracked(baseId);
            if (skillExclude ? inList : !inList) continue;
            float rem = SkillCDPatch.GetRemainSec(entry);
            if (rem <= 0f) continue;
            SampleCooldown(entry, rem, now, out float disp, out float rate);   // Plugin.Smooth.cs — measured rate, never-go-up
            bool isImagine = entry.MaxCharges > 1;
            _tiles[n++] = new TrackedTile(TileKind.Cooldown, baseId, isImagine, baseId,
                disp, now, entry.TotalSec, rate, isImagine ? entry.PendingCharges : 0, false);
        }
        PruneCdSmooth();

        foreach (var entry in BuffTrackPatch.ActiveBuffs.Values)
        {
            if (n >= MaxTiles) break;
            if (entry.BuffType != 0) continue;                               // only debuffs
            if (!_showHidden && entry.Hidden) continue;  // off-mode skips internal/hidden buffs (empty game Name OR empty Icon) — matches the settings list gate
            if (entry.Duration > 0 && entry.RemainSec < 0.05f) continue;

            var cls = ClassifyDebuff(entry);
            if (cls.IsImagine && !debuffExclude) _selection.AutoTrackImagine(entry.BuffBaseId);
            bool dbInList = _selection.IsDebuffTracked(entry.BuffBaseId);
            if (debuffExclude ? dbInList : !dbInList) continue;

            // Icon skill = the resolved Battle-Imagine CARD for a lockout (e.g. Mechanical Failure -> 3971), else the
            // live source skill. Passing entry.SkillId for a lockout drew the generic debuff icon (2.2.3 bug).
            // Sample = the entry's own server-clock snap (SnapRemain at SnapTick), drained at exactly 1.0 — the same
            // extrapolation BuffTrackEntry.RemainSec does, now evaluated per frame by the Funcs.
            _tiles[n++] = new TrackedTile(TileKind.Debuff, entry.BuffBaseId, cls.IsImagine,
                DebuffAttribution.TileIconSkill(cls, entry.SkillId),
                BuffSample(entry), entry.SnapTick, entry.Duration / 1000f, 1f, entry.Layer, false);
        }

        foreach (var entry in BuffTrackPatch.ActiveBuffs.Values)
        {
            if (n >= MaxTiles) break;
            if (entry.BuffType == 0) continue;                               // only buffs (not debuffs)
            if (!_showHidden && entry.Hidden) continue;  // off-mode skips internal/hidden buffs (empty game Name OR empty Icon) — matches the settings list gate
            if (entry.Duration > 0 && entry.RemainSec < 0.05f) continue;
            bool bfInList = _selection.IsBuffTracked(entry.BuffBaseId);
            if (buffExclude ? bfInList : !bfInList) continue;

            _tiles[n++] = new TrackedTile(TileKind.Buff, entry.BuffBaseId, false, entry.SkillId,
                BuffSample(entry), entry.SnapTick, entry.Duration / 1000f, 1f, entry.Layer, false);
        }

        UpdateArrivalOrder(n);
        _tileComparer ??= new TileComparer(_arrivalOrder);
        if (n > 1) Array.Sort(_tiles, 0, n, _tileComparer);
        _tilesPerRow    = ComputeTilesPerRow();
        _rowsVisible    = ComputeRowsVisible();
        _totalTileCount = n;
        _tileCount      = Math.Min(n, _tilesPerRow * _rowsVisible);
    }

    // Classify a debuff entry as an imagine lockout (live source skill, BuffTable.SkillId, then the curated lockout
    // map) — the same decision CombatMeter's ResolveDebuffIcon makes. See DebuffAttribution.
    private DebuffAttribution.Result ClassifyDebuff(BuffTrackEntry entry) =>
        _attr.Classify(entry.BuffBaseId, entry.SkillId);

    private int ComputeTilesPerRow()
    {
        float inner = BarWidth - ChromePadH;
        if (inner <= 0f) return MaxTiles / MaxRows;
        return Math.Min(MaxTiles / MaxRows, Math.Max(1, (int)((inner + TileHGap) / (TileW + TileHGap))));
    }

    private int ComputeRowsVisible()
    {
        float h = BarHeight;
        if (h <= 0f) return 1;
        return Math.Min(MaxRows, Math.Max(1, (int)((h - BaseH) / (TileRowH + TileVGap)) + 1));
    }

    // A buff/debuff tile's sample: -1 = permanent (Duration 0), else SnapRemain (remaining at SnapTick).
    private static float BuffSample(BuffTrackEntry e) => e.Duration == 0 ? -1f : (e.SnapRemain < 0f ? 0f : e.SnapRemain);

    // Memoize success only — table may finish loading after first CD arrives.
    private readonly Dictionary<int, int> _baseSkillMemo = new();
    private int ResolveNamedBaseSkill(int cdId)
    {
        if (_baseSkillMemo.TryGetValue(cdId, out var cached)) return cached;
        var combat = _services.GameData.Combat;
        int resolved = 0;
        if (combat.GetSkill(cdId) is { Name.Length: > 0 }) resolved = cdId;
        else { int b = cdId / 100; if (b > 0 && combat.GetSkill(b) is { Name.Length: > 0 }) resolved = b; }
        if (resolved != 0) _baseSkillMemo[cdId] = resolved;
        return resolved;
    }

    private sealed class TileComparer : IComparer<TrackedTile>
    {
        private readonly Dictionary<(TileKind, int), int> _order;
        internal TileComparer(Dictionary<(TileKind, int), int> order) => _order = order;
        public int Compare(TrackedTile a, TrackedTile b)
        {
            _order.TryGetValue((a.Kind, a.Id), out int oa);
            _order.TryGetValue((b.Kind, b.Id), out int ob);
            return oa.CompareTo(ob);
        }
    }
}
