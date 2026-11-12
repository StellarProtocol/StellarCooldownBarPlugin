using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

internal static partial class BuffTrackPatch
{
    public static IReadOnlyDictionary<int, BuffTrackEntry> ActiveBuffs => _activeBuffs;

    private static readonly Dictionary<int, BuffTrackEntry> _activeBuffs = new();
    private static Action<string>? _log;

    // ── Demand gate ──
    // The postfixes fire for EVERY entity's buff add/sync and box longs via reflection before the local-player
    // check. Skip them entirely (one comparison, zero alloc) unless the bar is actively refreshing. The plugin's
    // OnUpdate calls MarkDemand() on each throttled refresh while the bar is shown.
    private const  long DemandWindowMs = 500;
    private static long _lastDemandTick;
    private static bool Active => _lastDemandTick != 0 && Environment.TickCount64 - _lastDemandTick < DemandWindowMs;
    internal static void MarkDemand() => _lastDemandTick = Environment.TickCount64;

    // Reused across RefreshActiveBuffs calls so the 10 Hz refresh doesn't churn a fresh HashSet+List each time.
    private static readonly HashSet<int> _live  = new();
    private static readonly List<int>    _stale = new();

    // Player uuid is stable within a session; cache it to avoid a game read per buff event.
    private static long _cachedPlayerUuid;
    private static long _playerUuidTick;

    private static object? _localBuffComp;
    private static object? _localClientBuffComp;

    // Every hot-path member below is a delegate bound ONCE by Getters (compiled, reflection fallback) — no
    // PropertyInfo.GetValue / Invoke / boxing per read. Resolved lazily from the first live object (BuffTrackPatch.Resolve.cs).
    private static Func<object, object?>? _getShowedBuffList;
    private static bool                   _showedListResolved;

    private static Func<object, long>? _bItemUuid, _bItemBaseId, _bItemLayer, _bItemLevel, _bItemDuration, _bItemCreateTime;
    private static bool                _buffItemFieldsResolved;

    private static Func<object, object?>? _getClientBuffList;
    private static bool                   _clientListResolved;

    private static Func<object, long>?   _cUuid, _cId, _cLayer, _cInvalid;
    private static Func<object, double>? _cMaxLife, _cCreateTime;
    private static bool                  _clientBuffInfoResolved;

    // Int-indexed list accessors (Count + get_Item(int)), bound per list TYPE — replaces StellarInterop.Enumerate.
    private static Type?                       _srvListType, _cliListType;
    private static Func<object, long>?         _srvCount, _cliCount;
    private static Func<object, int, object?>? _srvItem, _cliItem;

    private static Func<object, object?>? _getHost;
    private static Func<object, long>?    _getHostUuid;
    private static bool                   _hostReflResolved;

    private static object?             _entityMgrInst;
    private static Func<object, long>? _getPlayerUuid;
    private static bool                _entityMgrResolved;

    private static readonly Dictionary<int, int> _buffSourceSkillId = new();
    private static Func<object, long>?    _fsBuffUuid, _fsType, _fsConfigId;
    private static Func<object, object?>? _fsInfo;
    private static Type?                  _fsInfoType;
    private static bool                   _fightSourceResolved;

    private static bool _loggedError;
    private static bool _diagLogged;

    internal static bool Install(Harmony harmony, Action<string> log, Func<long> serverNowMs)
    {
        _log = log;
        _serverNowMs = serverNowMs;   // framework clock (Tables.cs ServerNowMs) — never the game ZServerTime

        var buffCompType = StellarInterop.FindType("Panda.ZGame.BuffComp");
        if (buffCompType == null)
        {
            log("[Buff] BuffComp not found — patch skipped");
            return false;
        }

        // OnAddBuff is mandatory; resolve it FIRST and bail before patching anything if absent, so we never
        // leave a partial patch installed (the framework owns the Harmony instance — no manual UnpatchSelf).
        var mAdd = StellarInterop.FindMethod(buffCompType, "OnAddBuff", 2);
        if (mAdd == null) { log("[Buff] OnAddBuff not found — patch skipped"); return false; }
        try
        {
            harmony.Patch(mAdd, postfix: new HarmonyMethod(typeof(BuffTrackPatch), nameof(PostfixOnAddBuff)));
            log("[Buff] OnAddBuff patched");
        }
        catch (Exception ex) { log($"[Buff] OnAddBuff patch failed: {ex.Message}"); return false; }

        var mSync = StellarInterop.FindMethod(buffCompType, "OnBuffSync", 2);
        if (mSync != null)
        {
            try
            {
                harmony.Patch(mSync, postfix: new HarmonyMethod(typeof(BuffTrackPatch), nameof(PostfixOnAddBuff)));
                log("[Buff] OnBuffSync patched");
            }
            catch (Exception ex) { log($"[Buff] OnBuffSync patch failed: {ex.Message}"); }
        }

        var clientCompType = StellarInterop.FindType("Panda.ZGame.ClientBuffComp");
        if (clientCompType != null)
        {
            var mClientAdd = StellarInterop.FindMethod(clientCompType, "AddBuff", 4);
            if (mClientAdd != null)
            {
                try
                {
                    harmony.Patch(mClientAdd, postfix: new HarmonyMethod(typeof(BuffTrackPatch), nameof(PostfixClientAddBuff)));
                    log("[Buff] ClientBuffComp.AddBuff postfix patched");
                }
                catch (Exception ex) { log($"[Buff] ClientBuffComp.AddBuff patch failed: {ex.Message}"); }
            }
        }
        return true;
    }

