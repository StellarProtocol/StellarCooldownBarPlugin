using System;
using System.IO;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// The overlay element tree. Built ONCE; Funcs re-pull the live snapshot on the framework's capped refresh.
// Active-only: a slot shows only while idx < _tileCount. Each active slot is a bespoke CooldownTileElement
// (icon + accent outline + foot fill-bar + seconds + ★/charge badges) — cooldown = cyan, debuff = red.
// Imagine-lockout debuffs render the source Imagine's artwork + a ★ badge.
// Every Func is wrapped in P(...) = CdPerf.Fn: the [CdPerf] "funcs" bucket when the probe is on, the bare lambda when off.
public sealed partial class Plugin
{
    private static readonly ColorRgba CooldownCol = new(0.35f, 0.78f, 1.00f, 1f);   // cyan
    private static readonly ColorRgba DebuffCol   = new(1.00f, 0.35f, 0.35f, 1f);   // red
    private static readonly ColorRgba BuffCol     = new(0.35f, 1.00f, 0.50f, 1f);   // green
    private static readonly ColorRgba MutedCol    = new(1.00f, 1.00f, 1.00f, 1f);   // white (borderless over world; relies on Shadow)

    // Per-slot UV stash — Load* returns the sub-rect via out param; the tile's Uv Func reads it.
    private readonly UvRect[] _uv = new UvRect[MaxTiles];

    private const int MaxTilesPerRow = MaxTiles / MaxRows;

