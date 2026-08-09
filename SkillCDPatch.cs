using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

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
    private static Action<string>? _log;

    // Demand gate — matches BuffTrackPatch. The OnCDBegin/OnSkillCDLayerChanged postfixes and the per-frame
    // GetRemainSec query only matter while the bar is refreshing; skip the postfixes otherwise. The plugin's
    // OnUpdate calls MarkDemand() each throttled refresh while the bar is shown.
    private const  long DemandWindowMs = 500;
    private static long _lastDemandTick;
    private static bool Active => _lastDemandTick != 0 && Environment.TickCount64 - _lastDemandTick < DemandWindowMs;
    internal static void MarkDemand() => _lastDemandTick = Environment.TickCount64;

    // Reused arg array for the per-CD ZBattleUtils.TryGetCDData query (avoids a new object?[2] per active CD per refresh).
    private static readonly object?[] _cdArgs = new object?[2];

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

    internal static bool Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        var scdType = StellarInterop.FindType("Panda.ZGame.SkillControlData");
        if (scdType == null)
        {
            log("[SkillCD] SkillControlData not found — patch skipped");
            return false;
        }

        var mBegin = StellarInterop.FindMethod(scdType, "OnCDBegin", 1);
        var mLayer = StellarInterop.FindMethod(scdType, "OnSkillCDLayerChanged", 1);

        int patched = 0;
        if (mBegin != null)
        {
            try
            {
                harmony.Patch(mBegin, postfix: new HarmonyMethod(typeof(SkillCDPatch), nameof(PostfixOnCDBegin)));
                log("[SkillCD] OnCDBegin postfix patched");
                patched++;
            }
            catch (Exception ex) { log($"[SkillCD] OnCDBegin patch failed: {ex.Message}"); }
        }
        if (mLayer != null)
        {
            try
            {
                harmony.Patch(mLayer, postfix: new HarmonyMethod(typeof(SkillCDPatch), nameof(PostfixOnSkillCDLayerChanged)));
                log("[SkillCD] OnSkillCDLayerChanged postfix patched");
                patched++;
            }
            catch (Exception ex) { log($"[SkillCD] OnSkillCDLayerChanged patch failed: {ex.Message}"); }
        }

        // Nothing patched → nothing to tear down (the framework owns the Harmony instance and auto-unpatches
        // on plugin dispose; we no longer call UnpatchSelf).
        return patched != 0;
    }

    internal static void Uninstall()
    {
        // Harmony teardown is owned by IHarmonyHost, which auto-unpatches every instance on plugin dispose —
        // do NOT unpatch here (that would double-unpatch). Only reset our own transient reflection state.
        _activeCDs.Clear();
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
        _lastDemandTick   = 0;
    }

    internal static float GetRemainSec(SkillCdEntry entry)
    {
        if (!_liveQueryReady) ResolveLiveQuery();
        if (_miTryGetCDData == null || string.IsNullOrEmpty(entry.CdKey)) return -1f;
        try
        {
            var args = _cdArgs; args[0] = entry.CdKey; args[1] = null;   // reused arg array
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
        if (!Active) return;
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
        if (!Active) return;
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
        var t = StellarInterop.FindType("Panda.ZGame.ZBattleUtils");
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
}