    internal static void Uninstall()
    {
        // Harmony teardown is owned by IHarmonyHost, which auto-unpatches every instance on plugin dispose —
        // do NOT unpatch here (that would double-unpatch). Only reset our own transient reflection state.
        _activeBuffs.Clear();
        _serverNowMs = null;
        _localBuffComp = _localClientBuffComp = null;
        _getShowedBuffList = null; _showedListResolved = false;
        ResetShowHiddenCaches();
        _bItemUuid = _bItemBaseId = _bItemLayer = _bItemLevel = _bItemDuration = _bItemCreateTime = null;
        _buffItemFieldsResolved = false;
        _getClientBuffList = null; _clientListResolved = false;
        _cUuid = _cId = _cLayer = _cInvalid = null; _cMaxLife = _cCreateTime = null;
        _clientBuffInfoResolved = false;
        _srvListType = _cliListType = null; _srvCount = _cliCount = null; _srvItem = _cliItem = null;
        _getHost = null; _getHostUuid = null; _hostReflResolved = false;
        _entityMgrInst = null; _getPlayerUuid = null; _entityMgrResolved = false;
        _buffSourceSkillId.Clear();
        _fsBuffUuid = _fsType = _fsConfigId = null; _fsInfo = null; _fsInfoType = null;
        _fightSourceResolved = false;
        ResetTableCaches();   // Tables.cs
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
            // Full (unfiltered) list when ShowHidden, else the game's display-filtered "showed" list. See Tables.cs.
            var list = GetActiveServerList(_localBuffComp);
            if (list != null)
            {
                try
                {
                    if (EnsureListAccess(list, ref _srvListType, ref _srvCount, ref _srvItem))
                    {
                        for (int i = 0, n = (int)_srvCount!(list); i < n; i++)
                        {
                            var item = ItemAt(_srvItem!, list, i);
                            if (item == null) continue;
                            if (!_buffItemFieldsResolved) ResolveBuffItemFields(item);
                            if (_bItemUuid == null) break;
                            int uuid = (int)_bItemUuid(item);
                            if (uuid == 0) continue;
                            live.Add(uuid);
                            int baseId    = _bItemBaseId     != null ? (int)_bItemBaseId(item) : 0;
                            int layer     = _bItemLayer      != null ? (int)_bItemLayer(item)  : 1;
                            int level     = _bItemLevel      != null ? (int)_bItemLevel(item)  : 0;
                            long durMs    = _bItemDuration   != null ? _bItemDuration(item)    : 0L;
                            long createMs = _bItemCreateTime != null ? _bItemCreateTime(item)  : 0L;
                            UpsertServerBuff(uuid, baseId, layer, level, durMs, createMs);
                        }
                    }
                }
                catch (Exception ex) { LogError("server list", ex); }
            }
        }

