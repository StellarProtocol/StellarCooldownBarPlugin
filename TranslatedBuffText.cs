using System;
using System.Collections.Generic;
using System.Text.Json;

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
// Parsed once, lazily, into an id → (Name, Desc, PlaceholderNamed) map. System.Text.Json ignores unused columns.
internal static class TranslatedBuffText
{
    private const string Placeholder  = "Spirit Blade Thrust count";
    private const string ResourceName = "Stellar.CooldownBar.BuffTable.json";

    private static Dictionary<int, (string Name, string Desc, bool PlaceholderNamed)>? _map;

    internal static void EnsureLoaded(Action<string>? log)
    {
        if (_map != null) return;
        var map = new Dictionary<int, (string, string, bool)>();
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(ResourceName);
            if (s == null) { log?.Invoke($"[CooldownBar] {ResourceName} not embedded"); _map = map; return; }
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
            log?.Invoke($"[CooldownBar] translated buff text loaded: {map.Count} entries");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[CooldownBar] BuffTable.json parse failed: {ex.Message}");
        }
        _map = map;
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // True + resolved strings when the id is present. Either string may be empty (caller falls back to game data).
    internal static bool TryGet(int id, out string name, out string desc)
    {
        name = ""; desc = "";
        EnsureLoaded(null);
        if (_map == null || !_map.TryGetValue(id, out var v)) return false;
        name = v.Name; desc = v.Desc;
        return true;
    }

    // Unified display-label resolver used by the picker, ingestion, the tile fallback, and the tooltip so every
    // surface shows the SAME label. A REAL buff keeps its localized game Name; a hidden / placeholder-named buff
    // gets the distinct NameDesign from the translated table (else the game name, else "#id").
    internal static string ResolveLabel(int baseId, string? gameName)
    {
        EnsureLoaded(null);
        bool placeholder = _map != null && _map.TryGetValue(baseId, out var row) && row.PlaceholderNamed;
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
