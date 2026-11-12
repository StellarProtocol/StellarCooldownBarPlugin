using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// More house-kit building blocks (Stellar Design Language §4; ported from TargetLens Plugin.Style.Blocks.cs): embedded
// art, the accent icon tile, ToolHeader, the clickable 24 px chips (On/Off + choice), the round-knob slider, filled list
// slots, the round ✕ clear button, and a cached string formatter so the per-refresh text Funcs never build a string
// unless their inputs changed (Plugin-Performance-Rules §2).
public sealed partial class Plugin
{
    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource (csproj EmbeddedResource LogicalName "Stellar.CooldownBar.<file>") and kept: ImageElement
    // Funcs are polled. A missing resource caches null (the element then draws nothing) instead of re-reading every poll.
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string file)
    {
        if (_pngCache.TryGetValue(file, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.CooldownBar." + file);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); png = ms.ToArray(); }
        }
        catch { png = null; }
        _pngCache[file] = png;
        return png;
    }

    // ── icon tile + tool header ───────────────────────────────────────────────────────────────────────────────────
    // A rounded tile in the LIVE theme accent with a flat white glyph centred on it (art can't follow the theme, so the
    // colour lives in the tile). The glyph is PRE-DRAWN at the exact size it is shown (Resources\hourglass-<px>.png,
    // hinted to whole pixels by tools\gen_hourglass_icon.py) — never one master scaled down. 40 px tile → the 24 px
    // glyph with 8 px all round. Radius ≈ 22 % of the side (9 at 40).
    private const float ToolIconSize  = 40f;
    private const int   ToolGlyphSize = 24;

    private HudElement IconBox(string glyphPng, float size, int glyph)
    {
        float pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            new RowElement(new HudElement[] { new ImageElement(() => CachedPng(glyphPng), glyph, glyph) },
                Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
        }, Width: size);
    }

    // Window title block: 40 px icon + bold title over a live status line (one line each, ellipsized, so the header
    // height never changes with the state or the locale).
    private HudElement ToolHeader(string glyphPng, Func<string> title, Func<string> status) => new RowElement(
        new HudElement[]
        {
            IconBox(glyphPng, ToolIconSize, ToolGlyphSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(title, Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
                new TextElement(status, Color: () => MutedText(), NoWrap: true) { Ellipsize = true },
            }, Gap: 2f), Weight: 1f),
        }, Gap: 10f);

    // ── chips ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // A clickable 24 px chip in a fixed-width cell: Cell → round Bubble (fill + hover) → Bare Selectable → Row[24 px
    // strut, label]. Two COMPLETE chips (on / off) are swapped by a ConditionalElement wrapping the whole chip (bold can't
    // toggle live; never a Conditional inside a bubble's measured child), both the SAME width and sharing one HoverFlag,
    // so a click never moves the row. Bubble.MaxWidth pins the width (a MaxWidth-0 bubble under a Conditional caps
    // itself at the 100 px default — MinimalNameplate, measured 64×24 in both states in-game 2026-11-07).
    private const float ChipHeight = 24f;

    private HudElement ChipShell(HudElement label, Func<ColorRgba> fill, HoverFlag hover, Action onClick, float width)
        => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipHeight),   // height strut = the side (house rule 4: no stacked spacers)
                label,
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(hover.On ? HoverFill(fill()) : fill()),
            CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = width,
        };

    private ColorRgba ChipOnFill() => Over(AccentColor() with { A = AccentWashAlpha }, PanelColor());
    private ColorRgba ChipOnText() => EnsureContrast(AccentColor(), ChipOnFill(), SolidMinContrast);

    // One chip, two complete states (on = bold accent on a 22 % accent wash, off = muted on the Tile). `onLabel` /
    // `offLabel` are the label in each state (On / Off for a toggle, the same text for a choice chip).
    private HudElement StateChip(Func<string> onLabel, Func<string> offLabel, Func<bool> on, Action click, float width)
    {
        var hover = new HoverFlag();
        return new CellElement(new ConditionalElement(on,
            ChipShell(DropBold(new TextElement(onLabel, Color: () => ChipOnText(),
                    Width: width, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }),
                ChipOnFill, hover, click, width),
            ChipShell(new TextElement(offLabel, Color: () => MutedText(),
                    Width: width, Align: TextAlign.Center, NoWrap: true) { Ellipsize = true },
                TileColor, hover, click, width)), Width: width);
    }

    // On/Off toggle chip: 64×24 in both states (spec + house StatusChip), wide enough for the longest On/Off translation
    // (fil "Nakabukas" ≈ 58 plain) — house rule 10. On = the status green (the prototype's .oo.on), not the accent.
    private const float OnOffChipW = 64f;

    private HudElement OnOffChip(Func<bool> get, Action<bool> set)
    {
        var hover = new HoverFlag();
        void Click() => set(!get());
        ColorRgba OnFill() => Over(SuccessColor() with { A = AccentWashAlpha }, PanelColor());
        return new CellElement(new ConditionalElement(get,
            ChipShell(DropBold(new TextElement(() => _loc.T("cd.chip.on"),
                    Color: () => EnsureContrast(SuccessColor(), OnFill(), SolidMinContrast),
                    Width: OnOffChipW, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }),
                OnFill, hover, Click, OnOffChipW),
            ChipShell(new TextElement(() => _loc.T("cd.chip.off"), Color: () => MutedText(),
                    Width: OnOffChipW, Align: TextAlign.Center, NoWrap: true) { Ellipsize = true },
                TileColor, hover, Click, OnOffChipW)), Width: OnOffChipW);
    }

    // One choice of a segmented setting (mode: Only ticked / All except ticked): on = the current value.
    private HudElement ChoiceChip(string labelKey, Func<bool> selected, Action pick, float width)
    {
        Func<string> label = () => _loc.T(labelKey);
        return StateChip(label, label, selected, pick, width);
    }

    // ── round-knob slider ─────────────────────────────────────────────────────────────────────────────────────────
    // SquareHandle = a knob exactly HandleSize across (the default handle stretches to the row height = a capsule).
    // Width 0 = the track flexes to fill its row cell (the prototype's flex:1 track).
    private const float SliderKnobSize = 14f;

    private static SliderElement RoundSlider(Func<float> get, Action<float> set, float min, float max)
        => new(get, set, min, max) { SquareHandle = true, HandleSize = SliderKnobSize };

    // ── filled list rows ──────────────────────────────────────────────────────────────────────────────────────────
    // House rule 15: the VirtualList slot STRETCHES its child to RowHeight, so a filled 26 px tile with RowHeight 28 gets
    // no gap. The pooled element is Column[tile, 2 px spacer] instead, whose preferred height IS the 28 px pitch.
    private const float ListTileHeight = 26f;
    private const float ListRowGap     = 2f;
    private const float ListRowPitch   = ListTileHeight + ListRowGap;   // the VirtualList RowHeight

    private static HudElement ListSlot(HudElement tile) => new ColumnElement(new HudElement[]
    {
        tile,
        new SpacerElement(0f, ListRowGap),
    }, Gap: 0.01f);

    // Viewport height that shows exactly `rows` rows: the last row's 2 px gap falls outside, so the list ends on a tile.
    private static float ListHeight(int rows) => rows * ListRowPitch - ListRowGap;

    // ── round ✕ clear button ──────────────────────────────────────────────────────────────────────────────────────
    // 28 × 28 circle, clear at rest with a muted PLAIN "✕" ("✕" can draw blank in bold); on hover a red wash
    // (Danger @22 %) with a lighter red glyph — the prototype's search clear / Remove-hover token. Exactly its side:
    // one Row[28 px strut, a 28 px-wide glyph box] (house rule 4). Rest fill is an explicit transparent: a null
    // FillColorFn would draw the bubble's default accent blob.
    private const float IconButtonSize = 28f;

    private HudElement ClearButton(Action onClick)
    {
        var hover = new HoverFlag();
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, IconButtonSize),
                new TextElement(() => "✕", Color: () => hover.On ? Mix(DangerColor(), White, 0.35f) : MutedText(),
                    Width: IconButtonSize, Align: TextAlign.Center, NoWrap: true),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => hover.On ? Fade(DangerColor() with { A = AccentWashAlpha }) : Transparent,
            CornerRadius = RoundRadius(IconButtonSize), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // ── cached formatter ──────────────────────────────────────────────────────────────────────────────────────────
    // A localized template + up to three numbers → the composed string, rebuilt only when the template (a language
    // switch) or a number changes. The text Funcs poll every refresh; this keeps them allocation-free in steady state.
    private sealed class FmtCache
    {
        private string? _tpl;
        private long _a = long.MinValue, _b = long.MinValue, _c = long.MinValue;
        private string _s = string.Empty;

        public string Get(string tpl, long a, long b = 0, long c = 0)
        {
            if (a != _a || b != _b || c != _c || !string.Equals(tpl, _tpl, StringComparison.Ordinal))
            {
                _tpl = tpl; _a = a; _b = b; _c = c;
                try { _s = string.Format(CultureInfo.CurrentCulture, tpl, a, b, c); }
                catch (FormatException) { _s = tpl; }   // a broken translation shows its raw text, never throws per refresh
            }
            return _s;
        }
    }

    // The same for string arguments (already-composed parts, e.g. FmtCache outputs): rebuilt only when the template or
    // an argument changed. Arguments are compared by REFERENCE — a FmtCache hands back the same instance until its own
    // inputs change, so an idle refresh is three pointer compares.
    private sealed class StrFmtCache
    {
        private string? _tpl, _a, _b, _c;
        private string _s = string.Empty;

        public string Get(string tpl, string a, string? b = null, string? c = null)
        {
            if (!ReferenceEquals(a, _a) || !ReferenceEquals(b, _b) || !ReferenceEquals(c, _c)
                || !string.Equals(tpl, _tpl, StringComparison.Ordinal))
            {
                _tpl = tpl; _a = a; _b = b; _c = c;
                try { _s = string.Format(CultureInfo.CurrentCulture, tpl, a, b, c); }
                catch (FormatException) { _s = tpl; }
            }
            return _s;
        }
    }
}