    private HudElement BuildRoot()
    {
        var gear = new CellElement(
            new SelectableElement(
                new ImageElement(P(() => GearPng()), 15, 15),
                OnClick: () => _settings.SetVisible(!_settings.IsShown)),
            Width: 26f);
        var header = new RowElement(new HudElement[]
        {
            new TextElement(P(() => _loc.T("cd.barTitle")), P<ColorRgba?>(() => MutedCol), Shadow: true),
            new SpacerElement(),
            gear,
        });

        // Row 1: columns 0..MaxTilesPerRow-1 → tiles 0.._tilesPerRow-1
        var row1 = new HudElement[MaxTilesPerRow + 1];   // +1 for +N overflow label
        for (int i = 0; i < MaxTilesPerRow; i++)
        {
            int c = i;
            row1[i] = new ConditionalElement(
                P(() => c < _tilesPerRow && c < _tileCount),
                new CooldownTileElement(
                    Icon:        P(() => TileIcon(c)),
                    Uv:          P(() => _uv[c]),
                    Fill01:      P(() => TileFill(c)),
                    Seconds:     P(() => SecondsLabel(c)),
                    Accent:      P(() => AccentColor(c)),
                    IsImagine:   P(() => false),
                    ChargeCount: P(() => c < _tileCount ? _tiles[c].ChargeCount : 0))
                { OnClick = () => OnTileClick(c), FallbackLabel = P(() => Abbrev(TileName(c))) });
        }
        row1[MaxTilesPerRow] = new ConditionalElement(
            P(() => _rowsVisible < 2 && _totalTileCount > _tileCount),
            new TextElement(P(() => $"+{_totalTileCount - _tileCount}"), P<ColorRgba?>(() => MutedCol), Shadow: true));

        // Row 2: columns 0..MaxTilesPerRow-1 → tiles _tilesPerRow.._tilesPerRow+col
        var row2 = new HudElement[MaxTilesPerRow + 1];
        for (int i = 0; i < MaxTilesPerRow; i++)
        {
            int c = i;
            row2[i] = new ConditionalElement(
                P(() => c < _tilesPerRow && _tilesPerRow + c < _tileCount),
                new CooldownTileElement(
                    Icon:        P(() => TileIcon(_tilesPerRow + c)),
                    Uv:          P(() => _uv[_tilesPerRow + c]),
                    Fill01:      P(() => TileFill(_tilesPerRow + c)),
                    Seconds:     P(() => SecondsLabel(_tilesPerRow + c)),
                    Accent:      P(() => AccentColor(_tilesPerRow + c)),
                    IsImagine:   P(() => false),
                    ChargeCount: P(() => { int ti = _tilesPerRow + c; return ti < _tileCount ? _tiles[ti].ChargeCount : 0; }))
                { OnClick = () => OnTileClick(_tilesPerRow + c), FallbackLabel = P(() => Abbrev(TileName(_tilesPerRow + c))) });
        }
        row2[MaxTilesPerRow] = new ConditionalElement(
            P(() => _rowsVisible == 2 && _totalTileCount > _tileCount),
            new TextElement(P(() => $"+{_totalTileCount - _tileCount}"), P<ColorRgba?>(() => MutedCol), Shadow: true));

        // Row 3: columns 0..MaxTilesPerRow-1 → tiles 2*_tilesPerRow..2*_tilesPerRow+col
        var row3 = new HudElement[MaxTilesPerRow + 1];
        for (int i = 0; i < MaxTilesPerRow; i++)
        {
            int c = i;
            row3[i] = new ConditionalElement(
                P(() => c < _tilesPerRow && _tilesPerRow * 2 + c < _tileCount),
                new CooldownTileElement(
                    Icon:        P(() => TileIcon(_tilesPerRow * 2 + c)),
                    Uv:          P(() => _uv[_tilesPerRow * 2 + c]),
                    Fill01:      P(() => TileFill(_tilesPerRow * 2 + c)),
                    Seconds:     P(() => SecondsLabel(_tilesPerRow * 2 + c)),
                    Accent:      P(() => AccentColor(_tilesPerRow * 2 + c)),
                    IsImagine:   P(() => false),
                    ChargeCount: P(() => { int ti = _tilesPerRow * 2 + c; return ti < _tileCount ? _tiles[ti].ChargeCount : 0; }))
                { OnClick = () => OnTileClick(_tilesPerRow * 2 + c), FallbackLabel = P(() => Abbrev(TileName(_tilesPerRow * 2 + c))) });
        }
        row3[MaxTilesPerRow] = new ConditionalElement(
            P(() => _rowsVisible >= 3 && _totalTileCount > _tileCount),
            new TextElement(P(() => $"+{_totalTileCount - _tileCount}"), P<ColorRgba?>(() => MutedCol), Shadow: true));

        var hint = new ConditionalElement(
            P(() => _tileCount == 0),
            new TextElement(P(() => _loc.T("cd.empty")), P<ColorRgba?>(() => MutedCol), Shadow: true));

        var tileRows = new ColumnElement(new HudElement[]
        {
            new RowElement(row1, Gap: 6f),
            new ConditionalElement(P(() => _rowsVisible >= 2 && _tileCount > _tilesPerRow),
                new RowElement(row2, Gap: 6f)),
            new ConditionalElement(P(() => _rowsVisible >= 3 && _tileCount > _tilesPerRow * 2),
                new RowElement(row3, Gap: 6f)),
        }, Gap: 6f);

        return new ColumnElement(new HudElement[]
        {
            header,
            new SeparatorElement(),
            hint,
            tileRows,
        }, Gap: 2f);
    }

    private ColorRgba AccentColor(int idx)
    {
        if (idx >= _tileCount) return CooldownCol;
        return _tiles[idx].Kind switch
        {
            TileKind.Debuff => DebuffCol,
            TileKind.Buff   => BuffCol,
            _               => CooldownCol,
        };
    }

