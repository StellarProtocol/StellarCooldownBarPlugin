using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Stellar.CooldownBar;

public sealed class SkillCdEntry
{
    public int    SkillId   { get; }
    public string SkillName { get; internal set; } = "";
    public string CdKey     { get; internal set; } = "";
    public float  TotalSec       { get; internal set; }
    public int    PendingCharges { get; internal set; }
    public int    MaxCharges     { get; internal set; }

    internal SkillCdEntry(int skillId) { SkillId = skillId; }
}

internal static class SkillCDPatch
{
    public static IReadOnlyDictionary<int, SkillCdEntry> ActiveCDs => _activeCDs;

    private static readonly Dictionary<int, SkillCdEntry> _activeCDs = new();
    private static Harmony?        _harmony;
    private static Action<string>? _log;

    private static MethodInfo?   _miGetSkillId;
    private static PropertyInfo? _piDuration;
    private static PropertyInfo? _piCdKey;
    private static PropertyInfo? _piSkillRow;
    private static PropertyInfo? _piSkillName;

    private static MethodInfo?   _miTryGetCDData;
    private static PropertyInfo? _piProgress;
    private static PropertyInfo? _piCdRealLen;
    private static PropertyInfo? _piCdCurLayer;
    private static PropertyInfo? _piCdMaxLayer;
    private static bool          _liveQueryReady;

    private static bool _loggedError;

    internal static bool Install(string harmonyId, Action<string> log)
    {
        _log     = log;
        _harmony = new Harmony(harmonyId + ".skillcd");

        var scdType = FindType("Panda.ZGame.SkillControlData");
        if (scdType == null)
        {
            log("[SkillCD] SkillControlData not found — patch skipped");
            return false;
        }

        int patched = 0;
        foreach (var m in scdType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name == "OnCDBegin" && m.GetParameters().Length == 1)
            {
                try
                {
                    _harmony.Patch(m, postfix: new HarmonyMethod(typeof(SkillCDPatch), nameof(PostfixOnCDBegin)));
                    log("[SkillCD] OnCDBegin postfix patched");
                    patched++;
                }
                catch (Exception ex) { log($"[SkillCD] OnCDBegin patch failed: {ex.Message}"); }
            }
            else if (m.Name == "OnSkillCDLayerChanged" && m.GetParameters().Length == 1)
            {
                try
                {
                    _harmony.Patch(m, postfix: new HarmonyMethod(typeof(SkillCDPatch), nameof(PostfixOnSkillCDLayerChanged)));
                    log("[SkillCD] OnSkillCDLayerChanged postfix patched");
                    patched++;
                }
                catch (Exception ex) { log($"[SkillCD] OnSkillCDLayerChanged patch failed: {ex.Message}"); }
            }

            if (patched == 2) break;
        }

