using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

/// <summary>
/// Placed display mode (approved 2026-11-13, <c>_scratch\prototypes\cooldownbar-redesign-APPROVED.md</c> second half):
/// the bar is not registered, and every TICKED effect — a same-name variant group counts as ONE, exactly the rows the
/// list shows ticked — gets its own small Borderless window with one T1 tile (Plugin.Placed.Window.cs), visible only while
/// that effect is active; in the layout editor all of them show (inactive ones as a faded sample).
/// <list type="bullet">
/// <item>Max <see cref="MaxPlaced"/>. Placement is STABLE: effects already placed stay placed (persisted order
/// "placed.order"); newly ticked ones are appended in list order while there is room; the rest are "not placed".</item>
/// <item>Window lifecycle: windows are registered when the placed set gains an effect and removed when it loses one or
/// Placed mode is left — only from <see cref="SyncPlaced"/>, which runs on a SELECTION change (<see cref="_trackVer"/>),
/// never per frame. The set needs the picker tables (groups), so Placed mode loads them at world entry
/// (Plugin.Normalize.cs NeedEagerTables).</item>
/// <item>Data: no second game-reading path. RebuildSnapshot (Plugin.Seen.cs) keeps every placed effect's tile in
/// <c>_tiles</c> and <see cref="AssignPlacedTiles"/> hands each window its index; the window Funcs read that.</item>
/// <item>Samples: each placed effect owns <c>_tiles[MaxTiles + slot]</c>, a frozen "12s" tile (rate 0) drawn while it is
/// not active — only ever visible in the layout editor (the window is hidden otherwise).</item>
/// </list>
/// </summary>
public sealed partial class Plugin
{
    private const int MaxPlaced = 30;
    private const int TileCap   = MaxTiles + MaxPlaced;
    private static readonly int[] PlacedSizes = { 75, 100, 125, 150, 200 };

    private sealed class PlacedEffect
    {
        public TileKind Kind;
        public int      Id;                       // the list row's representative id (window id + sample)
        public string   Name = string.Empty;
        public int      Slot;                     // 0..MaxPlaced-1: sample index + default column position
        public IWindowControl? Win;
        public int      TileIdx = -1;             // live _tiles index this refresh, -1 = not active
        // Edit-mode icon fade (Plugin.Placed.Fade.cs).
        public UnityEngine.RectTransform? Root;
        public bool     FadeApplied;
        public long     FadeScanAt;
    }

    private bool _placedMode;                   // config display.mode: 0 = Bar, 1 = Placed
    private int  _placedSize = 100;             // config placed.size: the DISPLAY size (%)
    private bool _placedLive;                   // last OnUpdate's PlacedLive()
    private readonly PlacedEffect?[] _placed = new PlacedEffect?[MaxPlaced];
    private int _placedCount;
    private readonly Dictionary<long, PlacedEffect> _placedByMember = new();   // (kind, member id) → effect
    private readonly HashSet<long> _placedKeys    = new();                       // (kind, row id) placed
    private readonly HashSet<long> _notPlacedKeys = new();                       // ticked, over the cap
    private readonly List<long> _placedOrder = new();                            // persisted placement order
    private readonly Dictionary<long, int> _sizeOverrides = new();               // (kind, row id) → % (persisted)
    private int _trackVer, _placedSyncVer = -1;

    private static long PKey(TileKind k, int id) => ((long)k << 32) | (uint)id;

    private void InitPlaced()
    {
        _placedMode = _cfg.Get("display.mode", 0) == 1;
        _placedSize = NormalizeSize(_cfg.Get("placed.size", 100));
        foreach (var k in _cfg.Get("placed.order", Array.Empty<long>()) ?? Array.Empty<long>()) _placedOrder.Add(k);
        var sk = _cfg.Get("placed.sizeKeys", Array.Empty<long>()) ?? Array.Empty<long>();
        var sv = _cfg.Get("placed.sizeVals", Array.Empty<int>()) ?? Array.Empty<int>();
        for (int i = 0; i < sk.Length && i < sv.Length; i++) if (Array.IndexOf(PlacedSizes, sv[i]) >= 0) _sizeOverrides[sk[i]] = sv[i];
    }

    private static int NormalizeSize(int pct) => Array.IndexOf(PlacedSizes, pct) >= 0 ? pct : 100;

    // A selection change (toggle, Untick all, imagine auto-track, conversion, regrouped table): the placed set re-syncs
    // on the next tick (or at once from a picker click, which needs the answer for its feedback).
    private void OnTrackedChanged() => _trackVer++;

    private bool IsPlacedMember(TileKind k, int id) => _placedByMember.ContainsKey(PKey(k, id));

    private bool IsSampleSlot(int idx) => idx >= MaxTiles && idx < TileCap && _placed[idx - MaxTiles] != null;

