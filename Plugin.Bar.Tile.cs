using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// The bar tile T1 ("soft card"), built from plain toolkit elements (replaces the framework CooldownTileElement):
//   Bubble r6, padding 3, fill = kind colour @16 % over Panel, then @90 % alpha; 1 px ring = kind colour @55 %
//   (OutlineColorFn, overlaid: never measured) → Bare Selectable (click = the tile tooltip) → Column, gap 2:
//     • icon: GameTexture 40×40, CornerRadius 5 (stencil-masked) — or, icon-less, the 2-letter fallback centred in a
//       40×40 Tile-coloured rounded box;
//     • fill bar: BarElement 40×4 (smooth, per frame, unquantised — TrackedTile.FillAt);
//     • footer, 16 px, in a FIXED 44 px cell: bold 12 seconds in the kind colour, and only while there is a count to
//       show, a gold chip (14 tall, r6, gold @25 %, bold 10 gold). With a chip the seconds sit in a fixed 24 px
//       right-aligned box beside it and the pair is centred; without, the seconds centre in the whole 44 px.
// Every size is pinned (struts / fixed widths / Text.Height), so a ticking label, a chip appearing or a load-late icon
// never resizes the tile or moves a neighbour. Geometry numbers: Plugin.Seen.cs.
//
// Redraw cost (Plugin-Performance-Rules §5): the BarElement carries the framework's own nested "live" canvas
// (WindowBuilder.Preview.cs BuildBar → AddLiveCanvas), so the per-frame fill re-batches only that 40×4 bar. The other
// parts have no live canvas of their own (the toolkit gives one only to Bar / MeterRow / CooldownTile), so a seconds
// text change re-batches the whole bar window's canvas. Text is written only when the cached string changes
// (TextBinding diffs; SecondsLabel re-formats only on a CdTimeFormat.Key change): ≤ 10 Hz while a tile is under 10 s,
// 1 Hz under 100 s. Colours are re-polled per refresh but the Graphic setters skip equal values.
public sealed partial class Plugin
{
    private const float TileIconSize   = 40f;
    private const int   TileIconRadius = 5;
    private const float TilePad        = 3f;
    private const float TileInnerW     = TileW - 2f * TilePad;   // 44: the footer cell pins the card width
    private const float TileBarH       = 4f;
    private const float TileFooterH    = 16f;
    private const float TileColGap     = 2f;
    private const float TileSecsBoxW   = 24f;   // seconds box beside a chip ("57s" / "9.8s" bold 12 ≈ 20–23)
    private const float TileChipH      = 14f;
    private const float TileChipDigitW = 8f;    // 1-digit chip: 8 + 2×3 padding = the spec's 14 px minimum width
    private const float CardWashAlpha  = 0.16f;
    private const float CardAlpha      = 0.90f;
    private const float CardRingAlpha  = 0.55f;
    private const float ChipWashAlpha  = 0.25f;

    private HudElement BuildTile(Func<int> slot) => new BubbleElement(new SelectableElement(
        new ColumnElement(new HudElement[]
        {
            TileIconArea(slot),
            new RowElement(new HudElement[]
            {
                new BarElement(P(() => TileFill(slot())), CooldownCol)
                {
                    Width = TileIconSize, Height = TileBarH,
                    FillColorFn = P<ColorRgba?>(() => KindColor(slot())),
                },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            new CellElement(TileFooter(slot), Width: TileInnerW),
        }, Gap: TileColGap),
        OnClick: () => OnTileClick(slot())) { Bare = true })
    {
        // The HUD is not a GlassMenu window: no window-opacity Fade (the panel's own opacity is the Background slider).
        FillColorFn    = P<ColorRgba?>(() => Over(KindColor(slot()) with { A = CardWashAlpha }, PanelColor()) with { A = CardAlpha }),
        OutlineColorFn = P<ColorRgba?>(() => KindColor(slot()) with { A = CardRingAlpha }),
        CornerRadius = TileRadius, PaddingX = TilePad, PaddingY = TilePad, MaxWidth = TileW,
    };

    // The icon, or (icon-less: no texture after the resolver's whole fallback chain) the 2-letter badge in a Tile box.
    // TileIcon(i) is the cached resolver (Plugin.Bar.Cache.cs) and keeps retrying a miss, so a late-loading icon swaps
    // in on its own. Both arms are exactly 40 × 40 inside a centring Row (a bare picture in a Column is stretched).
    private HudElement TileIconArea(Func<int> slot) => new RowElement(new HudElement[]
    {
        new ConditionalElement(P(() => TileIcon(slot()) != null),
            new GameTextureElement(P(() => TileIcon(slot())), (int)TileIconSize, (int)TileIconSize, P(() => _uv[slot()]))
                { CornerRadius = TileIconRadius, Fill = true },
            new BubbleElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, TileIconSize),
                    new TextElement(P(() => TileAbbrev(slot())), P<ColorRgba?>(() => KindColor(slot())),
                        Width: TileIconSize, Align: TextAlign.Center, NoWrap: true, FontSize: 14) { Bold = true },
                }, Gap: 0.01f, Justify: RowJustify.Center))
            {
                FillColorFn = P<ColorRgba?>(() => TileColor()),
                CornerRadius = TileIconRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = TileIconSize,
            }),
    }, Gap: 0.01f, Justify: RowJustify.Center);

    // Footer: seconds alone (centred in 44) or seconds + gold chip (centred together). Two complete arms, both inside
    // the caller's fixed 44 px cell, so switching never changes the card's width.
    private HudElement TileFooter(Func<int> slot) => new ConditionalElement(P(() => TileChipCount(slot()) > 0),
        new RowElement(new HudElement[]
        {
            SecondsText(slot, TileSecsBoxW, TextAlign.Right),
            TileChip(slot),
        }, Gap: 2f, Justify: RowJustify.Center),
        SecondsText(slot, TileInnerW, TextAlign.Center));

    private HudElement SecondsText(Func<int> slot, float width, TextAlign align)
        => new TextElement(P(() => SecondsLabel(slot())), P<ColorRgba?>(() => KindColor(slot())),
            Width: width, Align: align, NoWrap: true, FontSize: 12) { Bold = true, Height = TileFooterH };

    // Gold chip, 14 px tall, r6: one digit → a fixed 8 px label box (14 px chip); two or more → the label hugs its text.
    private HudElement TileChip(Func<int> slot)
    {
        HudElement Chip(float labelW) => new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, TileChipH),
                new TextElement(P(() => ChipLabel(slot())), P<ColorRgba?>(() => ChargeCol), Width: labelW,
                    Align: TextAlign.Center, NoWrap: true, FontSize: 10) { Bold = true, Height = TileChipH },
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = P<ColorRgba?>(() => ChargeCol with { A = ChipWashAlpha }),
            CornerRadius = TileRadius, PaddingX = 3f, PaddingY = 0f,
        };
        return new ConditionalElement(P(() => TileChipCount(slot()) < 10), Chip(TileChipDigitW), Chip(0f));
    }

    // The number in the gold chip, 0 = no chip. Skill cooldowns: the Imagine's pending charges (TrackedTile.ChargeCount
    // is set only for multi-charge skills), shown from 1. Buffs / debuffs: the stack count, shown from 2 — every
    // effect has a layer count of at least 1, so "1" on every buff would be noise (the old tile also hid a lone stack).
    private int TileChipCount(int idx)
    {
        if (idx >= _tileCount) return 0;
        var t = _tiles[idx];
        int n = t.ChargeCount;
        return t.Kind == TileKind.Cooldown ? (n > 0 ? n : 0) : (n > 1 ? n : 0);
    }
}
