using System;
using System.Collections.Generic;
using UnityEngine;

namespace Stellar.CooldownBar;

public sealed partial class Plugin
{
    // ── Bar geometry (redesign T1 tile, 2026-11-13) ──────────────────────────────────────────────────────────────
    // Every size below is PINNED by the element tree (Plugin.Bar.Tile.cs / Plugin.Bar.cs), not estimated from fonts:
    //   tile  = card padding 3 + [icon 40 | gap 2 | bar 4 | gap 2 | footer 16] + padding 3 = 70 tall;
    //           3 + 44 (the footer cell's fixed width) + 3 = 50 wide. The icon is a fixed 40×40 GameTexture (or a 40 px
    //           strut box), the bar track a 4 px BarElement, the footer a Text with Height 16 (chip 14 inside it).
    //   caption row = the 28 px gear button (texts are shorter and centre on it);
    //   window = Borderless chrome padding 12 on every side (WindowBuilder.Chrome.cs BuildBorderless).
    // Pitch: 56 px across (50 + gap 6), 76 px down (70 + gap 6). Window height for r rows:
    //   H(r) = 12 + 28 + 6 + r·70 + (r−1)·6 + 12 = 52 + 76·r  →  1 row 128 · 2 rows 204 · 3 rows 280.
    // Validate in game with the bridge (ui.inspect "CdTile*" Bubble rects) — these are the layout contracts, not a
    // screenshot measurement.
    private const float TileW      = 50f;   // card outer width
    private const float TileH      = 70f;   // card outer height
    private const float TileHGap   = 6f;    // horizontal gap between tiles (tile row Gap)
    private const float TileVGap   = 6f;    // vertical gap between tile rows (and caption → first row)
    private const float CaptionH   = 28f;   // caption row = the round gear button
    private const float ChromePad  = 12f;   // Borderless padding, each side
    private const int   MaxRows    = 3;     // rows in the UI

    // Window height that fits exactly `rows` tile rows under the caption (the HUD window's min = 1 row, max = 3).
    private static float BarHeightFor(int rows)
        => ChromePad * 2f + CaptionH + TileVGap + rows * TileH + (rows - 1) * TileVGap;

    // Arrival-order tracking: maps (kind, id) → monotonic sequence number assigned on first appearance.
    // Pruned each tick so a re-activated tile gets a fresh (later) number instead of keeping its old position.
    private readonly Dictionary<(TileKind, int), int> _arrivalOrder = new();
    private readonly (TileKind, int)[] _activeBuf = new (TileKind, int)[MaxTiles];
    private readonly List<(TileKind, int)> _staleKeys = new();
    private int _arrivalSeq;
    private TileComparer? _tileComparer;

    // A _tiles index a tile Func may read: a live tile of the current snapshot, or a placed effect's edit-mode sample.
    private bool SlotValid(int idx) => (uint)idx < (uint)_tileCount || IsSampleSlot(idx);   // + Plugin.Placed.cs samples

    private int _tilesPerRow;     // tiles that fit in current width (capped at MaxTiles/MaxRows)
    private int _rowsVisible;     // rows that fit in current height (1 or MaxRows)
    private int _totalTileCount;  // raw active count before display cap (for overflow indicator)

    // Live bar size while the user resizes: TickResize writes the window root's sizeDelta directly, bypassing SetRect.
    // Read the root RectTransform's rect. The framework names the window root GameObject after WindowSpec.Id
    // (WindowBuilder.Chrome*.cs: UGuiPrimitives.NewRect(spec.Id, parent)), so Find("cooldownbar.main") hits it.
    // (Framework 2.30's IWindowControl.Rect → WindowRenderer.GetRect also reads token.Rect.rect live — token.Rect IS
    // that root — so _bar.Rect is a correct fallback there; the cached-RT path keeps older frameworks live too.)
    // The window is destroyed on hide and remounted on show, so the cached RT can become Unity fake-null: re-validate
    // with Unity's == (the old `??=` used a plain reference check and kept a dead object), and retry Find at most once
    // per second while missing — GameObject.Find walks the whole scene.
    private RectTransform? _barRt;
    private long _barRtRetryAt;

