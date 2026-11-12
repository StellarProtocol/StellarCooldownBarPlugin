using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

public sealed partial class Plugin
{
    private const int SettingsPoolSize = 14;

    // ── Skills tab raw + filtered data ────────────────────────────────────────
    private int[]    _stIds       = Array.Empty<int>();   // merged source: base SkillTable + bar-seen extras
    private string[] _stNames     = Array.Empty<string>();
    private string[] _stDescs     = Array.Empty<string>();
    private int      _stCount     = 0;
    private int[]    _stBaseIds   = Array.Empty<int>();   // raw SkillTable load (before folding in bar-seen skills)
    private string[] _stBaseNames = Array.Empty<string>();
    private string[] _stBaseDescs = Array.Empty<string>();
    private bool     _stBaseLoaded;
    // Id sets for SelectionNormalizer.MapSkill (the bar's resolution + the stale-id conversion): what the list shows
    // (the table filtered by "Hidden effects") and every SkillTable row (unfiltered). Replaced on each table load.
    private HashSet<int> _stListSet = new();
    private HashSet<int> _stAllSet  = new();
    // Base skill ids the bar has actually displayed. Folded into the Skills tab so every cooldown that can appear
    // on the bar is toggleable — including placeholder-named skills the game's SkillTable doesn't list (owner 2026-08-19).
    private readonly HashSet<int> _seenSkillIds = new();
    private bool _seenDirty;
    private int[]    _stFiltIds   = Array.Empty<int>();
    private string[] _stFiltNames = Array.Empty<string>();
    private string[] _stFiltDescs = Array.Empty<string>();
    private int      _stFiltCount = 0;
    private string   _stFilter    = "";
    private int      _stOffset    = 0;
    private readonly UvRect[] _stUv = new UvRect[SettingsPoolSize];

    // ── Debuffs tab raw + filtered data ──────────────────────────────────────
    // Rows are GROUPED by display Name: _dtIds/_dtNames/_dtDescs hold one representative per group (first-seen
    // member), _dtMembers holds every id sharing that Name. Selection stays per-id (a toggle writes all members).
    private int[]    _dtIds       = Array.Empty<int>();     // representative id per group (icon + tooltip)
    private string[] _dtNames     = Array.Empty<string>();
    private string[] _dtDescs     = Array.Empty<string>();
    private int[][]  _dtMembers   = Array.Empty<int[]>();   // all ids sharing each group's Name
    private int      _dtCount     = 0;
    private int[]    _dtFiltIds   = Array.Empty<int>();
    private string[] _dtFiltNames = Array.Empty<string>();
    private string[] _dtFiltDescs = Array.Empty<string>();
    private int[][]  _dtFiltMembers = Array.Empty<int[]>();
    private int      _dtFiltCount = 0;
    private string   _dtFilter    = "";
    private int      _dtOffset    = 0;
    private readonly UvRect[] _dtUv = new UvRect[SettingsPoolSize];

    // ── Buffs tab raw + filtered data ─────────────────────────────────────────
    // Grouped by display Name — see the Debuffs block above.
    private int[]    _btIds       = Array.Empty<int>();     // representative id per group
    private string[] _btNames     = Array.Empty<string>();
    private string[] _btDescs     = Array.Empty<string>();
    private int[][]  _btMembers   = Array.Empty<int[]>();   // all ids sharing each group's Name
    private int      _btCount     = 0;
    private int[]    _btFiltIds   = Array.Empty<int>();
    private string[] _btFiltNames = Array.Empty<string>();
    private string[] _btFiltDescs = Array.Empty<string>();
    private int[][]  _btFiltMembers = Array.Empty<int[]>();
    private int      _btFiltCount = 0;
    private string   _btFilter    = "";
    private int      _btOffset    = 0;
    private readonly UvRect[] _btUv = new UvRect[SettingsPoolSize];
    private bool     _buffTabLoaded = false;
    // Every BuffTable row id (debuffs and buffs, unfiltered — hidden effects included): the stale-id conversion keeps a
    // saved tick that is in no listed group but still a real effect (SelectionNormalizer.Groups). Replaced on each load.
    private HashSet<int> _buffAllSet = new();

    // ── Lazy loaders (idempotent: called from VirtualListElement Count func) ──
    // Both finish any in-flight sliced walk synchronously (Plugin.Settings.TableLoad.cs): the caller needs the rows now.