    // In world, no blocking menu, at least one placed effect — or the layout editor (where every placed tile shows).
    private bool PlacedGateOpen()
        => _services.ClientState.Phase == GamePhase.World
           && (_services.ClientState.UiState & (GameUIState.Blocking | GameUIState.AnyMenu)) == 0
           && !_perfOff;

    private bool PlacedLive()
        => _placedMode && _placedCount > 0
           && (PlacedGateOpen() || _services.Windows.IsLayoutEditing && _services.ClientState.Phase == GamePhase.World);

    // The _tiles index a placed window shows: its live tile, else its sample. Outside the world (the layout editor can
    // open at the title screen) the last snapshot is stale → the sample.
    private int PlacedSlot(PlacedEffect p)
        => p.TileIdx >= 0 && _services.ClientState.Phase == GamePhase.World ? p.TileIdx : MaxTiles + p.Slot;

    private bool PlacedSample(PlacedEffect p) => PlacedSlot(p) >= MaxTiles;

    private int EffectiveSize(PlacedEffect p)
        => _sizeOverrides.TryGetValue(PKey(p.Kind, p.Id), out int s) ? s : _placedSize;

    // RebuildSnapshot, Placed mode: the first live tile of each placed effect (a group may have several members active).
    private void AssignPlacedTiles(int n)
    {
        for (int s = 0; s < MaxPlaced; s++) if (_placed[s] is { } p) p.TileIdx = -1;
        for (int i = 0; i < n; i++)
            if (_placedByMember.TryGetValue(PKey(_tiles[i].Kind, _tiles[i].Id), out var p) && p.TileIdx < 0) p.TileIdx = i;
    }

    private void ClearPlacedTiles()
    {
        for (int s = 0; s < MaxPlaced; s++) if (_placed[s] is { } p) p.TileIdx = -1;
    }

    // OnUpdate. The sync is an int compare unless the selection moved; the fade is idle outside the layout editor.
    private void TickPlaced()
    {
        if (!_placedMode) return;
        if (_placedSyncVer != _trackVer && _stBaseLoaded && _buffTabLoaded) SyncPlaced();
        TickPlacedFade();   // Plugin.Placed.Fade.cs
    }

    // ── display mode ──────────────────────────────────────────────────────────────────────────────────────────────
    private void SetDisplayMode(bool placed)
    {
        if (placed == _placedMode) return;
        _placedMode = placed;
        _cfg.Set("display.mode", placed ? 1 : 0);
        _cfg.Save();
        _tileCount = _totalTileCount = 0;   // the next refresh rebuilds for the new mode
        if (placed)
        {
            _bar?.Remove();
            _bar = null;
            _placedSyncVer = -1;            // sync as soon as the tables are loaded (eager load: Plugin.Normalize.cs)
            _eagerTablesAt = 0;
        }
        else
        {
            RemoveAllPlaced();
            _bar = RegisterBar();           // same id → the saved rect comes back
        }
        CloseSizePopover();                 // Plugin.Settings.Placed.cs
        CancelUntickAll();
        _selVersion++;                      // rows re-style (forced Only ticked, size chips / not placed)
    }

    // ── sync ──────────────────────────────────────────────────────────────────────────────────────────────────────
    // Ticked rows in LIST order (skills, debuffs, buffs; each in table order — the picker's own row data), then a stable
    // placement: kept order first, new ones appended while there is room. Allocates (selection-change path only).
    private void SyncPlaced()
    {
        _placedSyncVer = _trackVer;
        var rows  = new List<(TileKind Kind, int Id, string Name, int[] Members)>();
        for (int i = 0; i < _stCount; i++)
            if (_selection.IsCooldownTracked(_stIds[i])) rows.Add((TileKind.Cooldown, _stIds[i], _stNames[i], new[] { _stIds[i] }));
        var isDb = _isDebuffTrackedFn ??= _selection.IsDebuffTracked;
        var isBf = _isBuffTrackedFn ??= _selection.IsBuffTracked;
        for (int g = 0; g < _dtCount; g++)
            if (GroupAllTracked(_dtMembers[g], isDb)) rows.Add((TileKind.Debuff, _dtIds[g], _dtNames[g], _dtMembers[g]));
        for (int g = 0; g < _btCount; g++)
            if (GroupAllTracked(_btMembers[g], isBf)) rows.Add((TileKind.Buff, _btIds[g], _btNames[g], _btMembers[g]));

        var rowOf = new Dictionary<long, int>(rows.Count);
        for (int i = 0; i < rows.Count; i++) rowOf.TryAdd(PKey(rows[i].Kind, rows[i].Id), i);
        var want = new List<long>(MaxPlaced);
        var wantSet = new HashSet<long>();
        foreach (var k in _placedOrder)
            if (want.Count < MaxPlaced && rowOf.ContainsKey(k) && wantSet.Add(k)) want.Add(k);
        foreach (var r in rows)
        {
            if (want.Count >= MaxPlaced) break;
            long k = PKey(r.Kind, r.Id);
            if (wantSet.Add(k)) want.Add(k);
        }

        // Diff the windows: remove what left, add what's new (free slots), refresh names / members of the rest.
        for (int s = 0; s < MaxPlaced; s++)
            if (_placed[s] is { } p && !wantSet.Contains(PKey(p.Kind, p.Id))) RemovePlaced(s);
        _placedByMember.Clear();
        _placedKeys.Clear();
        foreach (var k in want)
        {
            var r = rows[rowOf[k]];
            var p = FindPlaced(k) ?? AddPlaced(r.Kind, r.Id, StripTags(r.Name));
            if (p == null) break;            // no free slot (cannot happen: want ≤ MaxPlaced)
            p.Name = StripTags(r.Name);
            _placedKeys.Add(k);
            foreach (int m in r.Members) _placedByMember.TryAdd(PKey(r.Kind, m), p);
        }
        _notPlacedKeys.Clear();
        foreach (var r in rows) { long k = PKey(r.Kind, r.Id); if (!wantSet.Contains(k)) _notPlacedKeys.Add(k); }

        if (!SameOrder(want, _placedOrder))
        {
            _placedOrder.Clear();
            _placedOrder.AddRange(want);
            _cfg.Set("placed.order", _placedOrder.ToArray());
            _cfg.Save();
        }
        _selVersion++;   // placed / not placed labels and the counts recompute
    }

