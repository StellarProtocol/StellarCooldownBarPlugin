using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// The HUD bar's element tree, house style (approved redesign 2026-11-13, frame A + tiles T1). Built ONCE; Funcs re-pull
// the live snapshot on the framework's refresh. Layout:
//   caption row (28 px): bold "COOLDOWNS" + muted "· N active" … round 28 px gear (toggles Settings)
//   up to 3 tile rows (Plugin.Bar.Tile.cs), the last visible one ending in a round "+N" chip on overflow
//   or, with nothing active, the muted "Nothing active right now" (the window keeps its size — it is resizable/fixed).
// The rounded theme panel behind all of it is the window ROOT's own Image, restyled in Plugin.Bar.Frame.cs, so it
// fills the whole window rect at any resize size (a Bubble here would sit inside the 12 px chrome padding).
// Every Func is wrapped in P(...) = CdPerf.Fn: the [CdPerf] "funcs" bucket when the probe is on, the bare lambda when off.
public sealed partial class Plugin
{
    // Kind colours are FIXED (they carry meaning, spec "Platform"): skill cooldown cyan, debuff red, buff green;
    // charge / stack gold.
    private static readonly ColorRgba CooldownCol = new(0.35f, 0.78f, 1.00f, 1f);   // #59C7FF
    private static readonly ColorRgba DebuffCol   = new(1.00f, 0.35f, 0.35f, 1f);   // #FF5959
    private static readonly ColorRgba BuffCol     = new(0.35f, 1.00f, 0.50f, 1f);   // #59FF80
    private static readonly ColorRgba ChargeCol   = new(1.00f, 0.84f, 0.35f, 1f);   // #FFD75A

    // Per-slot UV stash — Load* returns the sub-rect via out param; the tile's Uv Func reads it.
    private readonly UvRect[] _uv = new UvRect[TileCap];

    private const int MaxTilesPerRow = MaxTiles / MaxRows;
    private const float GearGlyph    = 16f;
    private const float OverflowChipH = 24f;

    private HudElement BuildRoot()
    {
        var rows = new HudElement[MaxRows];
        for (int r = 0; r < MaxRows; r++) rows[r] = BuildTileRow(r);

        var empty = new TextElement(P(() => _loc.T("cd.empty")), P<ColorRgba?>(() => MutedText()), NoWrap: true, Shadow: true)
            { Ellipsize = true };

        return new ColumnElement(new HudElement[]
        {
            BuildBarCaption(),
            // One child either way (a no-else Conditional collapses fully; this one always shows one arm), so the
            // caption → content gap is always exactly TileVGap.
            new ConditionalElement(P(() => _totalTileCount == 0), empty,
                new ColumnElement(rows, Gap: TileVGap)),
        }, Gap: TileVGap);
    }

    // Row r: slots c → tile r·_tilesPerRow + c, then the "+N" chip when this is the last visible row and tiles overflow.
    // A row collapses (no-else Conditional) when it is beyond the visible rows or has no tile in it.
    private HudElement BuildTileRow(int r)
    {
        var cells = new HudElement[MaxTilesPerRow + 1];
        for (int i = 0; i < MaxTilesPerRow; i++)
        {
            int c = i;
            cells[i] = new ConditionalElement(
                P(() => c < _tilesPerRow && r * _tilesPerRow + c < _tileCount),
                BuildTile(() => r * _tilesPerRow + c, BarGeom));   // Plugin.Bar.Tile.cs
        }
        cells[MaxTilesPerRow] = new ConditionalElement(
            P(() => _rowsVisible == r + 1 && _totalTileCount > _tileCount),
            OverflowChip());
        var row = new RowElement(cells, Gap: TileHGap);
        if (r == 0) return row;
        return new ConditionalElement(P(() => _rowsVisible > r && _tileCount > r * _tilesPerRow), row);
    }

    // "COOLDOWNS" (bold, UPPERCASE) + muted "· N active" (N = every active tile, overflow included) in a weighted,
    // ellipsized cell that is also the flexible spacer, then the round gear. The 28 px gear pins the row height.
    // Shadow (user-approved 2026-11-13): every bar text outside a tile card keeps the readability outline so it still reads
    // with Background at 0 %. ⚠️ A Shadow text takes the toolkit's LEGACY Text path (WindowBuilder.BuildText: Shadow skips
    // the TMP faces), so FontSize / RichEmoji are ignored there: these draw at the fixed 14 px, Bold = synthetic bold.
    private HudElement BuildBarCaption() => new RowElement(new HudElement[]
    {
        new TextElement(P(UpperCached(() => _loc.T("cd.barTitle"))), P<ColorRgba?>(() => TextColor()), NoWrap: true,
            FontSize: 12, Shadow: true) { Bold = true },
        new CellElement(new TextElement(P(ActiveCaption), P<ColorRgba?>(() => MutedText()), NoWrap: true, FontSize: 12, Shadow: true)
            { Ellipsize = true }, Weight: 1f),
        GearButton(),
    }, Gap: 6f);

