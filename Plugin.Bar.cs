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
public sealed partial class Plugin
{
    private static readonly ColorRgba CooldownCol = new(0.35f, 0.78f, 1.00f, 1f);   // cyan
    private static readonly ColorRgba DebuffCol   = new(1.00f, 0.35f, 0.35f, 1f);   // red
    private static readonly ColorRgba MutedCol    = new(1.00f, 1.00f, 1.00f, 1f);   // white (borderless over world; relies on Shadow)

    // Per-slot UV stash — Load* returns the sub-rect via out param; the tile's Uv Func reads it.
    private readonly UvRect[] _uv = new UvRect[MaxTiles];

    // Persistent panel (StatInspector mini-HUD pattern): a header strip with a title + a gear button that opens
    // the picker (so settings are reachable without the F8 hotkey), a divider, then the active-only tile strip
    // (or a "pick what to track" hint when idle). Always visible → discoverable + movable in layout edit-mode.
    private HudElement BuildRoot()
    {
        // Gear icon (the SAME settings-gear.png StatInspector uses) in a fixed cell — a ⚙ glyph has no in-game
        // font coverage, but the PNG renders reliably. SelectableElement force-expands its child, so the CellElement
        // pins the 15px icon's width (else it streaks). Click toggles the picker (no F8 needed).
        var gear = new CellElement(
            new SelectableElement(
                new ImageElement(() => GearPng(), 15, 15),
                OnClick: () => _settings.SetVisible(!_settings.IsShown)),
            Width: 26f);   // matches StatInspector mini-HUD gear cell exactly
        var header = new RowElement(new HudElement[]
        {
            new TextElement(() => "Cooldowns", () => MutedCol, Shadow: true),
            new SpacerElement(),
            gear,
        });

        var slots = new HudElement[MaxTiles];
        for (int i = 0; i < MaxTiles; i++)
        {
            int idx = i;   // capture per-slot index
            slots[i] = new ConditionalElement(
                () => idx < _tileCount,
                new CooldownTileElement(
                    Icon:        () => TileIcon(idx),
                    Uv:          () => _uv[idx],
                    Fill01:      () => idx < _tileCount ? _tiles[idx].Fill01 : 0f,
                    Seconds:     () => SecondsLabel(idx),
                    Accent:      () => AccentColor(idx),
                    IsImagine:   () => idx < _tileCount && _tiles[idx].IsImagine,
                    ChargeCount: () => idx < _tileCount ? _tiles[idx].ChargeCount : 0));
        }
        var body = new ConditionalElement(
            () => _tileCount > 0,
            new RowElement(slots, Gap: 6f),
            new TextElement(() => "No active cooldowns — click the gear (top-right) to pick what to show", () => MutedCol, Shadow: true));

        return new ColumnElement(new HudElement[] { header, new SeparatorElement(), body }, Gap: 2f);
    }

    private ColorRgba AccentColor(int idx)
        => idx < _tileCount && _tiles[idx].Kind == TileKind.Debuff ? DebuffCol : CooldownCol;

    private object? TileIcon(int idx)
    {
        if (idx >= _tileCount) { _uv[idx] = new UvRect(0f, 0f, 1f, 1f); return null; }
        var t = _tiles[idx];
        if (t.IsImagine)                 return _services.GameAssets.LoadImagineIcon(t.IconSkillId, out _uv[idx]);
        if (t.Kind == TileKind.Cooldown) return _services.GameAssets.LoadSkillIcon(t.Id, out _uv[idx]);
        return _services.GameAssets.LoadBuffIcon(t.Id, out _uv[idx]);
    }

    private string SecondsLabel(int idx)
    {
        if (idx >= _tileCount) return "";
        var t = _tiles[idx];
        float secs = t.RemainingMs / 1000f;
        string s = secs >= 10f ? $"{(int)secs}s" : $"{secs:F1}s";
        if (t.Fallback) s = "*" + s;
        return s;
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
