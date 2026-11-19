using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// Buff name / description resolution for every surface (picker, bar ingestion, tile fallback badge, tooltip), so they
// all show the SAME label. The English text comes from the framework's central translated game text
// (IPluginServices.GameText, fw ≥ 2.33.0 — Buff-Tracking.md "IGameText"); the plugin used to embed its own 8.6 MB
// BuffTable.json for this. Only the hand-made per-effect overrides below stay in the plugin.
internal static class EffectText
{
    // Set by Plugin (ctor), cleared on Dispose. Static because BuffTrackPatch (static Harmony side) resolves labels too.
    // null = not wired yet → game-name fallback (the same path as "table not ready").
    internal static IGameText? GameText;

    // A curated per-effect override for buffs the game data resolves poorly.
    //  • Name      — display label; wins over the automatic resolution ("" = keep the automatic one).
    //  • IconSkill — imagine/skill id whose icon to borrow (Bar fallback for icon-less buffs; 0 = none).
    //  • AsBuff    — file it as a BUFF everywhere (picker Buffs list, bar buff pass, buff selection), even though the
    //                row's BuffType is 0 (the game's "Debuff" type, which placeholder rows default to).
    //  • AlwaysShow — list it and let the bar show it even when "Show hidden" is off (the row has no icon and a
    //                placeholder name, so the normal hidden-effect gate would drop it).
    internal readonly record struct EffectOverride(string Name, int IconSkill, bool AsBuff = false, bool AlwaysShow = false);

    // baseId → override. Rolora / Element Stasis copied verbatim from TargetLens; IconSkill 3948 = "Arcane! Divine
    // Reliance" imagine icon.
    internal static readonly Dictionary<int, EffectOverride> EffectOverrides = new()
    {
        { 2110135, new("Rolora - Active Timer", 3948) },
        { 2110111, new("Rolora - Spell",        3948) },
        { 2110050, new("", 3957) },   // Element Stasis — no source skill of its own; borrow "Arcane! Fatal Spiral" (3957) icon
        // S4 Oath Psychoscope effect "Dreamlike Arrow Rain" (梦幻箭雨, = S3 "Phantom Arrow", same ids): every row is a
        // hidden placeholder (Name "Spirit Blade Thrust count", BuffType 0, no icon) — Buff-Tracking.md "Oath
        // Psychoscope". No game icon exists (the arrow skill 7997 has an empty Icon too), so the bar's placeholder stays.
        { 3002411, new("Dreamlike Arrow Rain",    0, AsBuff: true, AlwaysShow: true) },   // arrow stacks 0–4, 30 s
        { 3002412, new("Dreamlike Arrow Rain CD", 0, AsBuff: true, AlwaysShow: true) },   // 7 s internal cooldown
    };

    // Ids the bar must read from the FULL buff list even while "Show hidden" is off (the game's display-filtered list
    // may leave them out). Built once from the table above; looked up per buff on the 10 Hz refresh.
    internal static readonly HashSet<int> AlwaysShownIds = BuildAlwaysShown();

    private static HashSet<int> BuildAlwaysShown()
    {
        var s = new HashSet<int>();
        foreach (var kv in EffectOverrides) if (kv.Value.AlwaysShow) s.Add(kv.Key);
        return s;
    }

    internal static bool TryGetOverride(int baseId, out EffectOverride ov) => EffectOverrides.TryGetValue(baseId, out ov);

    internal static bool IsForcedBuff(int baseId) => EffectOverrides.TryGetValue(baseId, out var ov) && ov.AsBuff;

    // Unified display label. Curated override first; then the framework chain: a REAL game name wins (unless the row is
    // known to be unnamed / the name is the game's placeholder filler), else the English name, else the English design
    // name, else the game name, else "Buff <id>". Before GameText is ready this is the game name (or "Buff <id>").
    internal static string ResolveLabel(int baseId, string? gameName)
    {
        if (EffectOverrides.TryGetValue(baseId, out var ov) && ov.Name.Length > 0) return ov.Name;
        var gt = GameText;
        if (gt != null) return gt.ResolveDisplayName(GameTextKind.Buff, baseId, gameName, out _);
        return string.IsNullOrEmpty(gameName) ? "Buff " + baseId : gameName!;
    }

    // Description: the game's own description wins when present; else the English description / design note.
    internal static string ResolveDesc(int baseId, string? gameDesc)
    {
        if (!string.IsNullOrEmpty(gameDesc)) return gameDesc!;
        return GameText?.GetDescription(GameTextKind.Buff, baseId) ?? "";
    }
}