    private readonly FmtCache _activeFmt = new();
    private string ActiveCaption() => _activeFmt.Get(_loc.T("cd.bar.active"), _totalTileCount);

    // Round 28 × 28 icon-only gear: clear at rest, a soft MenuText @10 % wash on hover (a round Bubble around a Bare
    // selectable — the default square wash must never show on a round control). Toggles the Settings window. The
    // embedded gear PNG (the ⚙ glyph has no in-game font coverage) is centred at 16 px.
    private HudElement GearButton()
    {
        var hover = new HoverFlag();
        float pad = (IconButtonSize - GearGlyph) / 2f;
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(pad, IconButtonSize),
                new ImageElement(P(() => CachedPng("settings-gear.png")), (int)GearGlyph, (int)GearGlyph),
                new SpacerElement(pad, IconButtonSize),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: ToggleSettings) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = P<ColorRgba?>(() => hover.On ? TextColor() with { A = HoverMix } : Transparent),
            CornerRadius = RoundRadius(IconButtonSize), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // Round "+N" chip: 24 px tall, MenuText @14 % fill, bold Text with the readability shadow (legacy 14 px, see above).
    // FIXED width (house R10: a changing label never resizes anything): the label sits in a box that fits the longest
    // case, "+99" (OverflowLabel caps there) = 23.7 px in the legacy 14 px bold face (Arial Bold: 1.696 em), so "+5" and
    // "+12" draw in the same 38 × 24 capsule. Align Center on a fixed-width box centres the label: the legacy path sets
    // alignByGeometry, which centres the drawn glyphs (CenterGlyphs is the TMP twin of that and is ignored on Shadow text).
    // The old natural-width chip sat directly in a Conditional with MaxWidth 0, so it latched its first width and "+12"
    // spilled off-centre (WindowBuilder-Patterns.md: give every fixed-width chip an explicit MaxWidth).
    private const float OverflowLabelW = 24f;
    private const float OverflowPadX   = 7f;

    private HudElement OverflowChip() => new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, OverflowChipH),
            new TextElement(P(OverflowLabel), P<ColorRgba?>(() => TextColor()), Width: OverflowLabelW, Align: TextAlign.Center,
                NoWrap: true, FontSize: 12, Shadow: true) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = P<ColorRgba?>(() => TextColor() with { A = 0.14f }),
        CornerRadius = RoundRadius(OverflowChipH), PaddingX = OverflowPadX, PaddingY = 0f,
        MaxWidth = OverflowLabelW + 2f * OverflowPadX,
    };

    private ColorRgba KindColor(int idx)
    {
        if (!SlotValid(idx)) return CooldownCol;
        return _tiles[idx].Kind switch
        {
            TileKind.Debuff => DebuffCol,
            TileKind.Buff   => BuffCol,
            _               => CooldownCol,
        };
    }

    // Per-frame fill, extrapolated from the tile's last 10 Hz sample (TrackedTile.FillAt). Not quantized: the user
    // chose smooth fills without pixel rounding (2026-11-12, "smooth, no item 8").
    private float TileFill(int idx)
    {
        if (!SlotValid(idx)) return 0f;
        return _tiles[idx].FillAt(Environment.TickCount64);
    }

    // Resolved display name for a tile — the source for the icon-less fallback badge. Skills use the game name;
    // buffs/debuffs route through the unified translated resolver so hidden effects get a distinct NameDesign label.
    // `known` = the game row exists, so the result is final and safe to cache (Plugin.Bar.Cache.cs TileAbbrev).
    private string TileName(int idx, out bool known)
    {
        known = false;
        if (!SlotValid(idx)) return "";
        var t = _tiles[idx];
        if (t.Kind == TileKind.Cooldown)
        {
            var sk = _services.GameData.Combat.GetSkill(t.Id);
            known = sk != null;
            return sk?.Name ?? "";
        }
        var bf = _services.GameData.Combat.GetBuff(t.Id);
        known = bf != null;
        return TranslatedBuffText.ResolveLabel(t.Id, bf?.Name);
    }

    // First two letter/digit characters of the (tag-stripped) name, uppercased. Drawn only when an effect has no icon,
    // so normal (icon-bearing) tiles are unaffected. (Copied from TargetLens.)
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
}
