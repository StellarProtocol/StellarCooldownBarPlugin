using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// The tile T1 ("soft card"), built from plain toolkit elements (replaces the framework CooldownTileElement). At 100 %:
//   Bubble r6, padding 3, fill = kind colour @16 % over Panel, then @90 % alpha; 1 px ring = kind colour @55 %
//   (OutlineColorFn, overlaid: never measured) → Bare Selectable (click = the tile tooltip) → Column, gap 2:
//     • icon: GameTexture 40×40, CornerRadius 5 (stencil-masked) — or, icon-less, the 2-letter fallback centred in a
//       40×40 Tile-coloured rounded box;
//     • fill bar: BarElement 40×4 (smooth, per frame, unquantised — TrackedTile.FillAt);
//     • footer, 16 px, in a FIXED 44 px cell: bold seconds in the kind colour, and only while there is a count to show, a
//       gold chip (14 tall, r6, gold @25 %, bold 10 gold). With a chip the seconds sit in a fixed right-aligned box
//       beside it and the pair is centred; without, the seconds centre in the whole 44 px.
// Sizes (Placed mode, 75–200 %): the toolkit can't scale a window, so every number above is multiplied by the size and
// rounded to whole px in a TileGeom, and each size is BUILT separately (icon, bar, fonts, chip, padding re-laid out —
// never stretched). The bar always uses TileGeom 100 % with the shadow.
// Shadow (bar only, user 2026-11-13): the seconds get the readability outline. A Shadow text is the toolkit's LEGACY
// path — fixed 14 px synthetic bold, FontSize ignored (Plugin.Bar.cs caption note) — so it can't scale; Placed tiles keep
// the TMP bold at the scaled size instead (they sit on their own card, there is no Background setting for them).
// Every size is pinned (struts / fixed widths / Text.Height), so a ticking label, a chip appearing or a load-late icon
// never resizes the tile or moves a neighbour. Bar geometry numbers: Plugin.Seen.cs.
//
// Redraw cost (Plugin-Performance-Rules §5): the BarElement carries the framework's own nested "live" canvas
// (WindowBuilder.Preview.cs BuildBar → AddLiveCanvas), so the per-frame fill re-batches only that bar. The other parts
// have no live canvas of their own (the toolkit gives one only to Bar / MeterRow / CooldownTile), so a seconds text
// change re-batches the whole window's canvas. Text is written only when the cached string changes (TextBinding diffs;
// SecondsLabel re-formats only on a CdTimeFormat.Key change): ≤ 10 Hz while a tile is under 10 s, 1 Hz under 100 s.
// Colours are re-polled per refresh but the Graphic setters skip equal values.
public sealed partial class Plugin
{
    private const float CardWashAlpha  = 0.16f;
    private const float CardAlpha      = 0.90f;
    private const float CardRingAlpha  = 0.55f;
    private const float ChipWashAlpha  = 0.25f;

    // Whole-px tile geometry at one size. W = 2·Pad + InnerW (the footer cell pins the card width).
    private sealed class TileGeom
    {
        public readonly float Pad, InnerW, W, Icon, BarH, FooterH, ColGap, SecsBoxW, ChipH, ChipDigitW, ChipPadX, Radius;
        public readonly int IconPx, IconRadius, SecsFont, ChipFont, AbbrevFont;
        public readonly bool Shadow;

        public TileGeom(float s, bool shadow)
        {
            Shadow = shadow;
            Pad = Px(3f * s); InnerW = Px(44f * s); W = 2f * Pad + InnerW;
            IconPx = (int)Px(40f * s); Icon = IconPx; IconRadius = (int)Px(5f * s);
            BarH = MathF.Max(2f, Px(4f * s)); FooterH = Px(16f * s); ColGap = MathF.Max(1f, Px(2f * s));
            // Beside a chip: "9.8s" bold 12 ≈ 20–23 → 24; the shadowed legacy 14 px ≈ 26 → 28 (28 + 2 + 14 = 44).
            SecsBoxW = shadow ? 28f : Px(24f * s);
            ChipH = Px(14f * s); ChipDigitW = Px(8f * s); ChipPadX = MathF.Max(2f, Px(3f * s)); Radius = Px(6f * s);
            SecsFont = (int)Px(12f * s); ChipFont = (int)Px(10f * s); AbbrevFont = (int)Px(14f * s);
        }

        private static float Px(float v) => MathF.Round(v, MidpointRounding.AwayFromZero);
    }

    // The bar's tile (100 %, shadowed seconds). Its outer size is TileW × TileH (Plugin.Seen.cs).
    private static readonly TileGeom BarGeom = new(1f, shadow: true);

