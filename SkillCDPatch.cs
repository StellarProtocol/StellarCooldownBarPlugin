using System;
using System.Collections.Generic;
using System.Linq.Expressions;
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

    // Demand gate — matches BuffTrackPatch. The OnCDBegin/OnSkillCDLayerChanged postfixes and the per-read
    // GetRemainSec query only matter while the bar is refreshing; skip the postfixes otherwise. The plugin's
    // OnUpdate calls MarkDemand() each throttled refresh while the bar is shown.
    private const  long DemandWindowMs = 500;
    private static long _lastDemandTick;
    private static bool Active => _lastDemandTick != 0 && Environment.TickCount64 - _lastDemandTick < DemandWindowMs;
    internal static void MarkDemand() => _lastDemandTick = Environment.TickCount64;

    private const BindingFlags PubInst = BindingFlags.Public | BindingFlags.Instance;

    // All member reads are delegates bound once by Getters (compiled; reflection fallback) — no GetValue / Invoke /
    // Convert boxing per call (Plugin-Performance-Rules.md §2).
    // OnCDBegin: SkillControlData.SkillId / CdKey / SkillRow(.Name) + the CD arg's Duration.
    private static Func<object, long>?    _getSkillId, _getDuration;
    private static Func<object, object?>? _getCdKey, _getSkillRow, _getSkillName;
    private static bool                   _beginResolved;

    // OnSkillCDLayerChanged arg getters — bound per arg TYPE (it is CDData in practice, but this path never assumed so).
    private static Type?                 _layerArgType;
    private static Func<object, double>? _lProgress;
    private static Func<object, long>?   _lCur, _lMax;

    // ZBattleUtils.TryGetCDData(string, out CDData) compiled into key → CDData-or-null (the out param is a local of the
    // compiled lambda, so no object[] / boxing). Fallback: Invoke with the reused _cdArgs array.
    private static Func<string, object?>? _tryGetCd;
    private static readonly object?[]     _cdArgs = new object?[2];
    private static Func<object, double>?  _cdProgress;
    private static Func<object, long>?    _cdCur, _cdMax;
    private static bool                   _liveQueryReady;

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
        _getSkillId = _getDuration = null;
        _getCdKey = _getSkillRow = _getSkillName = null;
        _beginResolved = false;
        _layerArgType = null; _lProgress = null; _lCur = _lMax = null;
        _tryGetCd = null; _cdArgs[0] = _cdArgs[1] = null;
        _cdProgress = null; _cdCur = _cdMax = null;
        _liveQueryReady = false;
        _loggedError    = false;
        _lastDemandTick = 0;
    }

    internal static float GetRemainSec(SkillCdEntry entry)
    {
        if (!_liveQueryReady) ResolveLiveQuery();
        if (_tryGetCd == null || string.IsNullOrEmpty(entry.CdKey)) return -1f;
        try
        {
            var cd = _tryGetCd(entry.CdKey);
            if (cd == null) return -1f;

            float prog    = _cdProgress != null ? (float)_cdProgress(cd) : 0f;
            int   cur     = Math.Max(0, _cdCur != null ? (int)_cdCur(cd) : 0);
            int   max     = Math.Max(1, _cdMax != null ? (int)_cdMax(cd) : 1);
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
        using var _perf = CdPerf.Hook();   // [CdPerf] postfix bucket — default struct unless the probe is on
        if (!Active) return;
        CdPerf.HookFull();
        try
        {
            if (!_beginResolved)
            {
                _beginResolved = true;
                int b0 = Getters.Bound, f0 = Getters.Fallback;
                var it = __instance.GetType();
                _getSkillId  = Getters.Long(it.GetMethod("get_SkillId", PubInst));
                _getCdKey    = Getters.Obj(it.GetProperty("CdKey",    PubInst));
                _getSkillRow = Getters.Obj(it.GetProperty("SkillRow", PubInst));
                _getDuration = Getters.Long(__0.GetType().GetProperty("Duration", PubInst));
                LogBindPath("OnCDBegin", b0, f0);
            }
            if (_getSkillId == null || _getDuration == null) return;

            int   skillId    = (int)_getSkillId(__instance);
            int   durationMs = (int)_getDuration(__0);
            if (durationMs <= 0) return;

            string cdKey     = (string?)_getCdKey?.Invoke(__instance) ?? "";
            string skillName = "";
            var skillRow     = _getSkillRow?.Invoke(__instance);
            if (skillRow != null)
            {
                _getSkillName ??= Getters.Obj(skillRow.GetType().GetProperty("Name", PubInst));
                skillName = (string?)_getSkillName?.Invoke(skillRow) ?? "";
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
        using var _perf = CdPerf.Hook();   // [CdPerf] postfix bucket — default struct unless the probe is on
        if (!Active) return;
        CdPerf.HookFull();
        try
        {
            if (!_beginResolved && _getSkillId == null)
                _getSkillId = Getters.Long(__instance.GetType().GetMethod("get_SkillId", PubInst));
            if (_getSkillId == null) return;

            var cdType = __0.GetType();
            if (cdType != _layerArgType)
            {
                _layerArgType = cdType;
                _lProgress = Getters.Double(cdType.GetProperty("Progress",   PubInst));
                _lCur      = Getters.Long(cdType.GetProperty("CdCurLayer",   PubInst));
                _lMax      = Getters.Long(cdType.GetProperty("CdMaxLayer",   PubInst));
            }
            if (_lProgress == null) return;

            int   skillId  = (int)_getSkillId(__instance);
            float progress = (float)_lProgress(__0);
            int   cur      = Math.Max(0, _lCur != null ? (int)_lCur(__0) : 0);
            int   max      = Math.Max(1, _lMax != null ? (int)_lMax(__0) : 1);
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

        MethodInfo? mi = null;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "TryGetCDData") continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string)) { mi = m; break; }
        }
        if (mi == null) { _log?.Invoke("[SkillCD] TryGetCDData not found"); return; }

        var cdDataType = mi.GetParameters()[1].ParameterType.GetElementType();
        int b0 = Getters.Bound, f0 = Getters.Fallback;
        _tryGetCd = BindTryGetCd(mi, cdDataType);
        if (cdDataType != null)
        {
            _cdProgress = Getters.Double(cdDataType.GetProperty("Progress",   PubInst));
            _cdCur      = Getters.Long(cdDataType.GetProperty("CdCurLayer",   PubInst));
            _cdMax      = Getters.Long(cdDataType.GetProperty("CdMaxLayer",   PubInst));
        }
        _log?.Invoke($"[SkillCD] live query ready: TryGetCDData={_tryGetCd != null}");
        LogBindPath("CDData/TryGetCDData", b0, f0);
    }

    // key → CDData-or-null. The compiled form declares the out CDData as a block local, so a call allocates nothing
    // beyond the returned proxy wrapper. If expression compile is unavailable, fall back to the reused-array Invoke.
    private static Func<string, object?> BindTryGetCd(MethodInfo mi, Type? cdDataType)
    {
        if (cdDataType != null)
        {
            try
            {
                var key = Expression.Parameter(typeof(string), "key");
                var cd  = Expression.Variable(cdDataType, "cd");
                var body = Expression.Block(typeof(object), new[] { cd },
                    Expression.Condition(Expression.Call(mi, key, cd),
                        Expression.Convert(cd, typeof(object)),
                        Expression.Constant(null, typeof(object))));
                var f = Expression.Lambda<Func<string, object?>>(body, key).Compile();
                Getters.Bound++;
                return f;
            }
            catch (Exception ex) { _log?.Invoke($"[SkillCD] TryGetCDData bind failed, using Invoke: {ex.Message}"); }
        }
        Getters.Fallback++;
        return k =>
        {
            var args = _cdArgs; args[0] = k; args[1] = null;   // reused arg array
            return (bool)mi.Invoke(null, args)! ? args[1] : null;
        };
    }

    private static void LogBindPath(string what, int bound0, int fallback0) =>
        _log?.Invoke($"[SkillCD] {what} getters: {Getters.Bound - bound0} compiled, {Getters.Fallback - fallback0} reflection fallback");
}