    private void EnsureSkillTabLoaded()
    {
        if (!_stBaseLoaded) StepSkillLoad(Unbounded);
        AfterSkillLoad();
    }

    // The merged list (base table + bar-seen extras), its filter and the placed set: once after a load, and again
    // whenever the seen set grows.
    private void AfterSkillLoad()
    {
        if (_stBaseLoaded && _seenDirty) { RebuildMergedSkills(); ApplySkillTabFilter(_stFilter); _seenDirty = false; OnTrackedChanged(); }
    }

    // Fold bar-seen skills (placeholder-named / SkillTable-absent) into the Skills tab so every cooldown that can
    // appear on the bar is toggleable. Rebuilt from the raw table + the seen set whenever the seen set changes.
    private void RebuildMergedSkills()
    {
        var ids   = new List<int>(_stBaseIds);
        var names = new List<string>(_stBaseNames);
        var descs = new List<string>(_stBaseDescs);
        var have  = new HashSet<int>(_stBaseIds);
        foreach (var id in _seenSkillIds)
        {
            if (!_stAllSet.Contains(id)) continue;             // only real SkillTable rows (never a sub-id like 392301)
            if (!have.Add(id)) continue;                       // already in the base table (or a dup) — skip
            var info = _services.GameData.Combat.GetSkill(id);
            var nm   = info?.Name;
            ids.Add(id);
            names.Add(string.IsNullOrEmpty(nm) ? $"Skill {id}" : nm!);
            descs.Add(info?.Description ?? "");
        }
        _stIds = ids.ToArray(); _stNames = names.ToArray(); _stDescs = descs.ToArray(); _stCount = ids.Count;
    }

