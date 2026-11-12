using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.CooldownBar;

// English-translated buff name/description source (ported from TargetLensPlugin).
//
// The live game BuffTableBase returns the build's Chinese strings; the bundled BuffTable.json (embedded, from the
// community translation dump) carries the English text. We resolve name + description per these rules:
//   Name = (Name == ""   || Name == Placeholder) ? NameDesign : Name
//   Desc = (Desc == ""   || Desc == Placeholder) ? Note       : Desc
// The "Name was empty or the placeholder" fact is retained (PlaceholderNamed) so ResolveLabel can prefer the game's
// own localized name for REAL buffs while still handing hidden / placeholder-named buffs a distinct NameDesign.
//
// The JSON is a top-level object keyed by buff id; each row has Id/Name/NameDesign/Desc/Note (+ many fields we skip).
// Parsed once on a BACKGROUND task (EnsureLoaded) into an id → (Name, Desc, PlaceholderNamed) map. System.Text.Json
// ignores unused columns.
internal static class TranslatedBuffText
{
    private const string Placeholder  = "Spirit Blade Thrust count";
    private const string ResourceName = "Stellar.CooldownBar.BuffTable.json";

    // Published once by the background loader (volatile: the main thread must see the fully built map, never a
    // half-built one). null = still loading.
    private static volatile Dictionary<int, (string Name, string Desc, bool PlaceholderNamed)>? _map;
    private static int     _started;        // 0 → 1 once the loader task is queued
    private static int     _loadedSignal;   // 1 = finished, not yet consumed by the main thread
    private static string  _loadMsg = "";

    // Manual per-effect overrides for buffs the game data resolves poorly (unnamed / no icon), copied verbatim from
    // TargetLens. baseId → (display name, imagine/skill id whose icon to borrow). IconSkill 3948 = "Arcane! Divine
    // Reliance" imagine icon. The name wins over the automatic NameDesign resolution; the icon is a Bar fallback.
    internal static readonly Dictionary<int, (string Name, int IconSkill)> EffectOverrides = new()
    {
        { 2110135, ("Rolora - Active Timer", 3948) },
        { 2110111, ("Rolora - Spell",        3948) },
        { 2110050, ("", 3957) },   // Element Stasis — no source skill of its own; borrow "Arcane! Fatal Spiral" (3957) icon
    };

    // Starts the one-time BACKGROUND parse (8.8 MB JSON — ~hundreds of ms that used to block the plugin constructor on
    // the main thread). Idempotent and cheap after the first call. The worker touches only the embedded resource
    // stream + managed collections (never IL2CPP/Unity), builds the whole map privately, then publishes it with ONE
    // volatile write — readers see either null (not ready → caller falls back to the game's own name/description,
    // the same path as an id missing from the table) or the complete map, never a half-built one.
    // Plugin's OnUpdate polls ConsumeLoaded() to re-resolve labels that were computed during the fallback window.
    internal static void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        Task.Run(Load);
    }

    private static void Load()
    {
        var sw  = Stopwatch.StartNew();
        var map = new Dictionary<int, (string, string, bool)>();
        string msg;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(ResourceName);
            if (s == null) msg = $"[CooldownBar] {ResourceName} not embedded";
            else
            {
                using var doc = JsonDocument.Parse(s);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var e = prop.Value;
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (!e.TryGetProperty("Id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                    int id = idEl.GetInt32();

                    string name = Str(e, "Name");
                    string desc = Str(e, "Desc");
                    bool placeholderNamed = name.Length == 0 || name == Placeholder;
                    if (placeholderNamed)               name = Str(e, "NameDesign");
                    if (desc.Length == 0 || desc == Placeholder) desc = Str(e, "Note");
                    map[id] = (name, desc, placeholderNamed);
                }
                msg = $"[CooldownBar] translated buff text loaded: {map.Count} entries in {sw.ElapsedMilliseconds} ms";
            }
        }
        catch (Exception ex)
        {
            msg = $"[CooldownBar] BuffTable.json parse failed: {ex.Message}";
        }
        _map = map;                               // volatile publish of the finished map (empty on failure)
        _loadMsg = msg;                           // logged by the main thread (ConsumeLoaded caller), not from here
        Volatile.Write(ref _loadedSignal, 1);     // OnUpdate → log + re-resolve labels on the main thread
    }

    /// <summary>True exactly once, on the first main-thread poll after the background parse finished; <paramref name="msg"/>
    /// is the load result line to log.</summary>
    internal static bool ConsumeLoaded(out string msg)
    {
        msg = "";
        if (_loadedSignal == 0 || Interlocked.Exchange(ref _loadedSignal, 0) == 0) return false;
        msg = _loadMsg;
        return true;
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // True + resolved strings when the id is present. Either string may be empty (caller falls back to game data).
    // While the background parse is still running this returns false (game-data fallback) — it never blocks.
    internal static bool TryGet(int id, out string name, out string desc)
    {
        name = ""; desc = "";
        var map = _map;
        if (map == null) { EnsureLoaded(); return false; }
        if (!map.TryGetValue(id, out var v)) return false;
        name = v.Name; desc = v.Desc;
        return true;
    }

    // Unified display-label resolver used by the picker, ingestion, the tile fallback, and the tooltip so every
    // surface shows the SAME label. A REAL buff keeps its localized game Name; a hidden / placeholder-named buff
    // gets the distinct NameDesign from the translated table (else the game name, else "#id").
    internal static string ResolveLabel(int baseId, string? gameName)
    {
        // Curated override wins (specific buffs the game data can't resolve), then the automatic chain below.
        if (EffectOverrides.TryGetValue(baseId, out var ov) && !string.IsNullOrEmpty(ov.Name)) return ov.Name;
        var map = _map;   // null while the background parse runs → game-name fallback below
        bool placeholder = map != null && map.TryGetValue(baseId, out var row) && row.PlaceholderNamed;
        if (!string.IsNullOrEmpty(gameName) && !placeholder) return gameName!;
        if (TryGet(baseId, out var tn, out _) && !string.IsNullOrEmpty(tn)) return tn;
        if (!string.IsNullOrEmpty(gameName)) return gameName!;
        return "#" + baseId;
    }

    // Description counterpart: the game's own description wins when present; else the translated Desc/Note.
    internal static string ResolveDesc(int baseId, string? gameDesc)
    {
        if (!string.IsNullOrEmpty(gameDesc)) return gameDesc!;
        if (TryGet(baseId, out _, out var td) && !string.IsNullOrEmpty(td)) return td;
        return gameDesc ?? "";
    }
}