    // `slot` = the _tiles index this tile shows (re-read every refresh). `fade` (Placed edit-mode samples): an alpha
    // multiplier on every fill / ring / text colour; null = solid (the bar: one null check per colour poll).
    private HudElement BuildTile(Func<int> slot, TileGeom g, Func<float>? fade = null) => new BubbleElement(new SelectableElement(
        new ColumnElement(new HudElement[]
        {
            TileIconArea(slot, g, fade),
            new RowElement(new HudElement[]
            {
                new BarElement(P(() => TileFill(slot())), CooldownCol)
                {
                    Width = g.Icon, Height = g.BarH,
                    FillColorFn = P<ColorRgba?>(() => Faded(KindColor(slot()), fade)),
                },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            new CellElement(TileFooter(slot, g, fade), Width: g.InnerW),
        }, Gap: g.ColGap),
        OnClick: () => OnTileClick(slot())) { Bare = true })
    {
        // The HUD is not a GlassMenu window: no window-opacity Fade (the panel's own opacity is the Background slider).
        FillColorFn    = P<ColorRgba?>(() => Faded(Over(KindColor(slot()) with { A = CardWashAlpha }, PanelColor()) with { A = CardAlpha }, fade)),
        OutlineColorFn = P<ColorRgba?>(() => Faded(KindColor(slot()) with { A = CardRingAlpha }, fade)),
        CornerRadius = g.Radius, PaddingX = g.Pad, PaddingY = g.Pad, MaxWidth = g.W,
    };

    private static ColorRgba Faded(ColorRgba c, Func<float>? fade) => fade == null ? c : c with { A = c.A * fade() };

    // The icon, or (icon-less: no texture after the resolver's whole fallback chain) the 2-letter badge in a Tile box.
    // TileIcon(i) is the cached resolver (Plugin.Bar.Cache.cs) and keeps retrying a miss, so a late-loading icon swaps
    // in on its own. Both arms are exactly Icon × Icon inside a centring Row (a bare picture in a Column is stretched).
    // (GameTextureElement has no tint: a faded sample's icon is dimmed on the Unity side, Plugin.Placed.Fade.cs.)
    private HudElement TileIconArea(Func<int> slot, TileGeom g, Func<float>? fade) => new RowElement(new HudElement[]
    {
        new ConditionalElement(P(() => TileIcon(slot()) != null),
            new GameTextureElement(P(() => TileIcon(slot())), g.IconPx, g.IconPx, P(() => _uv[slot()]))
                { CornerRadius = g.IconRadius, Fill = true },
            new BubbleElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, g.Icon),
                    new TextElement(P(() => TileAbbrev(slot())), P<ColorRgba?>(() => Faded(KindColor(slot()), fade)),
                        Width: g.Icon, Align: TextAlign.Center, NoWrap: true, FontSize: g.AbbrevFont) { Bold = true },
                }, Gap: 0.01f, Justify: RowJustify.Center))
            {
                FillColorFn = P<ColorRgba?>(() => Faded(TileColor(), fade)),
                CornerRadius = g.IconRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = g.Icon,
            }),
    }, Gap: 0.01f, Justify: RowJustify.Center);

    // Footer: seconds alone (centred in InnerW) or seconds + gold chip (centred together). Two complete arms, both inside
    // the caller's fixed InnerW cell, so switching never changes the card's width.
    private HudElement TileFooter(Func<int> slot, TileGeom g, Func<float>? fade) => new ConditionalElement(
        P(() => TileChipCount(slot()) > 0),
        new RowElement(new HudElement[]
        {
            SecondsText(slot, g, fade, g.SecsBoxW, TextAlign.Right),
            TileChip(slot, g, fade),
        }, Gap: g.ColGap, Justify: RowJustify.Center),
        SecondsText(slot, g, fade, g.InnerW, TextAlign.Center));

    private HudElement SecondsText(Func<int> slot, TileGeom g, Func<float>? fade, float width, TextAlign align)
        => new TextElement(P(() => SecondsLabel(slot())), P<ColorRgba?>(() => Faded(KindColor(slot()), fade)),
            Width: width, Align: align, NoWrap: true, FontSize: g.SecsFont, Shadow: g.Shadow) { Bold = true, Height = g.FooterH };

    // Gold chip, ChipH tall, r6·s: one digit → a fixed digit box (14 px chip at 100 %); two or more → the label hugs it.
    private HudElement TileChip(Func<int> slot, TileGeom g, Func<float>? fade)
    {
        HudElement Chip(float labelW) => new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, g.ChipH),
                new TextElement(P(() => ChipLabel(slot())), P<ColorRgba?>(() => Faded(ChargeCol, fade)), Width: labelW,
                    Align: TextAlign.Center, NoWrap: true, FontSize: g.ChipFont) { Bold = true, Height = g.ChipH },
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = P<ColorRgba?>(() => Faded(ChargeCol with { A = ChipWashAlpha }, fade)),
            CornerRadius = g.Radius, PaddingX = g.ChipPadX, PaddingY = 0f,
        };
        return new ConditionalElement(P(() => TileChipCount(slot()) < 10), Chip(g.ChipDigitW), Chip(0f));
    }

    // The number in the gold chip, 0 = no chip. Skill cooldowns: the Imagine's pending charges (TrackedTile.ChargeCount
    // is set only for multi-charge skills), shown from 1. Buffs / debuffs: the stack count, shown from 2 — every
    // effect has a layer count of at least 1, so "1" on every buff would be noise (the old tile also hid a lone stack).
    private int TileChipCount(int idx)
    {
        if (!SlotValid(idx)) return 0;
        var t = _tiles[idx];
        int n = t.ChargeCount;
        return t.Kind == TileKind.Cooldown ? (n > 0 ? n : 0) : (n > 1 ? n : 0);
    }
}