    private void EnsureBuffTabLoaded()
    {
        if (!_buffTabLoaded) StepBuffLoad(Unbounded);   // publishes, re-syncs the placed set and re-applies the filters
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    private void ApplySkillTabFilter(string text)
    {
        _stFilter = text; _stOffset = 0;
        _selVersion++; _selScrollReset = true;   // re-sorted rows: slot caches + counts recompute, list back to the top
        SettingsFilterTable(text, _stIds, _stNames, _stDescs, _stCount,
            _selection.IsCooldownTracked, out _stFiltIds, out _stFiltNames, out _stFiltDescs, out _stFiltCount);
    }

    private void ApplyDebuffTabFilter(string text)
    {
        _dtFilter = text; _dtOffset = 0;
        _selVersion++; _selScrollReset = true;   // re-sorted rows: slot caches + counts recompute, list back to the top
        SettingsFilterGroups(text, _dtIds, _dtNames, _dtDescs, _dtMembers, _dtCount, _selection.IsDebuffTracked,
            out _dtFiltIds, out _dtFiltNames, out _dtFiltDescs, out _dtFiltMembers, out _dtFiltCount);
    }

    private void ApplyBuffTabFilter(string text)
    {
        _btFilter = text; _btOffset = 0;
        _selVersion++; _selScrollReset = true;   // re-sorted rows: slot caches + counts recompute, list back to the top
        SettingsFilterGroups(text, _btIds, _btNames, _btDescs, _btMembers, _btCount, _selection.IsBuffTracked,
            out _btFiltIds, out _btFiltNames, out _btFiltDescs, out _btFiltMembers, out _btFiltCount);
    }

    private static void SettingsFilterTable(string text, int[] srcIds, string[] srcNames, string[] srcDescs,
        int srcCount, Func<int, bool> isTracked,
        out int[] ids, out string[] names, out string[] descs, out int count)
    {
        var q = text?.Trim() ?? "";
        var ri = new List<int>(); var rn = new List<string>(); var rd = new List<string>();
        for (int i = 0; i < srcCount; i++)
        {
            if (q.Length > 0 && srcNames[i].IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            ri.Add(srcIds[i]); rn.Add(srcNames[i]); rd.Add(srcDescs[i]);
        }
        // Stable sort: tracked items float to the top
        var trackedIds = new List<int>(); var trackedNames = new List<string>(); var trackedDescs = new List<string>();
        var restIds    = new List<int>(); var restNames    = new List<string>(); var restDescs    = new List<string>();
        for (int i = 0; i < ri.Count; i++)
        {
            if (isTracked(ri[i])) { trackedIds.Add(ri[i]); trackedNames.Add(rn[i]); trackedDescs.Add(rd[i]); }
            else                  { restIds.Add(ri[i]);    restNames.Add(rn[i]);    restDescs.Add(rd[i]); }
        }
        trackedIds.AddRange(restIds); trackedNames.AddRange(restNames); trackedDescs.AddRange(restDescs);
        ids = trackedIds.ToArray(); names = trackedNames.ToArray(); descs = trackedDescs.ToArray(); count = ids.Length;
    }

    // Grouped filter (Buffs / Debuffs): same search + tracked-floats-to-top behaviour as SettingsFilterTable,
    // but each row is a Name-group. A group counts as "tracked" only when EVERY member id is tracked, and that
    // predicate — not a single id — drives the sort. The member arrays are carried through so the toggle/tooltip
    // can reach every id in the group.
    private static void SettingsFilterGroups(string text, int[] srcIds, string[] srcNames, string[] srcDescs,
        int[][] srcMembers, int srcCount, Func<int, bool> isTracked,
        out int[] ids, out string[] names, out string[] descs, out int[][] members, out int count)
    {
        var q = text?.Trim() ?? "";
        var ri = new List<int>(); var rn = new List<string>(); var rd = new List<string>(); var rm = new List<int[]>();
        for (int i = 0; i < srcCount; i++)
        {
            if (q.Length > 0 && srcNames[i].IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            ri.Add(srcIds[i]); rn.Add(srcNames[i]); rd.Add(srcDescs[i]); rm.Add(srcMembers[i]);
        }
        // Stable sort: fully-tracked groups float to the top.
        var tId = new List<int>(); var tN = new List<string>(); var tD = new List<string>(); var tM = new List<int[]>();
        var xId = new List<int>(); var xN = new List<string>(); var xD = new List<string>(); var xM = new List<int[]>();
        for (int i = 0; i < ri.Count; i++)
        {
            bool all = GroupAllTracked(rm[i], isTracked);
            if (all) { tId.Add(ri[i]); tN.Add(rn[i]); tD.Add(rd[i]); tM.Add(rm[i]); }
            else     { xId.Add(ri[i]); xN.Add(rn[i]); xD.Add(rd[i]); xM.Add(rm[i]); }
        }
        tId.AddRange(xId); tN.AddRange(xN); tD.AddRange(xD); tM.AddRange(xM);
        ids = tId.ToArray(); names = tN.ToArray(); descs = tD.ToArray(); members = tM.ToArray(); count = ids.Length;
    }

    // A group is "tracked" (toggle ON) only when every member id is tracked. A partially-tracked group — only
    // reachable from a pre-existing per-id config — reads OFF, so one tap then selects the whole group.
    private static bool GroupAllTracked(int[] members, Func<int, bool> isTracked)
    {
        if (members.Length == 0) return false;
        for (int i = 0; i < members.Length; i++) if (!isTracked(members[i])) return false;
        return true;
    }

    // Collapses per-id buff/debuff rows into Name-groups in first-seen order. The first row of a Name becomes the
    // group's representative (id + desc, used for the icon and click-tooltip); later rows just add their id.
    private sealed class BuffGroupAccumulator
    {
        private readonly Dictionary<string, int> _index = new();   // Name → group slot
        private readonly List<int>       _ids   = new();           // representative id per group
        private readonly List<string>    _names = new();
        private readonly List<string>    _descs = new();           // representative desc per group
        private readonly List<List<int>> _members = new();         // all ids sharing the Name

        public void Add(string name, int id, string desc)
        {
            if (_index.TryGetValue(name, out int gi)) { _members[gi].Add(id); return; }
            _index[name] = _ids.Count;
            _ids.Add(id); _names.Add(name); _descs.Add(desc); _members.Add(new List<int> { id });
        }

        public void Export(out int[] ids, out string[] names, out string[] descs, out int[][] members, out int count)
        {
            ids = _ids.ToArray(); names = _names.ToArray(); descs = _descs.ToArray();
            var m = new int[_members.Count][];
            for (int i = 0; i < _members.Count; i++) m[i] = _members[i].ToArray();
            members = m; count = _ids.Count;
        }
    }
}
