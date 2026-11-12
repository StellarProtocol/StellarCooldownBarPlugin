using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CooldownBar;

// Per-frame tile text + icon caches for the bar Funcs (Plugin-Performance-Rules.md §2/§5). The Funcs are polled every
// frame for every visible tile, so none of them may format strings, run the tag-strip regex, or hit GameAssets per call:
//  • Seconds: one cached string per tile SLOT, rebuilt only when the integer that drives the format changes
//    (CdTimeFormat.Key: tenths < 10 s, seconds < 100 s, minutes < 100 min, hours above; permanent / '*' folded in).
//  • Charge / stack chip: one string per slot, rebuilt when the count changes.
//  • Fallback badge (Abbrev(TileName)): cached per (kind, id) — it never changes for an id. Only cached once the game
//    record exists (tables can load late), and cleared when the translated buff text arrives (TranslatedBuffText).
//  • Icon: resolved texture + UV per (kind, id, icon skill), SUCCESSFUL loads only. Loads are async (null while
//    loading), so a miss is retried — every 200 ms for the first 2 s, then about once per second. A hit on a lower
//    tier of the fallback chain (e.g. the plain skill icon while the Imagine art is still loading) is kept but the
//    better tiers are re-tried on the same schedule, so the icon still upgrades the way the uncached chain did.
//  • "+N" overflow: one string per N.
// ClearBarCaches() runs on Show-hidden changes and Dispose.
public sealed partial class Plugin
{
    // ── Seconds label ────────────────────────────────────────────────────────
    private readonly long[]   _secKey = new long[TileCap];
    private readonly string[] _secStr = new string[TileCap];

    // CdTimeFormat.cs owns the format (unit-tested) and the integer key that drives it.
    private string SecondsLabel(int idx)
    {
        if (!SlotValid(idx)) return "";
        var t = _tiles[idx];
        // Truncate to whole ms like the old (int)(rem * 1000) snapshot so the formatted value is unchanged.
        float secs = t.Permanent ? 0f : (int)(t.RemainAt(Environment.TickCount64) * 1000f) / 1000f;
        long key = CdTimeFormat.Key(secs, t.Permanent, t.Fallback);
        if (key == _secKey[idx] && _secStr[idx] != null) return _secStr[idx];
        _secKey[idx] = key;
        return _secStr[idx] = CdTimeFormat.Format(secs, t.Permanent, t.Fallback);
    }

    // ── Charge / stack chip ──────────────────────────────────────────────────
    // One string per slot, rebuilt only when the count changes (stacks / pending charges move at human speed).
    private readonly int[]    _chipN   = new int[TileCap];
    private readonly string[] _chipStr = new string[TileCap];

