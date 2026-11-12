using System;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// Reflection resolution + identity helpers for BuffTrackPatch (split from BuffTrackPatch.cs for the 500-line cap):
// local-player / host-entity uuid lookups, the fight-source cache, and the one-time resolves for the server (BuffItem)
// and client (ClientBuffInfo) buff lists. Every member is found by reflection ONCE and bound into a typed delegate by
// Getters (compiled; reflection fallback) — the hot paths never call GetValue / Invoke. Each resolve logs once which
// path it got ("N compiled, M reflection fallback").
internal static partial class BuffTrackPatch
{
    private const BindingFlags PubInst = BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static void LogBindPath(string what, int bound0, int fallback0) =>
        _log?.Invoke($"[Buff] {what} getters: {Getters.Bound - bound0} compiled, {Getters.Fallback - fallback0} reflection fallback");

    // Last public instance property with this name — the original name-matching loop (never throws AmbiguousMatch).
    private static PropertyInfo? PropByName(Type t, string name)
    {
        PropertyInfo? hit = null;
        foreach (var p in t.GetProperties(PubInst)) if (p.Name == name) hit = p;
        return hit;
    }

    private static void CacheBuffFightSource(object buffInfo)
    {
        try
        {
            if (!_fightSourceResolved)
            {
                _fightSourceResolved = true;
                int b0 = Getters.Bound, f0 = Getters.Fallback;
                var t = buffInfo.GetType();
                _fsBuffUuid = Getters.Long(PropByName(t, "BuffUuid"));
                _fsInfo     = Getters.Obj(PropByName(t, "FightSourceInfo"));
                LogBindPath("BuffInfo", b0, f0);
            }
            if (_fsBuffUuid == null || _fsInfo == null) return;
            int uuid = (int)_fsBuffUuid(buffInfo);
            if (uuid == 0) return;
            var fsi = _fsInfo(buffInfo);
            if (fsi == null) return;
            if (_fsInfoType == null)
            {
                _fsInfoType = fsi.GetType();
                _fsType     = Getters.Long(PropByName(_fsInfoType, "FightSourceType"));
                _fsConfigId = Getters.Long(PropByName(_fsInfoType, "SourceConfigId"));
            }
            int sourceType     = _fsType     != null ? (int)_fsType(fsi)     : -1;
            int sourceConfigId = _fsConfigId != null ? (int)_fsConfigId(fsi) : 0;
            if (sourceType == 0 && sourceConfigId > 0)
                _buffSourceSkillId[uuid] = sourceConfigId;
        }
        catch { }
    }

    // Bind (or re-bind, if the list's proxy type changed — e.g. the show-hidden switch) the Count/get_Item accessors.
    private static bool EnsureListAccess(object list, ref Type? type, ref Func<object, long>? count, ref Func<object, int, object?>? item)
    {
        var t = list.GetType();
        if (t != type)
        {
            type = t;
            int b0 = Getters.Bound, f0 = Getters.Fallback;
            Getters.ListAccessors(t, out count, out item);
            LogBindPath(t.Name + " list", b0, f0);
        }
        return count != null && item != null;
    }

    // One element; a throwing read yields null (same as StellarInterop.Item did) so one bad slot doesn't drop the rest.
    private static object? ItemAt(Func<object, int, object?> item, object list, int i)
    {
        try { return item(list, i); } catch { return null; }
    }

    private static long GetPlayerUuid()
    {
        long now = Environment.TickCount64;
        if (_cachedPlayerUuid != 0 && now - _playerUuidTick < 2000) return _cachedPlayerUuid;
        if (!_entityMgrResolved) ResolveEntityMgr();
        if (_entityMgrInst == null || _getPlayerUuid == null) return 0L;
        _cachedPlayerUuid = _getPlayerUuid(_entityMgrInst);
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
        _entityMgrInst = StellarInterop.GetSingleton(t);
        if (_getPlayerUuid == null)
        {
            int b0 = Getters.Bound, f0 = Getters.Fallback;
            _getPlayerUuid = Getters.Long(t.GetMethod("get_PlayerUuid", PubInst));
            LogBindPath("ZEntityMgr.PlayerUuid", b0, f0);
        }
        if (_entityMgrInst != null) _entityMgrResolved = true;
    }

