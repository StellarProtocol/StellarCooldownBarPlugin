using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// Reflection resolution + identity helpers for BuffTrackPatch (split from BuffTrackPatch.cs for the 500-line cap):
// local-player / host-entity uuid lookups, the fight-source cache, and the one-time PropertyInfo resolves for the
// server (BuffItem) and client (ClientBuffInfo) buff lists.
internal static partial class BuffTrackPatch
{
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

    // Both buff lists are int-indexed ZLists (Count + Item[int]); the framework floor walks them.
    private static IEnumerable<object?> IterList(object list) => StellarInterop.Enumerate(list);

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
        var t = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
        if (t == null) { _entityMgrResolved = true; return; }
        _entityMgrInst   = StellarInterop.GetSingleton(t);
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