        if (_localClientBuffComp != null)
        {
            if (!_clientListResolved) ResolveClientList();
            var list = _getClientBuffList?.Invoke(_localClientBuffComp);
            if (list != null)
            {
                try
                {
                    if (EnsureListAccess(list, ref _cliListType, ref _cliCount, ref _cliItem))
                    {
                        for (int i = 0, n = (int)_cliCount!(list); i < n; i++)
                        {
                            var item = ItemAt(_cliItem!, list, i);
                            if (item == null) continue;
                            if (!_clientBuffInfoResolved) ResolveClientBuffInfoRefl(item);
                            if (_cUuid == null) break;
                            if (_cInvalid != null && _cInvalid(item) != 0) continue;
                            int uuid = (int)_cUuid(item);
                            if (uuid == 0) continue;
                            live.Add(uuid);
                            int buffId    = _cId         != null ? (int)_cId(item)          : 0;
                            int layer     = _cLayer      != null ? (int)_cLayer(item)       : 1;
                            float maxL    = _cMaxLife    != null ? (float)_cMaxLife(item)   : 0f;
                            long createMs = _cCreateTime != null ? (long)_cCreateTime(item) : 0L;
                            UpsertClientBuff(uuid, buffId, layer, maxL, createMs);
                        }
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
        }
        // The game frees and REUSES buff uuids across scene changes: a slot can come back as a DIFFERENT buff
        // under the same uuid. Re-populate the identity fields whenever the baseId differs from what's cached —
        // not only on first sight — or the new buff inherits the freed one's name/icon (e.g. a Potion showing a
        // Cuisine identity after zoning in/out of a dungeon).
        bool reinit = isNew || entry!.BuffBaseId != baseId;
        if (reinit)
        {
            // Drop the freed buff's cached fight-source before the fallback so a stale source skill can't apply a
            // wrong borrowed icon. A skill-sourced new buff re-populates it via the OnAddBuff/OnBuffSync postfix.
            if (!isNew) _buffSourceSkillId.Remove(uuid);
            GetBuffInfo(baseId, out var nm, out var vis, out var bt, out var sid, out var hidden);
            if (sid == 0) _buffSourceSkillId.TryGetValue(uuid, out sid);
            entry!.BuffBaseId = baseId; entry.BuffName = nm; entry.Hidden = hidden;
            entry.Visible = vis; entry.BuffType = bt;
            entry.SkillId = sid; entry.SkillName = GetSkillName(sid);
        }
        entry!.Layer = layer; entry.Level = level;
        // Re-snap remaining whenever identity changes (reinit) or the total duration / create time changes. A
        // stacking buff refresh keeps the same Duration but resets CreateTime — without the CreateTime check the
        // timer would keep counting down from the original application and the tile would expire early.
        if (reinit || entry.Duration != durMs || entry.CreateTime != createMs)
        {
            entry.Duration   = durMs;
            entry.CreateTime = createMs;
            entry.SnapTick   = Environment.TickCount64;
            // Seed from the server clock (CreateTime + Duration − serverNow), NOT full duration — a re-polled or
            // zone-change re-synced buff is partway through its life. See Buff-Tracking.md §7 / ComputeSnapRemain.
            entry.SnapRemain = ComputeSnapRemain(entry.Duration, createMs, ServerNowMs());
        }
    }

    private static void UpsertClientBuff(int uuid, int buffId, int layer, float maxLife, long createMs)
    {
        bool isNew = !_activeBuffs.TryGetValue(uuid, out var entry);
        if (isNew)
        {
            entry = new BuffTrackEntry(uuid) { IsClientBuff = true };
            _activeBuffs[uuid] = entry;
        }
        // Reused-uuid guard, same as UpsertServerBuff: re-populate identity when the baseId changes so a recycled
        // client-buff slot doesn't keep the freed buff's name/icon after zoning.
        bool reinit = isNew || entry!.BuffBaseId != buffId;
        if (reinit)
        {
            GetBuffInfo(buffId, out var nm, out var vis, out var bt, out var sid, out var hidden);
            entry!.BuffBaseId = buffId; entry.BuffName = nm; entry.Hidden = hidden;
            entry.Visible = vis; entry.BuffType = bt;
            entry.SkillId = sid; entry.SkillName = GetSkillName(sid);
        }
        entry!.Layer = layer;
        // Re-snap on identity change (reinit) or when a stacking client buff resets CreateTime / restores
        // BuffMaxLife (same uuid). Mirror the server-buff handling so it doesn't expire early.
        long durMs = maxLife > 0f ? (long)(maxLife * 1000f) : 0L;
        if (reinit || entry.CreateTime != createMs || entry.Duration != durMs)
        {
            entry.Duration   = durMs;
            entry.CreateTime = createMs;
            entry.SnapTick   = Environment.TickCount64;
            // ClientBuffInfo.CreateTime has an UNVERIFIED epoch — the createMs ≥ 1.6e12 guard in ComputeSnapRemain
            // keeps this path on the full-duration fallback until that epoch is confirmed (see Buff-Tracking.md §7).
            entry.SnapRemain = ComputeSnapRemain(entry.Duration, createMs, ServerNowMs());
        }
    }

    private static void PostfixOnAddBuff(object __instance, object __0, bool __1)
    {
        using var _perf = CdPerf.Hook();   // [CdPerf] postfix bucket — default struct unless the probe is on
        if (!Active) return;
        CdPerf.HookFull();
        try
        {
            if (!IsLocalPlayer(GetHostEntityUuid(__instance))) return;
            _localBuffComp = __instance;
            CacheBuffFightSource(__0);
        }
        catch (Exception ex) { LogError("PostfixOnAddBuff", ex); }
    }
    private static void PostfixClientAddBuff(object __instance, object __0, int __1, int __2, float __3, object __result)
    {
        using var _perf = CdPerf.Hook();   // [CdPerf] postfix bucket — default struct unless the probe is on
        if (!Active) return;
        CdPerf.HookFull();
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
}