    private static long GetHostEntityUuid(object comp)
    {
        if (!_hostReflResolved) ResolveHostRefl(comp);
        if (_getHost == null || _getHostUuid == null) return 0L;
        var host = _getHost(comp);
        return host == null ? 0L : _getHostUuid(host);
    }

    private static void ResolveHostRefl(object comp)
    {
        _hostReflResolved = true;
        PropertyInfo? piHost = null;
        for (var cur = comp.GetType(); cur != null && piHost == null; cur = cur.BaseType)
            piHost = cur.GetProperty("Host", BindingFlags.NonPublic | BindingFlags.Instance)
                  ?? cur.GetProperty("Host", PubInst);
        if (piHost == null) return;
        int b0 = Getters.Bound, f0 = Getters.Fallback;
        _getHost     = Getters.Obj(piHost);
        _getHostUuid = Getters.Long(piHost.PropertyType.GetProperty("Uuid", PubInst));
        LogBindPath("Host.Uuid", b0, f0);
    }

    private static void ResolveShowedList()
    {
        _showedListResolved = true;
        PropertyInfo? best = null; int bestPrio = 99;
        foreach (var p in _localBuffComp!.GetType().GetProperties(AnyInst))
        {
            int prio = p.Name switch { "pShowedBuffList_" => 0, "showedBuffList_" => 1, "pBuffList_" => 2, "buffList_" => 3, _ => 99 };
            if (prio < bestPrio) { best = p; bestPrio = prio; if (bestPrio == 0) break; }
        }
        _getShowedBuffList = Getters.Obj(best);
    }

    private static void ResolveBuffItemFields(object item)
    {
        _buffItemFieldsResolved = true;
        PropertyInfo? uuid = null, baseId = null, layer = null, level = null, dur = null, create = null;
        foreach (var p in item.GetType().GetProperties(AnyInst))
        {
            if      (p.Name == "BuffUuid")   uuid   = p;
            else if (p.Name == "BuffBaseId") baseId = p;
            else if (p.Name == "Layer")      layer  = p;
            else if (p.Name == "Level")      level  = p;
            else if (p.Name == "Duration")   dur    = p;
            else if (p.Name == "CreateTime") create = p;
        }
        int b0 = Getters.Bound, f0 = Getters.Fallback;
        _bItemUuid       = Getters.Long(uuid);
        _bItemBaseId     = Getters.Long(baseId);
        _bItemLayer      = Getters.Long(layer);
        _bItemLevel      = Getters.Long(level);
        _bItemDuration   = Getters.Long(dur);
        _bItemCreateTime = Getters.Long(create);
        LogBindPath("BuffItem", b0, f0);
    }

    private static void ResolveClientList()
    {
        _clientListResolved = true;
        _getClientBuffList = Getters.Obj(_localClientBuffComp!.GetType().GetProperty("BuffList", PubInst));
    }

    private static void ResolveClientBuffInfoRefl(object item)
    {
        _clientBuffInfoResolved = true;
        var t = item.GetType();
        int b0 = Getters.Bound, f0 = Getters.Fallback;
        _cUuid       = Getters.Long(t.GetProperty("BuffUuid",     PubInst));
        _cId         = Getters.Long(t.GetProperty("BuffId",       PubInst));
        _cMaxLife    = Getters.Double(t.GetProperty("BuffMaxLife", PubInst));
        _cLayer      = Getters.Long(t.GetProperty("CurrentLayer", PubInst));
        _cCreateTime = Getters.Double(t.GetProperty("CreateTime", PubInst));
        // GetField, as before: on the interop proxy IsInValid is surfaced as a PROPERTY (IL2CPP fields are properties),
        // so this resolves null and the invalid-skip never applies — unchanged behaviour, flagged for a follow-up.
        _cInvalid    = Getters.Long(t.GetField("IsInValid", PubInst));
        LogBindPath("ClientBuffInfo", b0, f0);
    }
}