    private RectTransform? BarRt()
    {
        var rt = _barRt;
        if (!ReferenceEquals(rt, null) && rt == null) rt = _barRt = null;   // destroyed (remount) → look it up again
        if (ReferenceEquals(rt, null))
        {
            long now = Environment.TickCount64;
            if (now < _barRtRetryAt) return null;
            _barRtRetryAt = now + 1000;
            var go = GameObject.Find("cooldownbar.main");
            rt = _barRt = go != null ? go.GetComponent<RectTransform>() : null;
        }
        return rt;
    }

    private float BarWidth  { get { var rt = BarRt(); return rt != null ? rt.rect.width  : _bar?.Rect.Width ?? 0f;  } }
    private float BarHeight { get { var rt = BarRt(); return rt != null ? rt.rect.height : _bar?.Rect.Height ?? 0f; } }

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

        // Placed mode FORCES "Only ticked" and keeps only the PLACED effects (a ticked 31st is not drawn); each kind's
        // own mode is untouched and applies again in Bar mode.
        bool placed        = _placedMode;
        bool skillExclude  = !placed && _selection.SkillMode  == TrackMode.ExcludeBelow;
        bool debuffExclude = !placed && _selection.DebuffMode == TrackMode.ExcludeBelow;
        bool buffExclude   = !placed && _selection.BuffMode   == TrackMode.ExcludeBelow;

        foreach (var entry in SkillCDPatch.ActiveCDs.Values)
        {
            if (n >= MaxTiles) break;
            int baseId = ResolveNamedBaseSkill(entry.SkillId);
            if (baseId == 0) continue;
            // Fold into the Skills list (before the exclude gate, so excluded skills stay toggleable) — only real SkillTable
            // rows the list doesn't show yet (hidden skills that fired), never a fallback guess or a sub-id like 392301.
            if (_stBaseLoaded && !_stListSet.Contains(baseId) && _stAllSet.Contains(baseId) && _seenSkillIds.Add(baseId))
                _seenDirty = true;
            bool inList = placed ? IsPlacedMember(TileKind.Cooldown, baseId) : _selection.IsCooldownTracked(baseId);
            if (skillExclude ? inList : !inList) continue;
            float rem = SkillCDPatch.GetRemainSec(entry);
            if (rem <= 0f) continue;
            SampleCooldown(entry, rem, now, out float disp, out float rate);   // Plugin.Smooth.cs / CdSmooth.cs — measured rate, no jitter tick-up, resync
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
            if (cls.IsImagine && !debuffExclude && _selection.ShouldAutoTrackImagine(entry.BuffBaseId))
            {
                _selection.AutoTrackImagine(entry.BuffBaseId);
                _selVersion++;   // the picker's ticked counts / row ticks recompute (Plugin.Settings.Rows.cs)
                OnTrackedChanged();   // Plugin.Placed.cs — a newly ticked lockout gets its placed tile
            }
            bool dbInList = placed ? IsPlacedMember(TileKind.Debuff, entry.BuffBaseId) : _selection.IsDebuffTracked(entry.BuffBaseId);
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
            bool bfInList = placed ? IsPlacedMember(TileKind.Buff, entry.BuffBaseId) : _selection.IsBuffTracked(entry.BuffBaseId);
            if (buffExclude ? bfInList : !bfInList) continue;

            _tiles[n++] = new TrackedTile(TileKind.Buff, entry.BuffBaseId, false, entry.SkillId,
                BuffSample(entry), entry.SnapTick, entry.Duration / 1000f, 1f, entry.Layer, false);
        }

