using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Stellar.CooldownBar;

public sealed class BuffTrackEntry
{
    public int    BuffUuid     { get; }
    public int    BuffBaseId   { get; internal set; }
    public string BuffName     { get; internal set; } = "";
    public int?   Visible      { get; internal set; }
    public int?   BuffType     { get; internal set; }   // 0=Debuff 1=Gain 2=GainRecovery 3=Item
    public bool   IsClientBuff { get; internal set; }
    public int    Layer        { get; internal set; }
    public int    Level        { get; internal set; }
    public int    SkillId      { get; internal set; }
    public string SkillName    { get; internal set; } = "";
    public long   Duration     { get; internal set; }   // ms; 0 = permanent

    internal long  CreateTime;   // game time (ms) at (re)application; changes when a stack refreshes the buff
    internal long  SnapTick;
    internal float SnapRemain;

    public float RemainSec => Duration == 0 ? -1f
        : MathF.Max(0f, SnapRemain - (Environment.TickCount64 - SnapTick) / 1000f);

    internal BuffTrackEntry(int buffUuid) { BuffUuid = buffUuid; }
}

internal static partial class BuffTrackPatch
{
    public static IReadOnlyDictionary<int, BuffTrackEntry> ActiveBuffs => _activeBuffs;

    private static readonly Dictionary<int, BuffTrackEntry> _activeBuffs = new();
    private static Harmony?        _harmony;
    private static Action<string>? _log;

    // ── Demand gate ──
    // The postfixes fire for EVERY entity's buff add/sync and box longs via reflection before the local-player
    // check. Skip them entirely (one comparison, zero alloc) unless the bar is actively refreshing. The plugin's
    // OnUpdate calls MarkDemand() on each throttled refresh while the bar is shown.
    private const  long DemandWindowMs = 500;
    private static long _lastDemandTick;
    private static bool Active => _lastDemandTick != 0 && Environment.TickCount64 - _lastDemandTick < DemandWindowMs;
    internal static void MarkDemand() => _lastDemandTick = Environment.TickCount64;

    // Reused across RefreshActiveBuffs calls so the ~20Hz refresh doesn't churn a fresh HashSet+List each time.
    private static readonly HashSet<int> _live  = new();
    private static readonly List<int>    _stale = new();
    // Reused arg array for IterList's indexer GetValue (avoids a new object[1] per buff item per refresh).
    private static readonly object[] _idxArg = new object[1];

    // Player uuid is stable within a session; cache it to avoid a reflection Invoke + boxed long per buff event.
    private static long _cachedPlayerUuid;
    private static long _playerUuidTick;

    private static object? _localBuffComp;
    private static object? _localClientBuffComp;

    private static PropertyInfo? _piShowedBuffList;
    private static bool          _showedListResolved;

    private static PropertyInfo? _piBuffItemUuid;
    private static PropertyInfo? _piBuffItemBaseId;
    private static PropertyInfo? _piBuffItemLayer;
    private static PropertyInfo? _piBuffItemLevel;
    private static PropertyInfo? _piBuffItemDuration;
    private static PropertyInfo? _piBuffItemCreateTime;
    private static bool          _buffItemFieldsResolved;

    private static PropertyInfo? _piClientBuffList;
    private static bool          _clientListResolved;

    private static PropertyInfo? _piClientBuffUuid;
    private static PropertyInfo? _piClientBuffId;
    private static PropertyInfo? _piClientBuffMaxLife;
    private static PropertyInfo? _piClientBuffLayer;
    private static PropertyInfo? _piClientBuffCreateTime;
    private static FieldInfo?    _fiIsInValid;
    private static bool          _clientBuffInfoResolved;

    private static PropertyInfo? _piZComponentHost;
    private static PropertyInfo? _piZEntityUuid;
    private static bool          _hostReflResolved;

    private static object?     _entityMgrInst;
    private static MethodInfo? _miGetPlayerUuid;
    private static bool        _entityMgrResolved;

    private static readonly Dictionary<int, int> _buffSourceSkillId = new();
    private static PropertyInfo? _piFightSourceInfo;
    private static PropertyInfo? _piFightSourceType;
    private static PropertyInfo? _piFightSourceConfigId;
    private static bool          _fightSourceResolved;

