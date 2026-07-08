using System;
using System.Reflection;

namespace Stellar.CooldownBar;

internal static partial class BuffTrackPatch
{
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
            name     = (string?)(_piBuffRowName?.GetValue(row)) ?? "";
            visible  = (int?)(_piBuffRowVisible?.GetValue(row));
            buffType = (int?)(_piBuffRowType?.GetValue(row));
            skillId  = (int?)(_piBuffRowSkillId?.GetValue(row)) ?? 0;
        }
        catch { }
    }

    private static void ResolveBuffTable()
    {
        _tableReflResolved = true;
        var t = SkillCDPatch.FindType("Bokura.BuffTableBase") ?? SkillCDPatch.FindType("BuffTableBase");
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
        var t = SkillCDPatch.FindType("Bokura.SkillTableBase") ?? SkillCDPatch.FindType("SkillTableBase");
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
