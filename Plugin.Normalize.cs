using System;
using Stellar.Abstractions.Domain;

namespace Stellar.CooldownBar;

// Stale saved-id conversion (SelectionNormalizer.cs holds the pure mapping) + the eager table load it needs.
// Game tables load lazily, so this runs once per session at the first moment BOTH picker tables are loaded — from the
// Settings window's first shown refresh (SelListCount) or the eager world-entry load below — never in the constructor.
// It saves once, only when something changed, and logs one line.
//
// Eager load: the picker tables are otherwise built on the first Settings open. The conversion only needs them once per
// install (config "track.normalized" records it), so a world-entry load happens just until that first successful run
// (and, in Placed mode, every session: Plugin.Placed.cs needs the groups). It is the existing one-shot reflection walk —
// a single main-thread hitch, retried every 2 s while the game tables aren't ready.
public sealed partial class Plugin
{
    private const int NormalizeVersion = 1;
    private const long EagerTableRetryMs = 2000;

    private bool _normDone;            // this session's conversion ran
    private bool _normPersisted;       // config says a conversion has run on this install
    private long _eagerTablesAt;       // next eager-load attempt (Environment.TickCount64)

    private void InitNormalize() => _normPersisted = _cfg.Get("track.normalized", 0) >= NormalizeVersion;

    // Who needs the tables before Settings opens: the first conversion, and Placed mode (its set is built from the groups).
    private bool NeedEagerTables => !_normPersisted || _placedMode;

    // OnUpdate: a couple of bool tests in the common case. Loaded → only the (rate-limited) conversion, until it ran;
    // not loaded → load only when someone needs the tables now.
    private void TickEagerTables()
    {
        bool loaded = _stBaseLoaded && _buffTabLoaded;
        if (loaded ? _normDone : !NeedEagerTables) return;
        long now = Environment.TickCount64;
        if (now < _eagerTablesAt) return;
        _eagerTablesAt = now + EagerTableRetryMs;
        if (_services.ClientState.Phase != GamePhase.World) return;
        if (!loaded) { EnsureSkillTabLoaded(); EnsureBuffTabLoaded(); }
        TryNormalizeSelection();
    }

    private void TryNormalizeSelection()
    {
        if (_normDone || !_stBaseLoaded || !_buffTabLoaded) return;
        var combat = _services.GameData.Combat;
        // Readiness: the game-data lookups the mapping uses must resolve a row the picker load just listed. Until they
        // do, a "no row" answer would mean "not ready", not "stale" — so wait (never drop on an unready table).
        if (_stBaseIds.Length == 0 || combat.GetSkill(_stBaseIds[0]) == null) return;
        int anyBuff = _dtCount > 0 ? _dtIds[0] : _btCount > 0 ? _btIds[0] : 0;
        if (anyBuff == 0 || combat.GetBuff(anyBuff) == null) return;
        _normDone = true;

        int converted = 0, dropped = 0;
        Func<int, bool> skillRow = id => combat.GetSkill(id) != null;
        Func<int, bool> buffRow  = id => combat.GetBuff(id) != null;
        var skills  = SelectionNormalizer.Skills(_selection.SkillIds, ResolveNamedBaseSkill, skillRow, ref converted, ref dropped);
        var debuffs = SelectionNormalizer.Groups(_selection.DebuffIds, _dtMembers, _dtCount, buffRow, ref converted, ref dropped);
        var buffs   = SelectionNormalizer.Groups(_selection.BuffIds,   _btMembers, _btCount, buffRow, ref converted, ref dropped);

        bool changed = !skills.SetEquals(_selection.SkillIds) || !debuffs.SetEquals(_selection.DebuffIds)
                       || !buffs.SetEquals(_selection.BuffIds);
        if (changed)
        {
            _selection.ReplaceSkills(skills);
            _selection.ReplaceDebuffs(debuffs);
            _selection.ReplaceBuffs(buffs);
        }
        if (changed || !_normPersisted)
        {
            _cfg.Set("track.normalized", NormalizeVersion);
            _normPersisted = true;
            _selection.Save(_cfg);   // one save (Save also persists the marker set above)
        }
        if (changed)
        {
            // Re-sort the lists (ticked to the top) and recompute the counts / row caches; the placed set follows.
            ApplySkillTabFilter(_stFilter);
            ApplyDebuffTabFilter(_dtFilter);
            ApplyBuffTabFilter(_btFilter);
            OnTrackedChanged();   // Plugin.Placed.cs — the placed set follows
        }
        _services.Log.Info($"[CooldownBar] saved tracked ids normalised: converted {converted}, dropped {dropped}");
    }
}