    private static bool SameOrder(List<long> a, List<long> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private PlacedEffect? FindPlaced(long key)
    {
        for (int s = 0; s < MaxPlaced; s++)
            if (_placed[s] is { } p && PKey(p.Kind, p.Id) == key) return p;
        return null;
    }

    private PlacedEffect? AddPlaced(TileKind kind, int id, string name)
    {
        int slot = Array.IndexOf(_placed, null);
        if (slot < 0) return null;
        var p = new PlacedEffect { Kind = kind, Id = id, Name = name, Slot = slot };
        _placed[slot] = p;
        _placedCount++;
        _tiles[MaxTiles + slot] = SampleTile(kind, id);
        p.Win = RegisterPlacedWindow(p);    // Plugin.Placed.Window.cs
        return p;
    }

    private void RemovePlaced(int slot)
    {
        var p = _placed[slot];
        if (p == null) return;
        p.Win?.Remove();
        if (p.FadeApplied) _fadeApplied--;
        p.Win = null; p.Root = null;
        _placed[slot] = null;
        _placedCount--;
        _secStr[MaxTiles + slot] = null!;   // the next effect in this slot re-formats its sample label
        _chipStr[MaxTiles + slot] = null!;
    }

    private void RemoveAllPlaced()
    {
        for (int s = 0; s < MaxPlaced; s++) RemovePlaced(s);
        _placedByMember.Clear();
        _placedKeys.Clear();
        _notPlacedKeys.Clear();
        _placedSyncVer = -1;
    }

    // A frozen sample for the layout editor: 12 s left of 20 (rate 0, so it never ticks), no chip. Icon source like the
    // bar's: the skill itself; a debuff's Imagine card when it is a lockout; a buff's own icon.
    private TrackedTile SampleTile(TileKind kind, int id)
    {
        int iconSkill = kind == TileKind.Cooldown ? id
                      : kind == TileKind.Debuff  ? DebuffAttribution.TileIconSkill(_attr.Classify(id, 0), 0)
                      : 0;
        return new TrackedTile(kind, id, false, iconSkill, 12f, 0L, 20f, 0f, 0, false);
    }

    // ── per-effect size override (the row chip + popover, Plugin.Settings.Placed.cs) ─────────────────────────────
    private void SetSizeOverride(long key, int pct)   // pct 0 = Default (follow the DISPLAY size)
    {
        if (pct == 0) { if (!_sizeOverrides.Remove(key)) return; }
        else { if (_sizeOverrides.TryGetValue(key, out int cur) && cur == pct) return; _sizeOverrides[key] = pct; }
        var keys = new long[_sizeOverrides.Count];
        var vals = new int[_sizeOverrides.Count];
        int i = 0;
        foreach (var kv in _sizeOverrides) { keys[i] = kv.Key; vals[i] = kv.Value; i++; }
        _cfg.Set("placed.sizeKeys", keys);
        _cfg.Set("placed.sizeVals", vals);
        _cfg.Save();
    }

    private void SetPlacedSize(int pct)
    {
        pct = NormalizeSize(pct);
        if (pct == _placedSize) return;
        _placedSize = pct;
        _cfg.Set("placed.size", pct);
        _cfg.Save();
    }
}