        UpdateArrivalOrder(n);
        _tileComparer ??= new TileComparer(_arrivalOrder);
        if (n > 1) Array.Sort(_tiles, 0, n, _tileComparer);
        if (placed)
        {
            // Every placed tile, not just what fits the bar's rows; each placed window then picks its tile index.
            _totalTileCount = _tileCount = n;
            AssignPlacedTiles(n);   // Plugin.Placed.cs
            return;
        }
        _tilesPerRow    = ComputeTilesPerRow();
        _rowsVisible    = ComputeRowsVisible();
        _totalTileCount = n;
        // Overflow: the last visible slot gives way to the round "+N" chip, so the chip always sits inside the panel
        // (a full last row + the chip would run past the right edge).
        int cap = _tilesPerRow * _rowsVisible;
        _tileCount = n <= cap ? n : Math.Max(0, cap - 1);
    }

    // Classify a debuff entry as an imagine lockout (live source skill, BuffTable.SkillId, then the curated lockout
    // map) — the same decision CombatMeter's ResolveDebuffIcon makes. See DebuffAttribution.
    private DebuffAttribution.Result ClassifyDebuff(BuffTrackEntry entry) =>
        _attr.Classify(entry.BuffBaseId, entry.SkillId);

    // Tiles that FULLY fit across: n·50 + (n−1)·6 ≤ width − 24 (0.5 px slack for float rects). Never clips a tile.
    private int ComputeTilesPerRow()
    {
        float inner = BarWidth - ChromePad * 2f;
        if (inner <= 0f) return MaxTiles / MaxRows;
        return Math.Min(MaxTiles / MaxRows, Math.Max(1, (int)((inner + TileHGap + 0.5f) / (TileW + TileHGap))));
    }

    // Rows that FULLY fit down: BarHeightFor(r) ≤ height — a row appears only once the window is tall enough for all
    // of it (the window's min/max are exactly 1 and 3 rows, so this is 1..3).
    private int ComputeRowsVisible()
    {
        float h = BarHeight;
        if (h <= 0f) return 1;
        float avail = h - (ChromePad * 2f + CaptionH + TileVGap) + TileVGap + 0.5f;
        return Math.Min(MaxRows, Math.Max(1, (int)(avail / (TileH + TileVGap))));
    }

    // A buff/debuff tile's sample: -1 = permanent (Duration 0), else SnapRemain (remaining at SnapTick).
    private static float BuffSample(BuffTrackEntry e) => e.Duration == 0 ? -1f : (e.SnapRemain < 0f ? 0f : e.SnapRemain);

    // Live cooldown id → the skill id its tile (and its tick) uses: the SAME mapping as the stale-id conversion
    // (SelectionNormalizer.MapSkill — the id the Skills list shows, else id/100, else a hidden SkillTable row, else 0), so a
    // Furious Hammer cooldown reported as 392301 matches the tick on 3923. The game's own name lookup is NOT the test: it
    // answers with a name for sub-ids like 392301 (that kept it as its own skill, in game 2026-11-13).
    // Until the table is loaded (world entry; Plugin.Normalize.cs loads it eagerly) the old named-row guess stands in, and
    // is never memoised; list results are memoised until the next table load (which clears the memo).
    private readonly Dictionary<int, int> _baseSkillMemo = new();
    private int ResolveNamedBaseSkill(int cdId)
    {
        if (_baseSkillMemo.TryGetValue(cdId, out var cached)) return cached;
        if (_stBaseLoaded)
        {
            int m = SelectionNormalizer.MapSkill(cdId, _stListSet, _stAllSet);
            _baseSkillMemo[cdId] = m;
            return m;
        }
        var combat = _services.GameData.Combat;
        if (combat.GetSkill(cdId) is { Name.Length: > 0 }) return cdId;
        int b = cdId / 100;
        return b > 0 && combat.GetSkill(b) is { Name.Length: > 0 } ? b : 0;
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