    private static bool _loggedError;
    private static bool _diagLogged;

    internal static bool Install(string harmonyId, Action<string> log)
    {
        _log     = log;
        _harmony = new Harmony(harmonyId + ".buff");

        var buffCompType = SkillCDPatch.FindType("Panda.ZGame.BuffComp");
        if (buffCompType == null)
        {
            log("[Buff] BuffComp not found — patch skipped");
            return false;
        }

        bool patchedAdd = false;
        foreach (var m in buffCompType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var ps = m.GetParameters();
            if ((m.Name == "OnAddBuff" || m.Name == "OnBuffSync") && ps.Length == 2)
            {
                try
                {
                    _harmony.Patch(m, postfix: new HarmonyMethod(typeof(BuffTrackPatch), nameof(PostfixOnAddBuff)));
                    if (m.Name == "OnAddBuff") { log("[Buff] OnAddBuff patched"); patchedAdd = true; }
                    else log("[Buff] OnBuffSync patched");
                }
                catch (Exception ex) { log($"[Buff] {m.Name} patch failed: {ex.Message}"); }
            }
        }
        if (!patchedAdd) { _harmony.UnpatchSelf(); _harmony = null; return false; }

        var clientCompType = SkillCDPatch.FindType("Panda.ZGame.ClientBuffComp");
        if (clientCompType != null)
        {
            foreach (var m in clientCompType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "AddBuff" && m.GetParameters().Length == 4)
                {
                    try
                    {
                        _harmony.Patch(m, postfix: new HarmonyMethod(typeof(BuffTrackPatch), nameof(PostfixClientAddBuff)));
                        log("[Buff] ClientBuffComp.AddBuff postfix patched");
                    }
                    catch (Exception ex) { log($"[Buff] ClientBuffComp.AddBuff patch failed: {ex.Message}"); }
                    break;
                }
            }
        }
        return true;
    }

    internal static void Uninstall()
    {
        _activeBuffs.Clear();
        _harmony?.UnpatchSelf();
        _harmony = null;
        _localBuffComp = _localClientBuffComp = null;
        _piShowedBuffList = null; _showedListResolved = false;
        _piBuffItemUuid = _piBuffItemBaseId = _piBuffItemLayer = _piBuffItemLevel = _piBuffItemDuration = null;
        _piBuffItemCreateTime = null;
        _buffItemFieldsResolved = false;
        _piClientBuffList = null; _clientListResolved = false;
        _piClientBuffUuid = _piClientBuffId = _piClientBuffMaxLife = _piClientBuffLayer = null;
        _piClientBuffCreateTime = null;
        _fiIsInValid = null; _clientBuffInfoResolved = false;
        _piZComponentHost = _piZEntityUuid = null; _hostReflResolved = false;
        _entityMgrInst = null; _miGetPlayerUuid = null; _entityMgrResolved = false;
        _buffSourceSkillId.Clear();
        _piFightSourceInfo = _piFightSourceType = _piFightSourceConfigId = null;
        _fightSourceResolved = false;
        _tableReflResolved = false;
        _miGetBuffTable = _miGetBuffRow = null; _buffTableInst = null;
        _piBuffRowName = _piBuffRowVisible = _piBuffRowType = _piBuffRowSkillId = null;
        _skillTableResolved = false;
        _skillTableInst = null; _miGetSkillRow = null; _piSkillRowName = null;
        _loggedError = _diagLogged = false;
        _lastDemandTick = 0; _cachedPlayerUuid = 0; _playerUuidTick = 0;
        _live.Clear(); _stale.Clear();
    }

    internal static void RefreshActiveBuffs()
    {
        if (!_diagLogged) { _diagLogged = true; _log?.Invoke($"[Buff] Refresh: buffComp={_localBuffComp != null} clientComp={_localClientBuffComp != null}"); }
        var live = _live; live.Clear();

        if (_localBuffComp != null)
        {
            if (!_showedListResolved) ResolveShowedList();
            var list = _piShowedBuffList?.GetValue(_localBuffComp);
            if (list != null)
            {
                try
                {
                    foreach (var item in IterList(list))
                    {
                        if (item == null) continue;
                        if (!_buffItemFieldsResolved) ResolveBuffItemFields(item);
                        if (_piBuffItemUuid == null) break;
                        int uuid = (int)(_piBuffItemUuid.GetValue(item) ?? 0);
                        if (uuid == 0) continue;
                        live.Add(uuid);
                        int baseId = (int)(_piBuffItemBaseId?.GetValue(item)   ?? 0);
                        int layer  = (int)(_piBuffItemLayer?.GetValue(item)    ?? 1);
                        int level  = (int)(_piBuffItemLevel?.GetValue(item)    ?? 0);
                        long durMs = (long)(_piBuffItemDuration?.GetValue(item) ?? 0L);
                        long createMs = (long)(_piBuffItemCreateTime?.GetValue(item) ?? 0L);
                        UpsertServerBuff(uuid, baseId, layer, level, durMs, createMs);
                    }
                }
                catch (Exception ex) { LogError("server list", ex); }
            }
        }

        if (_localClientBuffComp != null)
        {
            if (!_clientListResolved) ResolveClientList();
            var list = _piClientBuffList?.GetValue(_localClientBuffComp);
            if (list != null)
            {
                try
                {
                    foreach (var item in IterList(list))
                    {
                        if (item == null) continue;
                        if (!_clientBuffInfoResolved) ResolveClientBuffInfoRefl(item);
                        if (_piClientBuffUuid == null) break;
                        if (_fiIsInValid != null && (bool)(_fiIsInValid.GetValue(item) ?? false)) continue;
                        int uuid   = (int)(_piClientBuffUuid.GetValue(item)    ?? 0);
                        if (uuid == 0) continue;
                        live.Add(uuid);
                        int buffId = (int)(_piClientBuffId?.GetValue(item)     ?? 0);
                        int layer  = (int)(_piClientBuffLayer?.GetValue(item)  ?? 1);
                        float maxL = (float)(_piClientBuffMaxLife?.GetValue(item) ?? 0f);
                        long createMs = (long)(double)(_piClientBuffCreateTime?.GetValue(item) ?? 0d);
                        UpsertClientBuff(uuid, buffId, layer, maxL, createMs);
                    }
                }
                catch (Exception ex) { LogError("client list", ex); }
            }
        }

        var stale = _stale; stale.Clear();
        foreach (var kv in _activeBuffs)
            if (!live.Contains(kv.Key)) stale.Add(kv.Key);
        foreach (var k in stale) { _activeBuffs.Remove(k); _buffSourceSkillId.Remove(k); }
    }

    private static void UpsertServerBuff(int uuid, int baseId, int layer, int level, long durMs, long createMs)
    {
        bool isNew = !_activeBuffs.TryGetValue(uuid, out var entry);
        if (isNew)
        {
            entry = new BuffTrackEntry(uuid);
            _activeBuffs[uuid] = entry;
            GetBuffInfo(baseId, out var nm, out var vis, out var bt, out var sid);
            if (sid == 0) _buffSourceSkillId.TryGetValue(uuid, out sid);
            entry.BuffBaseId = baseId; entry.BuffName = nm;
            entry.Visible = vis; entry.BuffType = bt;
            entry.SkillId = sid; entry.SkillName = GetSkillName(sid);
        }
        entry!.Layer = layer; entry.Level = level;
        // Re-snap remaining whenever the total duration OR the create time changes. A stacking buff
        // refresh keeps the same Duration but resets CreateTime — without the CreateTime check the
        // timer would keep counting down from the original application and the tile would expire early.
        if (isNew || entry.Duration != durMs || entry.CreateTime != createMs)
        {
            entry.Duration   = durMs;
            entry.CreateTime = createMs;
            entry.SnapTick   = Environment.TickCount64;
            entry.SnapRemain = durMs > 0 ? durMs / 1000f : -1f;
        }
    }

    private static void UpsertClientBuff(int uuid, int buffId, int layer, float maxLife, long createMs)
    {
        bool isNew = !_activeBuffs.TryGetValue(uuid, out var entry);
        if (isNew)
        {
            entry = new BuffTrackEntry(uuid) { IsClientBuff = true };
            _activeBuffs[uuid] = entry;
            GetBuffInfo(buffId, out var nm, out var vis, out var bt, out var sid);
            entry.BuffBaseId = buffId; entry.BuffName = nm;
            entry.Visible = vis; entry.BuffType = bt;
            entry.SkillId = sid; entry.SkillName = GetSkillName(sid);
        }
        entry!.Layer = layer;
        // Re-snap on refresh: a stacking client buff resets CreateTime (and restores BuffMaxLife) when a
        // new stack lands, but keeps the same uuid. Mirror the server-buff handling so it doesn't expire early.
        long durMs = maxLife > 0f ? (long)(maxLife * 1000f) : 0L;
        if (isNew || entry.CreateTime != createMs || entry.Duration != durMs)
        {
            entry.Duration   = durMs;
            entry.CreateTime = createMs;
            entry.SnapTick   = Environment.TickCount64;
            entry.SnapRemain = maxLife > 0f ? maxLife : -1f;
        }
    }

    private static void PostfixOnAddBuff(object __instance, object __0, bool __1)
    {
        if (!Active) return;
        try
        {
            if (!IsLocalPlayer(GetHostEntityUuid(__instance))) return;
            _localBuffComp = __instance;
            CacheBuffFightSource(__0);
        }
        catch (Exception ex) { LogError("PostfixOnAddBuff", ex); }
    }

    private static PropertyInfo? _piBuffInfoUuid;

    private static void CacheBuffFightSource(object buffInfo)
    {
        try
        {
            if (!_fightSourceResolved)
            {
                _fightSourceResolved = true;
                foreach (var p in buffInfo.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.Name == "BuffUuid")        _piBuffInfoUuid    = p;
                    if (p.Name == "FightSourceInfo") _piFightSourceInfo = p;
                }
            }
            if (_piBuffInfoUuid == null || _piFightSourceInfo == null) return;
            int uuid = (int)(_piBuffInfoUuid.GetValue(buffInfo) ?? 0);
            if (uuid == 0) return;
            var fsi = _piFightSourceInfo.GetValue(buffInfo);
            if (fsi == null) return;
            if (_piFightSourceType == null)
            {
                foreach (var p in fsi.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.Name == "FightSourceType") _piFightSourceType     = p;
                    if (p.Name == "SourceConfigId")  _piFightSourceConfigId = p;
                }
            }
            int sourceType     = (int)(_piFightSourceType?.GetValue(fsi)     ?? -1);
            int sourceConfigId = (int)(_piFightSourceConfigId?.GetValue(fsi) ?? 0);
            if (sourceType == 0 && sourceConfigId > 0)
                _buffSourceSkillId[uuid] = sourceConfigId;
        }
        catch { }
    }

    private static void PostfixClientAddBuff(object __instance, object __0, int __1, int __2, float __3, object __result)
    {
        if (!Active) return;
        try
        {
            if (!IsLocalPlayer(GetHostEntityUuid(__instance))) return;
            _localClientBuffComp = __instance;
        }
        catch (Exception ex) { LogError("PostfixClientAddBuff", ex); }
    }

    private static void LogError(string src, Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        _log?.Invoke($"[Buff] {src}: {ex.InnerException?.Message ?? ex.Message}");
    }

    private static IEnumerable<object?> IterList(object list)
    {
        var t   = list.GetType();
        var cnt = (int)(t.GetProperty("Count")?.GetValue(list) ?? 0);
        var idx = t.GetProperty("Item");
        for (int i = 0; i < cnt; i++)
        {
            _idxArg[0] = i;                       // reused arg array — no per-item object[] alloc
            yield return idx?.GetValue(list, _idxArg);
        }
    }

    private static long GetPlayerUuid()
    {
        long now = Environment.TickCount64;
        if (_cachedPlayerUuid != 0 && now - _playerUuidTick < 2000) return _cachedPlayerUuid;
        if (!_entityMgrResolved) ResolveEntityMgr();
        if (_entityMgrInst == null || _miGetPlayerUuid == null) return 0L;
        _cachedPlayerUuid = (long)_miGetPlayerUuid.Invoke(_entityMgrInst, null)!;
        _playerUuidTick   = now;
        return _cachedPlayerUuid;
    }

    private static bool IsLocalPlayer(long hostUuid)
    {
        var puuid = GetPlayerUuid();
        return puuid != 0L && hostUuid == puuid;
    }

    private static void ResolveEntityMgr()
    {
        var t = SkillCDPatch.FindType("Panda.ZGame.ZEntityMgr");
        if (t == null) { _entityMgrResolved = true; return; }
        PropertyInfo? instProp = null;
        var cur = t;
        while (cur != null && instProp == null)
        {
            instProp = cur.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    ?? cur.GetProperty("Instance", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            cur = cur.BaseType;
        }
        _entityMgrInst   = instProp?.GetValue(null);
        _miGetPlayerUuid = t.GetMethod("get_PlayerUuid", BindingFlags.Public | BindingFlags.Instance);
        if (_entityMgrInst != null) _entityMgrResolved = true;
    }

    private static long GetHostEntityUuid(object comp)
    {
        if (!_hostReflResolved) ResolveHostRefl(comp);
        if (_piZComponentHost == null || _piZEntityUuid == null) return 0L;
        var host = _piZComponentHost.GetValue(comp);
        return host == null ? 0L : (long)(_piZEntityUuid.GetValue(host) ?? 0L);
    }

    private static void ResolveHostRefl(object comp)
    {
        _hostReflResolved = true;
        var cur = comp.GetType();
        while (cur != null && _piZComponentHost == null)
        {
            _piZComponentHost = cur.GetProperty("Host", BindingFlags.NonPublic | BindingFlags.Instance)
                             ?? cur.GetProperty("Host", BindingFlags.Public    | BindingFlags.Instance);
            cur = cur.BaseType;
        }
        if (_piZComponentHost != null)
            _piZEntityUuid = _piZComponentHost.PropertyType
                .GetProperty("Uuid", BindingFlags.Public | BindingFlags.Instance);
    }

    private static void ResolveShowedList()
    {
        _showedListResolved = true;
        PropertyInfo? best = null; int bestPrio = 99;
        foreach (var p in _localBuffComp!.GetType().GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            int prio = p.Name switch { "pShowedBuffList_" => 0, "showedBuffList_" => 1, "pBuffList_" => 2, "buffList_" => 3, _ => 99 };
            if (prio < bestPrio) { best = p; bestPrio = prio; if (bestPrio == 0) break; }
        }
        _piShowedBuffList = best;
    }

    private static void ResolveBuffItemFields(object item)
    {
        _buffItemFieldsResolved = true;
        foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if      (p.Name == "BuffUuid")   _piBuffItemUuid     = p;
            else if (p.Name == "BuffBaseId") _piBuffItemBaseId   = p;
            else if (p.Name == "Layer")      _piBuffItemLayer    = p;
            else if (p.Name == "Level")      _piBuffItemLevel    = p;
            else if (p.Name == "Duration")   _piBuffItemDuration = p;
            else if (p.Name == "CreateTime") _piBuffItemCreateTime = p;
        }
    }

    private static void ResolveClientList()
    {
        _clientListResolved = true;
        _piClientBuffList = _localClientBuffComp!.GetType()
            .GetProperty("BuffList", BindingFlags.Public | BindingFlags.Instance);
    }

    private static void ResolveClientBuffInfoRefl(object item)
    {
        _clientBuffInfoResolved = true;
        var t = item.GetType();
        _piClientBuffUuid    = t.GetProperty("BuffUuid",     BindingFlags.Public | BindingFlags.Instance);
        _piClientBuffId      = t.GetProperty("BuffId",       BindingFlags.Public | BindingFlags.Instance);
        _piClientBuffMaxLife = t.GetProperty("BuffMaxLife",  BindingFlags.Public | BindingFlags.Instance);
        _piClientBuffLayer   = t.GetProperty("CurrentLayer", BindingFlags.Public | BindingFlags.Instance);
        _piClientBuffCreateTime = t.GetProperty("CreateTime", BindingFlags.Public | BindingFlags.Instance);
        _fiIsInValid         = t.GetField("IsInValid",       BindingFlags.Public | BindingFlags.Instance);
    }
}
