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
    private static readonly ColorRgba BuffCol     = new(0.35f, 1.00f, 0.50f, 1f);   // green
    private static readonly ColorRgba MutedCol    = new(1.00f, 1.00f, 1.00f, 1f);   // white (borderless over world; relies on Shadow)

    // Per-slot UV stash — Load* returns the sub-rect via out param; the tile's Uv Func reads it.
    private readonly UvRect[] _uv = new UvRect[MaxTiles];

    private const int MaxTilesPerRow = MaxTiles / MaxRows;

    private HudElement BuildRoot()
    {
        var gear = new CellElement(
            new SelectableElement(
                new ImageElement(() => GearPng(), 15, 15),
                OnClick: () => _settings.SetVisible(!_settings.IsShown)),
            Width: 26f);
        var header = new RowElement(new HudElement[]
        {
            new TextElement(() => "Cooldowns", () => MutedCol, Shadow: true),
            new SpacerElement(),
            gear,
        });

        // Row 1: columns 0..MaxTilesPerRow-1 → tiles 0.._tilesPerRow-1
        var row1 = new HudElement[MaxTilesPerRow + 1];   // +1 for +N overflow label
        for (int i = 0; i < MaxTilesPerRow; i++)
        {
            int c = i;
            row1[i] = new ConditionalElement(
                () => c < _tilesPerRow && c < _tileCount,
                new CooldownTileElement(
                    Icon:        () => TileIcon(c),
                    Uv:          () => _uv[c],
                    Fill01:      () => c < _tileCount ? _tiles[c].Fill01 : 0f,
                    Seconds:     () => SecondsLabel(c),
                    Accent:      () => AccentColor(c),
                    IsImagine:   () => false,
                    ChargeCount: () => c < _tileCount ? _tiles[c].ChargeCount : 0)
                { OnClick = () => OnTileClick(c) });
        }
        row1[MaxTilesPerRow] = new ConditionalElement(
            () => _rowsVisible < 2 && _totalTileCount > _tileCount,
            new TextElement(() => $"+{_totalTileCount - _tileCount}", () => MutedCol, Shadow: true));

        // Row 2: columns 0..MaxTilesPerRow-1 → tiles _tilesPerRow.._tilesPerRow+col
        var row2 = new HudElement[MaxTilesPerRow + 1];
        for (int i = 0; i < MaxTilesPerRow; i++)
        {
            int c = i;
            row2[i] = new ConditionalElement(
                () => c < _tilesPerRow && _tilesPerRow + c < _tileCount,
                new CooldownTileElement(
                    Icon:        () => TileIcon(_tilesPerRow + c),
                    Uv:          () => _uv[_tilesPerRow + c],
                    Fill01:      () => { int ti = _tilesPerRow + c; return ti < _tileCount ? _tiles[ti].Fill01 : 0f; },
                    Seconds:     () => SecondsLabel(_tilesPerRow + c),
                    Accent:      () => AccentColor(_tilesPerRow + c),
                    IsImagine:   () => false,
                    ChargeCount: () => { int ti = _tilesPerRow + c; return ti < _tileCount ? _tiles[ti].ChargeCount : 0; })
                { OnClick = () => OnTileClick(_tilesPerRow + c) });
        }
        row2[MaxTilesPerRow] = new ConditionalElement(
            () => _rowsVisible == 2 && _totalTileCount > _tileCount,
            new TextElement(() => $"+{_totalTileCount - _tileCount}", () => MutedCol, Shadow: true));

        // Row 3: columns 0..MaxTilesPerRow-1 → tiles 2*_tilesPerRow..2*_tilesPerRow+col
        var row3 = new HudElement[MaxTilesPerRow + 1];
        for (int i = 0; i < MaxTilesPerRow; i++)
        {
            int c = i;
            row3[i] = new ConditionalElement(
                () => c < _tilesPerRow && _tilesPerRow * 2 + c < _tileCount,
                new CooldownTileElement(
                    Icon:        () => TileIcon(_tilesPerRow * 2 + c),
                    Uv:          () => _uv[_tilesPerRow * 2 + c],
                    Fill01:      () => { int ti = _tilesPerRow * 2 + c; return ti < _tileCount ? _tiles[ti].Fill01 : 0f; },
                    Seconds:     () => SecondsLabel(_tilesPerRow * 2 + c),
                    Accent:      () => AccentColor(_tilesPerRow * 2 + c),
                    IsImagine:   () => false,
                    ChargeCount: () => { int ti = _tilesPerRow * 2 + c; return ti < _tileCount ? _tiles[ti].ChargeCount : 0; })
                { OnClick = () => OnTileClick(_tilesPerRow * 2 + c) });
        }
        row3[MaxTilesPerRow] = new ConditionalElement(
            () => _rowsVisible >= 3 && _totalTileCount > _tileCount,
            new TextElement(() => $"+{_totalTileCount - _tileCount}", () => MutedCol, Shadow: true));

        var hint = new ConditionalElement(
            () => _tileCount == 0,
            new TextElement(() => "No active cooldowns — click the gear (top-right) to pick what to show", () => MutedCol, Shadow: true));

        var tileRows = new ColumnElement(new HudElement[]
        {
            new RowElement(row1, Gap: 6f),
            new ConditionalElement(() => _rowsVisible >= 2 && _tileCount > _tilesPerRow,
                new RowElement(row2, Gap: 6f)),
            new ConditionalElement(() => _rowsVisible >= 3 && _tileCount > _tilesPerRow * 2,
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
        // Buff/debuff: show the source skill's icon when known, fall back to the buff/debuff icon.
        if (t.IconSkillId > 0)
            return _services.GameAssets.LoadImagineIcon(t.IconSkillId, out _uv[idx])
                ?? _services.GameAssets.LoadSkillIcon(t.IconSkillId, out _uv[idx])
                ?? _services.GameAssets.LoadBuffIcon(t.Id, out _uv[idx]);
        return _services.GameAssets.LoadBuffIcon(t.Id, out _uv[idx]);
    }

    private string SecondsLabel(int idx)
    {
        if (idx >= _tileCount) return "";
        var t = _tiles[idx];
        if (t.RemainingMs < 0) return "∞";   // permanent — no expiry
        float secs = t.RemainingMs / 1000f;
        string time = secs >= 10f ? $"{(int)secs}s" : $"{secs:F1}s";
        if (t.Fallback) time = "*" + time;
        return time;
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
