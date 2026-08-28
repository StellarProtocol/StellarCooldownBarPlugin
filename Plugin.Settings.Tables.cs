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

    // ── Lazy loaders (idempotent: called from VirtualListElement Count func) ──

    private void EnsureSkillTabLoaded()
    {
        if (!_stBaseLoaded) { LoadSettingsSkillTable(); _stBaseLoaded = _stBaseIds.Length > 0; _seenDirty = true; }
        if (_stBaseLoaded && _seenDirty) { RebuildMergedSkills(); ApplySkillTabFilter(_stFilter); _seenDirty = false; }
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
        if (_buffTabLoaded) return;
        LoadSettingsBuffTable();
        _buffTabLoaded = _dtCount > 0 || _btCount > 0;
        if (_dtCount > 0) ApplyDebuffTabFilter(_dtFilter);
        if (_btCount > 0) ApplyBuffTabFilter(_btFilter);
    }

    // ── Table loaders ─────────────────────────────────────────────────────────

    private void LoadSettingsSkillTable()
    {
        try
        {
            var t = StellarInterop.FindType("Bokura.SkillTableBase") ?? StellarInterop.FindType("SkillTableBase");
            if (t == null) { _services.Log.Warning("[Settings] SkillTableBase not found"); return; }

            MethodInfo? miGet = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetTable") continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(bool)) { miGet = m; break; }
            }
            if (miGet == null) { _services.Log.Warning("[Settings] SkillTableBase.GetTable not found"); return; }

            var tbl    = miGet.Invoke(null, new object[] { false });
            if (tbl == null) return;
            var values = tbl.GetType().GetProperty("Values", BindingFlags.Public | BindingFlags.Instance)
                             ?.GetValue(tbl) ?? tbl;

            var ids = new List<int>(); var names = new List<string>();
            PropertyInfo? piKvp = null, piId = null, piName = null, piIcon = null, piDesc = null;
            var descs = new List<string>();

            foreach (var item in SettingsReflectEnumerate(values))
            {
                if (item == null) continue;
                object? row = item;
                if (piKvp == null && piId == null)
                {
                    var kv = item.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (kv != null) piKvp = kv;
                }
                if (piKvp != null) row = piKvp.GetValue(item);
                if (row == null) continue;
                if (piId == null)
                {
                    var rt = row.GetType();
                    piId   = rt.GetProperty("Id",   BindingFlags.Public | BindingFlags.Instance);
                    piName = rt.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    piIcon = rt.GetProperty("Icon", BindingFlags.Public | BindingFlags.Instance);
                    piDesc = rt.GetProperty("Desc", BindingFlags.Public | BindingFlags.Instance);
                }
                int id   = (int)(piId?.GetValue(row) ?? 0);
                if (id <= 0) continue;
                string name = (string?)(piName?.GetValue(row)) ?? "";
                string icon = (string?)(piIcon?.GetValue(row)) ?? "";
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(icon)) continue;
                string desc = (string?)(piDesc?.GetValue(row)) ?? "";
                ids.Add(id); names.Add(name); descs.Add(desc);
            }
            _stBaseIds = ids.ToArray(); _stBaseNames = names.ToArray(); _stBaseDescs = descs.ToArray();
            _services.Log.Info($"[Settings] loaded {_stBaseIds.Length} skills");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Settings] LoadSkillTable: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private void LoadSettingsBuffTable()
    {
        try
        {
            var t = StellarInterop.FindType("Bokura.BuffTableBase") ?? StellarInterop.FindType("BuffTableBase");
            if (t == null) { _services.Log.Warning("[Settings] BuffTableBase not found"); return; }

            MethodInfo? miGet = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetTable") continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(bool)) { miGet = m; break; }
            }
            if (miGet == null) { _services.Log.Warning("[Settings] BuffTableBase.GetTable not found"); return; }

            var tbl    = miGet.Invoke(null, new object[] { false });
            if (tbl == null) return;
            var values = tbl.GetType().GetProperty("Values", BindingFlags.Public | BindingFlags.Instance)
                             ?.GetValue(tbl) ?? tbl;

            // Group by display Name (exact match, first-seen order). name → group index into the parallel lists.
            var dGroups = new BuffGroupAccumulator();
            var bGroups = new BuffGroupAccumulator();
            PropertyInfo? piKvp = null, piId = null, piName = null, piIcon = null, piType = null, piDesc = null;

            foreach (var item in SettingsReflectEnumerate(values))
            {
                if (item == null) continue;
                object? row = item;
                if (piKvp == null && piId == null)
                {
                    var kv = item.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (kv != null) piKvp = kv;
                }
                if (piKvp != null) row = piKvp.GetValue(item);
                if (row == null) continue;
                if (piId == null)
                {
                    var rt = row.GetType();
                    piId   = rt.GetProperty("Id",       BindingFlags.Public | BindingFlags.Instance);
                    piName = rt.GetProperty("Name",     BindingFlags.Public | BindingFlags.Instance);
                    piIcon = rt.GetProperty("Icon",     BindingFlags.Public | BindingFlags.Instance);
                    piType = rt.GetProperty("BuffType", BindingFlags.Public | BindingFlags.Instance);
                    piDesc = rt.GetProperty("Desc",     BindingFlags.Public | BindingFlags.Instance);
                }
                int id = (int)(piId?.GetValue(row) ?? 0);
                if (id <= 0) continue;
                string name = (string?)(piName?.GetValue(row)) ?? "";
                string icon = (string?)(piIcon?.GetValue(row)) ?? "";
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(icon)) continue;
                string desc = (string?)(piDesc?.GetValue(row)) ?? "";
                int buffType = (int)(piType?.GetValue(row) ?? -1);
                (buffType == 0 ? dGroups : bGroups).Add(name, id, desc);
            }
            dGroups.Export(out _dtIds, out _dtNames, out _dtDescs, out _dtMembers, out _dtCount);
            bGroups.Export(out _btIds, out _btNames, out _btDescs, out _btMembers, out _btCount);
            _services.Log.Info($"[Settings] loaded {_dtCount} debuff groups, {_btCount} buff groups");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Settings] LoadBuffTable: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    private void ApplySkillTabFilter(string text)
    {
        _stFilter = text; _stOffset = 0;
        SettingsFilterTable(text, _stIds, _stNames, _stDescs, _stCount,
            _selection.IsCooldownTracked, out _stFiltIds, out _stFiltNames, out _stFiltDescs, out _stFiltCount);
    }

    private void ApplyDebuffTabFilter(string text)
    {
        _dtFilter = text; _dtOffset = 0;
        SettingsFilterGroups(text, _dtIds, _dtNames, _dtDescs, _dtMembers, _dtCount, _selection.IsDebuffTracked,
            out _dtFiltIds, out _dtFiltNames, out _dtFiltDescs, out _dtFiltMembers, out _dtFiltCount);
    }

    private void ApplyBuffTabFilter(string text)
    {
        _btFilter = text; _btOffset = 0;
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

    // ── IL2CPP duck-typed enumerator (same as Experiment) ────────────────────

    private static IEnumerable<object?> SettingsReflectEnumerate(object collection)
    {
        var t  = collection.GetType();
        var mi = t.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
        if (mi == null) yield break;
        var enumerator = mi.Invoke(collection, null);
        if (enumerator == null) yield break;
        var et     = enumerator.GetType();
        var miNext = et.GetMethod("MoveNext",  BindingFlags.Public | BindingFlags.Instance);
        var piCur  = et.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
        if (miNext == null || piCur == null) yield break;
        while ((bool)(miNext.Invoke(enumerator, null) ?? false))
            yield return piCur.GetValue(enumerator);
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