    private object? TileIcon(int idx)
    {
        if (idx >= _tileCount) { _uv[idx] = new UvRect(0f, 0f, 1f, 1f); return null; }
        var t = _tiles[idx];
        if (t.Kind == TileKind.Cooldown)
            return _services.GameAssets.LoadImagineIcon(t.IconSkillId, out _uv[idx])
                ?? _services.GameAssets.LoadSkillIcon(t.Id, out _uv[idx]);
        // Buff/debuff: source skill's icon when known, then a curated EffectOverride icon (for icon-less buffs with
        // no source skill — borrows an imagine/skill icon), falling back to the buff/debuff's own icon.
        if (t.IconSkillId > 0)
        {
            var bySkill = _services.GameAssets.LoadImagineIcon(t.IconSkillId, out _uv[idx])
                       ?? _services.GameAssets.LoadSkillIcon(t.IconSkillId, out _uv[idx]);
            if (bySkill != null) return bySkill;
        }
        if (TranslatedBuffText.EffectOverrides.TryGetValue(t.Id, out var ov) && ov.IconSkill > 0)
        {
            var byOverride = _services.GameAssets.LoadImagineIcon(ov.IconSkill, out _uv[idx])
                          ?? _services.GameAssets.LoadSkillIcon(ov.IconSkill, out _uv[idx]);
            if (byOverride != null) return byOverride;
        }
        return _services.GameAssets.LoadBuffIcon(t.Id, out _uv[idx]);
    }

    // Per-frame fill, extrapolated from the tile's last 10 Hz sample (TrackedTile.FillAt). Quantized to 1/88 (half a
    // canvas px of the 44 px foot bar — invisible) so a slow timer doesn't re-mesh its tile every frame
    // (Plugin-Performance-Rules.md §5: the tile's graphic only changes when the shown fill moves).
    private const float FillSteps = 88f;
    private float TileFill(int idx)
    {
        if (idx >= _tileCount) return 0f;
        return MathF.Round(_tiles[idx].FillAt(Environment.TickCount64) * FillSteps) / FillSteps;
    }

    private string SecondsLabel(int idx)
    {
        if (idx >= _tileCount) return "";
        var t = _tiles[idx];
        if (t.Permanent) return "∞";   // permanent — no expiry
        // Truncate to whole ms like the old (int)(rem * 1000) snapshot so the formatted value is unchanged.
        float secs = (int)(t.RemainAt(Environment.TickCount64) * 1000f) / 1000f;
        string time = secs > 999f ? $"{(int)(secs / 60f)}m"
                    : secs >= 10f  ? $"{(int)secs}s"
                    :                $"{secs:F1}s";
        if (t.Fallback) time = "*" + time;
        return time;
    }

    // Resolved display name for a tile — the source for the icon-less fallback badge. Skills use the game name;
    // buffs/debuffs route through the unified translated resolver so hidden effects get a distinct NameDesign label.
    private string TileName(int idx)
    {
        if (idx >= _tileCount) return "";
        var t = _tiles[idx];
        if (t.Kind == TileKind.Cooldown)
            return _services.GameData.Combat.GetSkill(t.Id)?.Name ?? "";
        return TranslatedBuffText.ResolveLabel(t.Id, _services.GameData.Combat.GetBuff(t.Id)?.Name);
    }

    // First two letter/digit characters of the (tag-stripped) name, uppercased. The framework draws this only when
    // an effect has no icon, so normal (icon-bearing) tiles are unaffected. (Copied from TargetLens.)
    private static string Abbrev(string name)
    {
        name = StripTags(name);
        char a = '\0', b = '\0';
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c)) continue;
            if (a == '\0') a = char.ToUpperInvariant(c);
            else { b = char.ToUpperInvariant(c); break; }
        }
        if (a == '\0') return "";
        return b == '\0' ? a.ToString() : new string(new[] { a, b });
    }

    // Embedded settings-gear PNG bytes (cached). Same resource StatInspector ships; the ⚙ glyph has no in-game
    // font coverage, so a PNG icon is the reliable gear. Null if the resource is missing → ImageElement draws nothing.
    private byte[]? _gearPng;
    private bool _gearFailed;
    private byte[]? GearPng()
    {
        if (_gearPng != null || _gearFailed) return _gearPng;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Stellar.CooldownBar.settings-gear.png");
            if (s == null) { _gearFailed = true; return null; }
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            _gearPng = ms.ToArray();
        }
        catch { _gearFailed = true; }
        return _gearPng;
    }
}