    private string ChipLabel(int idx)
    {
        int n = TileChipCount(idx);
        if (n == _chipN[idx] && _chipStr[idx] != null) return _chipStr[idx];
        _chipN[idx] = n;
        return _chipStr[idx] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    // ── Fallback badge (icon-less tiles) ─────────────────────────────────────
    private readonly Dictionary<(TileKind, int), string> _abbrevCache = new();

    private string TileAbbrev(int idx)
    {
        if (!SlotValid(idx)) return "";
        var t = _tiles[idx];
        var key = (t.Kind, t.Id);
        if (_abbrevCache.TryGetValue(key, out var s)) return s;
        s = Abbrev(TileName(idx, out bool known));
        if (known) _abbrevCache[key] = s;   // only once the game row exists — a late-loading table must still resolve
        return s;
    }

    // ── Icon ─────────────────────────────────────────────────────────────────
    private sealed class IconEntry
    {
        public object? Tex;
        public UvRect  Uv;
        public int     Tier;        // fallback-chain tier that produced Tex (lower = preferred)
        public long    FirstTry, RetryAt;
    }
    private readonly Dictionary<(TileKind, int, int), IconEntry> _iconCache = new();
    private const long IconFastRetryMs = 200, IconFastWindowMs = 2000, IconSlowRetryMs = 1000;

    private object? TileIcon(int idx)
    {
        if (!SlotValid(idx)) { _uv[idx] = new UvRect(0f, 0f, 1f, 1f); return null; }
        var t = _tiles[idx];
        long now = Environment.TickCount64;
        var key = (t.Kind, t.Id, t.IconSkillId);
        if (!_iconCache.TryGetValue(key, out var e))
        {
            e = new IconEntry { Tier = int.MaxValue, FirstTry = now };
            _iconCache[key] = e;
        }
        // A cached texture can be destroyed under us (Unity fake-null) — treat it as a miss and reload.
        if (e.Tex is UnityEngine.Object uo && uo == null) { e.Tex = null; e.Tier = int.MaxValue; e.RetryAt = 0; }

        int best = BestIconTier(t);
        if (e.Tier > best && now >= e.RetryAt)
        {
            var tex = ResolveTileIcon(t, out var uv, out int tier);
            if (tex != null && tier <= e.Tier) { e.Tex = tex; e.Uv = uv; e.Tier = tier; }
            e.RetryAt = now + (now - e.FirstTry < IconFastWindowMs ? IconFastRetryMs : IconSlowRetryMs);
        }
        _uv[idx] = e.Tex != null ? e.Uv : new UvRect(0f, 0f, 1f, 1f);
        return e.Tex;
    }

    // The first tier of ResolveTileIcon's chain that applies to this tile — reaching it means the icon is settled.
    private static int BestIconTier(in TrackedTile t)
    {
        if (t.Kind == TileKind.Cooldown || t.IconSkillId > 0) return 0;
        return TranslatedBuffText.EffectOverrides.TryGetValue(t.Id, out var ov) && ov.IconSkill > 0 ? 2 : 4;
    }

    // The original fallback chain, unchanged, reporting which tier hit.
    private object? ResolveTileIcon(in TrackedTile t, out UvRect uv, out int tier)
    {
        var ga = _services.GameAssets;
        object? tex;
        if (t.Kind == TileKind.Cooldown)
        {
            tier = 0; tex = ga.LoadImagineIcon(t.IconSkillId, out uv);
            if (tex != null) return tex;
            tier = 1; return ga.LoadSkillIcon(t.Id, out uv);
        }
        // Buff/debuff: source skill's icon when known, then a curated EffectOverride icon (for icon-less buffs with
        // no source skill — borrows an imagine/skill icon), falling back to the buff/debuff's own icon.
        if (t.IconSkillId > 0)
        {
            tier = 0; tex = ga.LoadImagineIcon(t.IconSkillId, out uv); if (tex != null) return tex;
            tier = 1; tex = ga.LoadSkillIcon(t.IconSkillId, out uv);   if (tex != null) return tex;
        }
        if (TranslatedBuffText.EffectOverrides.TryGetValue(t.Id, out var ov) && ov.IconSkill > 0)
        {
            tier = 2; tex = ga.LoadImagineIcon(ov.IconSkill, out uv); if (tex != null) return tex;
            tier = 3; tex = ga.LoadSkillIcon(ov.IconSkill, out uv);   if (tex != null) return tex;
        }
        tier = 4; return ga.LoadBuffIcon(t.Id, out uv);
    }

    // ── "+N" overflow ────────────────────────────────────────────────────────
    private int    _overflowN = int.MinValue;
    private string _overflowStr = "";

    private string OverflowLabel()
    {
        int n = _totalTileCount - _tileCount;
        if (n != _overflowN) { _overflowN = n; _overflowStr = "+" + n; }
        return _overflowStr;
    }

    // Show-hidden toggle, translated-text arrival (main thread, via OnUpdate) and Dispose.
    private void ClearBarCaches()
    {
        Array.Clear(_secKey); Array.Clear(_secStr);
        Array.Clear(_chipN); Array.Clear(_chipStr);
        _abbrevCache.Clear();
        _iconCache.Clear();
        _overflowN = int.MinValue; _overflowStr = "";
    }
}
