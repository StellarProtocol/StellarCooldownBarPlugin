using System;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

internal static partial class BuffTrackPatch
{
    // ── Show-hidden: full (unfiltered) buff list source ──────────────────────────
    // When ShowHidden is on the bar reads the FULL server buff list (pBuffList_ / buffList_) instead of the game's
    // display-filtered "showed" list (pShowedBuffList_ / showedBuffList_), so icon-less / internal buffs appear.
    // Both PropertyInfos resolve once (the showed one via ResolveShowedList in BuffTrackPatch.cs, the full one here);
    // RefreshActiveBuffs picks between them at read-time from this flag. Set from Plugin whenever config changes.
    internal static bool ShowHidden;

    private static PropertyInfo? _piFullBuffList;
    private static bool          _fullListResolved;

    // Prefer the FULL unfiltered lists (mirrors TargetLens' ResolveShowedList priority), showed lists only as fallback.
    private static void ResolveFullBuffList(object comp)
    {
        _fullListResolved = true;
        PropertyInfo? best = null; int bestPrio = 99;
        foreach (var p in comp.GetType().GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            int prio = p.Name switch { "pBuffList_" => 0, "buffList_" => 1, "pShowedBuffList_" => 2, "showedBuffList_" => 3, _ => 99 };
            if (prio < bestPrio) { best = p; bestPrio = prio; if (bestPrio == 0) break; }
        }
        _piFullBuffList = best;
    }

    // The server buff list to iterate this refresh: full (unfiltered) when ShowHidden, else the display-filtered list.
    private static object? GetActiveServerList(object comp)
    {
        if (ShowHidden)
        {
            if (!_fullListResolved) ResolveFullBuffList(comp);
            return _piFullBuffList?.GetValue(comp);
        }
        if (!_showedListResolved) ResolveShowedList();
        return _piShowedBuffList?.GetValue(comp);
    }

    // Reset the show-hidden list caches on Uninstall (called from BuffTrackPatch.cs Uninstall).
    private static void ResetShowHiddenCaches()
    {
        _piFullBuffList = null; _fullListResolved = false;
    }

    private static MethodInfo?   _miGetBuffTable;
    private static object?       _buffTableInst;
    private static MethodInfo?   _miGetBuffRow;
    private static PropertyInfo? _piBuffRowName;
    private static PropertyInfo? _piBuffRowVisible;
    private static PropertyInfo? _piBuffRowType;
    private static PropertyInfo? _piBuffRowSkillId;
    private static bool          _tableReflResolved;

    private static object?       _skillTableInst;
    private static MethodInfo?   _miGetSkillRow;
    private static PropertyInfo? _piSkillRowName;
    private static bool          _skillTableResolved;

    private static void GetBuffInfo(int baseId, out string name, out int? visible, out int? buffType, out int skillId)
    {
        name = ""; visible = null; buffType = null; skillId = 0;
        if (!_tableReflResolved) ResolveBuffTable();
        if (_miGetBuffRow == null || _buffTableInst == null) return;
        try
        {
            var row = _miGetBuffRow.Invoke(_buffTableInst, new object[] { (object)baseId });
            if (row == null) return;
            var t = row.GetType();
            _piBuffRowName    ??= t.GetProperty("Name",     BindingFlags.Public | BindingFlags.Instance);
            _piBuffRowVisible ??= t.GetProperty("Visible",  BindingFlags.Public | BindingFlags.Instance);
            _piBuffRowType    ??= t.GetProperty("BuffType", BindingFlags.Public | BindingFlags.Instance);
            _piBuffRowSkillId ??= t.GetProperty("SkillId",  BindingFlags.Public | BindingFlags.Instance);
            // Resolve to the translated display label (game Name for real buffs, NameDesign for hidden / placeholder-
            // named ones) so a hidden buff's BuffName is never empty — the bar's empty-name render gate then passes.
            string gameName = (string?)(_piBuffRowName?.GetValue(row)) ?? "";
            name     = TranslatedBuffText.ResolveLabel(baseId, gameName);
            visible  = (int?)(_piBuffRowVisible?.GetValue(row));
            buffType = (int?)(_piBuffRowType?.GetValue(row));
            skillId  = (int?)(_piBuffRowSkillId?.GetValue(row)) ?? 0;
        }
        catch { }
    }

    private static void ResolveBuffTable()
    {
        _tableReflResolved = true;
        var t = StellarInterop.FindType("Bokura.BuffTableBase") ?? StellarInterop.FindType("BuffTableBase");
        if (t == null) return;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "GetTable") continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(bool)) { _miGetBuffTable = m; break; }
        }
        if (_miGetBuffTable == null) return;
        try { _buffTableInst = _miGetBuffTable.Invoke(null, new object[] { false }); }
        catch (Exception ex) { _log?.Invoke($"[Buff] GetTable threw: {ex.InnerException?.Message ?? ex.Message}"); return; }
        if (_buffTableInst == null) return;
        foreach (var m in _buffTableInst.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "get_Item") continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) { _miGetBuffRow = m; break; }
        }
    }

    private static string GetSkillName(int skillId)
    {
        if (skillId <= 0) return "";
        if (!_skillTableResolved) ResolveSkillTable();
        if (_miGetSkillRow == null || _skillTableInst == null) return "";
        try
        {
            var row = _miGetSkillRow.Invoke(_skillTableInst, new object[] { (object)skillId });
            if (row == null) return "";
            _piSkillRowName ??= row.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            return (string?)(_piSkillRowName?.GetValue(row)) ?? "";
        }
        catch { return ""; }
    }

    private static void ResolveSkillTable()
    {
        _skillTableResolved = true;
        var t = StellarInterop.FindType("Bokura.SkillTableBase") ?? StellarInterop.FindType("SkillTableBase");
        if (t == null) return;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "GetTable") continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
            { try { _skillTableInst = m.Invoke(null, new object[] { false }); } catch { } break; }
        }
        if (_skillTableInst == null) return;
        foreach (var m in _skillTableInst.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "get_Item") continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) { _miGetSkillRow = m; break; }
        }
    }
}
