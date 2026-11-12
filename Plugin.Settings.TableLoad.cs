using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// Time-sliced picker table loads (Plugin-Performance-Rules.md §1 "long jobs → time-slice"). The SkillTable walk is ~4.9k
// reflected rows and the BuffTable ~10k: done in one go at world entry they cost a single 11–30 ms plugin tick
// ([Hitch] stellar.cooldownbar, 2026-11-13). Each load is now a resumable job: the eager world-entry load
// (Plugin.Normalize.cs) advances it within a per-tick ms budget and resumes next tick; a caller that needs the table NOW
// (Settings shown, a kind switch, the Hidden-effects toggle) finishes the same job synchronously (deadline = unbounded).
// Nothing is published until a job completes: the plugin fields swap in one step, so the bar's fallback skill mapping
// (Plugin.Seen.cs ResolveNamedBaseSkill — never memoised) stands in until then.
public sealed partial class Plugin
{
    private const long Unbounded = long.MaxValue;

    private SkillTableLoad? _stJob;   // in-flight SkillTable walk, or null
    private BuffTableLoad?  _btJob;   // in-flight BuffTable walk, or null

    // Advance (or start) the skill load until the deadline (Stopwatch timestamp). True = the attempt finished — success
    // publishes and sets _stBaseLoaded; a failed open / empty table leaves it false (the caller retries later).
    private bool StepSkillLoad(long deadline)
    {
        if (_stJob == null)
        {
            var walk = TableWalk.Open("Bokura.SkillTableBase", "SkillTableBase", _services.Log, "SkillTable");
            if (walk == null) return true;
            _stJob = new SkillTableLoad(walk, _showHidden);
        }
        bool done;
        try { done = _stJob.Step(deadline); }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Settings] LoadSkillTable: {ex.InnerException?.Message ?? ex.Message}");
            _stJob = null;
            return true;
        }
        if (!done) return false;
        var j = _stJob; _stJob = null;
        _stBaseIds = j.Ids.ToArray(); _stBaseNames = j.Names.ToArray(); _stBaseDescs = j.Descs.ToArray();
        _stListSet = new HashSet<int>(j.Ids);
        _stAllSet  = j.All;
        _baseSkillMemo.Clear();   // memoised mappings were made against the previous list (Hidden effects toggled)
        _stBaseLoaded = _stBaseIds.Length > 0;
        _seenDirty = true;
        _services.Log.Info($"[Settings] loaded {_stBaseIds.Length} skills ({j.All.Count} rows) in {j.Slices} slices, "
            + $"{j.WorkMs:F1} ms");
        return true;
    }

    // Same contract for the buff table. On success: _buffTabLoaded, the placed set re-syncs, the active filters re-apply.
    private bool StepBuffLoad(long deadline)
    {
        if (_btJob == null)
        {
            var walk = TableWalk.Open("Bokura.BuffTableBase", "BuffTableBase", _services.Log, "BuffTable");
            if (walk == null) return true;
            // Show-hidden uses the resolved label (game Name for real buffs, NameDesign for hidden ones) BOTH as the
            // display name and the grouping key, so distinct hidden buffs don't collapse into one placeholder blob.
            if (_showHidden) TranslatedBuffText.EnsureLoaded();   // no-op once started; labels fall back until it lands
            _btJob = new BuffTableLoad(walk, _showHidden);
        }
        bool done;
        try { done = _btJob.Step(deadline); }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Settings] LoadBuffTable: {ex.InnerException?.Message ?? ex.Message}");
            _btJob = null;
            return true;
        }
        if (!done) return false;
        var j = _btJob; _btJob = null;
        _buffAllSet = j.All;
        j.Debuffs.Export(out _dtIds, out _dtNames, out _dtDescs, out _dtMembers, out _dtCount);
        j.Buffs.Export(out _btIds, out _btNames, out _btDescs, out _btMembers, out _btCount);
        _services.Log.Info($"[Settings] loaded {_dtCount} debuff groups, {_btCount} buff groups in {j.Slices} slices, "
            + $"{j.WorkMs:F1} ms");
        _buffTabLoaded = _dtCount > 0 || _btCount > 0;
        if (_buffTabLoaded) OnTrackedChanged();   // new groups → the placed set re-syncs (Plugin.Placed.cs)
        if (_dtCount > 0) ApplyDebuffTabFilter(_dtFilter);
        if (_btCount > 0) ApplyBuffTabFilter(_btFilter);
        return true;
    }

    // Drop in-flight walks whose rows were read under settings that just changed (Hidden effects, translated labels),
    // or whose world went down mid-walk. The next Ensure / eager tick starts them again.
    private void CancelTableLoads() { _stJob = null; _btJob = null; }

    // ── resumable reflection walk over Bokura.<X>TableBase.GetTable(false).Values ────────────────────────────────────
    private sealed class TableWalk
    {
        private readonly object _enumerator;
        private readonly MethodInfo _moveNext;
        private readonly PropertyInfo _current;
        private PropertyInfo? _kvpValue;   // set when the enumerator yields KeyValuePairs (probed on the first row)
        private bool _probed;

        private TableWalk(object e, MethodInfo next, PropertyInfo cur) { _enumerator = e; _moveNext = next; _current = cur; }

        public static TableWalk? Open(string typeName, string fallbackName, IPluginLog log, string label)
        {
            var t = StellarInterop.FindType(typeName) ?? StellarInterop.FindType(fallbackName);
            if (t == null) { log.Warning($"[Settings] {fallbackName} not found"); return null; }
            MethodInfo? miGet = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetTable") continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(bool)) { miGet = m; break; }
            }
            if (miGet == null) { log.Warning($"[Settings] {fallbackName}.GetTable not found"); return null; }
            try
            {
                var tbl = miGet.Invoke(null, new object[] { false });
                if (tbl == null) return null;
                var values = tbl.GetType().GetProperty("Values", BindingFlags.Public | BindingFlags.Instance)
                                 ?.GetValue(tbl) ?? tbl;
                var ct = values.GetType();
                var miEnum = ct.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
                var e = miEnum?.Invoke(values, null);
                if (e == null) return null;
                var et = e.GetType();
                var next = et.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
                var cur  = et.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
                return next == null || cur == null ? null : new TableWalk(e, next, cur);
            }
            catch (Exception ex)
            {
                log.Warning($"[Settings] Load{label}: {ex.InnerException?.Message ?? ex.Message}");
                return null;
            }
        }

        // Next row (a KeyValuePair unwrapped to its Value); false once the table is exhausted. row may be null.
        public bool TryNext(out object? row)
        {
            row = null;
            if (!(bool)(_moveNext.Invoke(_enumerator, null) ?? false)) return false;
            var item = _current.GetValue(_enumerator);
            if (item == null) return true;
            if (!_probed) { _probed = true; _kvpValue = item.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance); }
            row = _kvpValue != null ? _kvpValue.GetValue(item) : item;
            return true;
        }
    }

    // Shared slice loop: rows until the walk ends (true) or the deadline passes (false, resume next call). The clock is
    // read every 32 rows — one reflected row is a few µs, so a slice overshoots its budget by well under 0.5 ms.
    private abstract class TableLoadJob
    {
        private readonly TableWalk _walk;
        private long _workTicks;
        public int Slices { get; private set; }
        public double WorkMs => _workTicks * 1000.0 / Stopwatch.Frequency;
        protected readonly bool ShowHidden;

        protected TableLoadJob(TableWalk walk, bool showHidden) { _walk = walk; ShowHidden = showHidden; }

        public bool Step(long deadline)
        {
            long start = Stopwatch.GetTimestamp();
            Slices++;
            try
            {
                for (int n = 1; ; n++)
                {
                    if (!_walk.TryNext(out var row)) return true;
                    if (row != null) Row(row);
                    if ((n & 31) == 0 && deadline != Unbounded && Stopwatch.GetTimestamp() >= deadline) return false;
                }
            }
            finally { _workTicks += Stopwatch.GetTimestamp() - start; }
        }

        protected abstract void Row(object row);
    }

    private sealed class SkillTableLoad : TableLoadJob
    {
        public readonly List<int> Ids = new();
        public readonly List<string> Names = new();
        public readonly List<string> Descs = new();
        public readonly HashSet<int> All = new();
        private PropertyInfo? _piId, _piName, _piIcon, _piDesc;
        private bool _bound;

        public SkillTableLoad(TableWalk walk, bool showHidden) : base(walk, showHidden) { }

        protected override void Row(object row)
        {
            if (!_bound)
            {
                _bound = true;
                var rt = row.GetType();
                _piId   = rt.GetProperty("Id",   BindingFlags.Public | BindingFlags.Instance);
                _piName = rt.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                _piIcon = rt.GetProperty("Icon", BindingFlags.Public | BindingFlags.Instance);
                _piDesc = rt.GetProperty("Desc", BindingFlags.Public | BindingFlags.Instance);
            }
            int id = (int)(_piId?.GetValue(row) ?? 0);
            if (id <= 0) return;
            All.Add(id);   // every row, before the Hidden-effects filter (SelectionNormalizer.MapSkill)
            string name = (string?)(_piName?.GetValue(row)) ?? "";
            string icon = (string?)(_piIcon?.GetValue(row)) ?? "";
            // Show-hidden: keep every id>0 row. An unnamed hidden skill still needs a usable list label.
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(icon))
            {
                if (!ShowHidden) return;
                if (string.IsNullOrEmpty(name)) name = $"Skill {id}";
            }
            string desc = (string?)(_piDesc?.GetValue(row)) ?? "";
            Ids.Add(id); Names.Add(name); Descs.Add(desc);
        }
    }

    private sealed class BuffTableLoad : TableLoadJob
    {
        // Grouped by display Name (exact match, first-seen order).
        public readonly BuffGroupAccumulator Debuffs = new();
        public readonly BuffGroupAccumulator Buffs = new();
        public readonly HashSet<int> All = new();
        private PropertyInfo? _piId, _piName, _piIcon, _piType, _piDesc;
        private bool _bound;

        public BuffTableLoad(TableWalk walk, bool showHidden) : base(walk, showHidden) { }

        protected override void Row(object row)
        {
            if (!_bound)
            {
                _bound = true;
                var rt = row.GetType();
                _piId   = rt.GetProperty("Id",       BindingFlags.Public | BindingFlags.Instance);
                _piName = rt.GetProperty("Name",     BindingFlags.Public | BindingFlags.Instance);
                _piIcon = rt.GetProperty("Icon",     BindingFlags.Public | BindingFlags.Instance);
                _piType = rt.GetProperty("BuffType", BindingFlags.Public | BindingFlags.Instance);
                _piDesc = rt.GetProperty("Desc",     BindingFlags.Public | BindingFlags.Instance);
            }
            int id = (int)(_piId?.GetValue(row) ?? 0);
            if (id <= 0) return;
            All.Add(id);   // before the Hidden-effects filter
            string gameName = (string?)(_piName?.GetValue(row)) ?? "";
            string icon = (string?)(_piIcon?.GetValue(row)) ?? "";
            if (!ShowHidden && (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(icon))) return;
            string desc = (string?)(_piDesc?.GetValue(row)) ?? "";
            int buffType = (int)(_piType?.GetValue(row) ?? -1);
            // OFF: keep today's behaviour (raw game Name). ON: resolved label for display + grouping.
            string name = ShowHidden ? TranslatedBuffText.ResolveLabel(id, gameName) : gameName;
            (buffType == 0 ? Debuffs : Buffs).Add(name, id, desc);
        }
    }
}
