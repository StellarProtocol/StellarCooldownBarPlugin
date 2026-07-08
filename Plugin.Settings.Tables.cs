using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;

namespace Stellar.CooldownBar;

public sealed partial class Plugin
{
    private const int SettingsPoolSize = 14;

    // ── Skills tab raw + filtered data ────────────────────────────────────────
    private int[]    _stIds       = Array.Empty<int>();
    private string[] _stNames     = Array.Empty<string>();
    private string[] _stDescs     = Array.Empty<string>();
    private int      _stCount     = 0;
    private int[]    _stFiltIds   = Array.Empty<int>();
    private string[] _stFiltNames = Array.Empty<string>();
    private string[] _stFiltDescs = Array.Empty<string>();
    private int      _stFiltCount = 0;
    private string   _stFilter    = "";
    private int      _stOffset    = 0;
    private readonly UvRect[] _stUv = new UvRect[SettingsPoolSize];

    // ── Debuffs tab raw + filtered data ──────────────────────────────────────
    private int[]    _dtIds       = Array.Empty<int>();
    private string[] _dtNames     = Array.Empty<string>();
    private string[] _dtDescs     = Array.Empty<string>();
    private int      _dtCount     = 0;
    private int[]    _dtFiltIds   = Array.Empty<int>();
    private string[] _dtFiltNames = Array.Empty<string>();
    private string[] _dtFiltDescs = Array.Empty<string>();
    private int      _dtFiltCount = 0;
    private string   _dtFilter    = "";
    private int      _dtOffset    = 0;
    private readonly UvRect[] _dtUv = new UvRect[SettingsPoolSize];

    // ── Buffs tab raw + filtered data ─────────────────────────────────────────
    private int[]    _btIds       = Array.Empty<int>();
    private string[] _btNames     = Array.Empty<string>();
    private string[] _btDescs     = Array.Empty<string>();
    private int      _btCount     = 0;
    private int[]    _btFiltIds   = Array.Empty<int>();
    private string[] _btFiltNames = Array.Empty<string>();
    private string[] _btFiltDescs = Array.Empty<string>();
    private int      _btFiltCount = 0;
    private string   _btFilter    = "";
    private int      _btOffset    = 0;
    private readonly UvRect[] _btUv = new UvRect[SettingsPoolSize];
    private bool     _buffTabLoaded = false;

    // ── Lazy loaders (idempotent: called from VirtualListElement Count func) ──

    private void EnsureSkillTabLoaded()
    {
        if (_stCount > 0) return;
        LoadSettingsSkillTable();
        if (_stCount > 0) ApplySkillTabFilter(_stFilter);
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
            var t = SkillCDPatch.FindType("Bokura.SkillTableBase") ?? SkillCDPatch.FindType("SkillTableBase");
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
            _stIds = ids.ToArray(); _stNames = names.ToArray(); _stDescs = descs.ToArray(); _stCount = ids.Count;
            _services.Log.Info($"[Settings] loaded {_stCount} skills");
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
            var t = SkillCDPatch.FindType("Bokura.BuffTableBase") ?? SkillCDPatch.FindType("BuffTableBase");
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

            var dIds  = new List<int>(); var dNames = new List<string>();
            var bIds  = new List<int>(); var bNames = new List<string>();
            PropertyInfo? piKvp = null, piId = null, piName = null, piIcon = null, piType = null, piDesc = null;
            var dDescs = new List<string>(); var bDescs = new List<string>();

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
                if (buffType == 0) { dIds.Add(id); dNames.Add(name); dDescs.Add(desc); }
                else               { bIds.Add(id); bNames.Add(name); bDescs.Add(desc); }
            }
            _dtIds = dIds.ToArray(); _dtNames = dNames.ToArray(); _dtDescs = dDescs.ToArray(); _dtCount = dIds.Count;
            _btIds = bIds.ToArray(); _btNames = bNames.ToArray(); _btDescs = bDescs.ToArray(); _btCount = bIds.Count;
            _services.Log.Info($"[Settings] loaded {_dtCount} debuffs, {_btCount} buffs");
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
        SettingsFilterTable(text, _dtIds, _dtNames, _dtDescs, _dtCount,
            _selection.IsDebuffTracked, out _dtFiltIds, out _dtFiltNames, out _dtFiltDescs, out _dtFiltCount);
    }

    private void ApplyBuffTabFilter(string text)
    {
        _btFilter = text; _btOffset = 0;
        SettingsFilterTable(text, _btIds, _btNames, _btDescs, _btCount,
            _selection.IsBuffTracked, out _btFiltIds, out _btFiltNames, out _btFiltDescs, out _btFiltCount);
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
}