        if (patched == 0) { _harmony.UnpatchSelf(); _harmony = null; return false; }
        return true;
    }

    internal static void Uninstall()
    {
        _activeCDs.Clear();
        _harmony?.UnpatchSelf();
        _harmony          = null;
        _miGetSkillId     = null;
        _piDuration       = null;
        _piCdKey          = null;
        _piSkillRow       = null;
        _piSkillName      = null;
        _miTryGetCDData   = null;
        _piProgress       = null;
        _piCdRealLen      = null;
        _liveQueryReady   = false;
        _loggedError      = false;
    }

    internal static float GetRemainSec(SkillCdEntry entry)
    {
        if (!_liveQueryReady) ResolveLiveQuery();
        if (_miTryGetCDData == null || string.IsNullOrEmpty(entry.CdKey)) return -1f;
        try
        {
            var args = new object?[] { entry.CdKey, null };
            if (!(bool)_miTryGetCDData.Invoke(null, args)! || args[1] == null) return -1f;

            var   cd      = args[1];
            float prog    = Convert.ToSingle(_piProgress?.GetValue(cd)  ?? 0f);
            int   cur     = Math.Max(0, Convert.ToInt32(_piCdCurLayer?.GetValue(cd)  ?? 0));
            int   max     = Math.Max(1, Convert.ToInt32(_piCdMaxLayer?.GetValue(cd) ?? 1));
            entry.PendingCharges = max - cur;
            entry.MaxCharges     = max;
            float remain  = entry.TotalSec * (1f + cur - max * prog);
            return remain <= 0f ? 0f : remain;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[SkillCD] GetRemainSec ex: {ex.InnerException?.Message ?? ex.Message}");
            return -1f;
        }
    }

    private static void PostfixOnCDBegin(object __instance, object __0)
    {
        try
        {
            _miGetSkillId ??= __instance.GetType().GetMethod("get_SkillId", BindingFlags.Public | BindingFlags.Instance);
            _piDuration   ??= __0.GetType().GetProperty("Duration", BindingFlags.Public | BindingFlags.Instance);
            _piCdKey      ??= __instance.GetType().GetProperty("CdKey",     BindingFlags.Public | BindingFlags.Instance);
            _piSkillRow   ??= __instance.GetType().GetProperty("SkillRow",  BindingFlags.Public | BindingFlags.Instance);

            if (_miGetSkillId == null || _piDuration == null) return;

            int   skillId    = (int)_miGetSkillId.Invoke(__instance, null)!;
            int   durationMs = (int)(_piDuration.GetValue(__0) ?? 0);
            if (durationMs <= 0) return;

            string cdKey     = (string?)(_piCdKey?.GetValue(__instance)) ?? "";
            string skillName = "";
            var skillRow     = _piSkillRow?.GetValue(__instance);
            if (skillRow != null)
            {
                _piSkillName ??= skillRow.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                skillName = (string?)(_piSkillName?.GetValue(skillRow)) ?? "";
            }

            var entry      = new SkillCdEntry(skillId);
            entry.TotalSec = durationMs / 1000f;
            entry.CdKey    = cdKey;
            entry.SkillName = skillName;
            _activeCDs[skillId] = entry;
        }
        catch (Exception ex)
        {
            if (_loggedError) return;
            _loggedError = true;
            _log?.Invoke($"[SkillCD] PostfixOnCDBegin error: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void PostfixOnSkillCDLayerChanged(object __instance, object __0)
    {
        try
        {
            _miGetSkillId ??= __instance.GetType().GetMethod("get_SkillId", BindingFlags.Public | BindingFlags.Instance);
            if (_miGetSkillId == null) return;

            var cdType = __0.GetType();
            _piProgress   ??= cdType.GetProperty("Progress",   BindingFlags.Public | BindingFlags.Instance);
            _piCdCurLayer ??= cdType.GetProperty("CdCurLayer", BindingFlags.Public | BindingFlags.Instance);
            _piCdMaxLayer ??= cdType.GetProperty("CdMaxLayer", BindingFlags.Public | BindingFlags.Instance);
            if (_piProgress == null) return;

            int   skillId  = (int)_miGetSkillId.Invoke(__instance, null)!;
            float progress = Convert.ToSingle(_piProgress.GetValue(__0)!);
            int   cur      = Math.Max(0, Convert.ToInt32(_piCdCurLayer?.GetValue(__0) ?? 0));
            int   max      = Math.Max(1, Convert.ToInt32(_piCdMaxLayer?.GetValue(__0) ?? 1));
            if (progress >= 1f && (max == 1 || cur >= max))
                _activeCDs.Remove(skillId);
        }
        catch (Exception ex)
        {
            if (_loggedError) return;
            _loggedError = true;
            _log?.Invoke($"[SkillCD] PostfixOnSkillCDLayerChanged error: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void ResolveLiveQuery()
    {
        _liveQueryReady = true;
        var t = FindType("Panda.ZGame.ZBattleUtils");
        if (t == null) { _log?.Invoke("[SkillCD] ZBattleUtils not found"); return; }

        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "TryGetCDData") continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string)) { _miTryGetCDData = m; break; }
        }

        if (_miTryGetCDData == null) { _log?.Invoke("[SkillCD] TryGetCDData not found"); return; }

        var cdDataType = _miTryGetCDData.GetParameters()[1].ParameterType.GetElementType();
        if (cdDataType != null)
        {
            _piCdRealLen  = cdDataType.GetProperty("CdRealLen",  BindingFlags.Public | BindingFlags.Instance);
            _piCdCurLayer = cdDataType.GetProperty("CdCurLayer", BindingFlags.Public | BindingFlags.Instance);
            _piCdMaxLayer = cdDataType.GetProperty("CdMaxLayer", BindingFlags.Public | BindingFlags.Instance);
            _piProgress ??= cdDataType.GetProperty("Progress", BindingFlags.Public | BindingFlags.Instance);
        }
        _log?.Invoke($"[SkillCD] live query ready: TryGetCDData={_miTryGetCDData != null}");
    }

    internal static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t is not null) return t;
        }
        return null;
    }
}
