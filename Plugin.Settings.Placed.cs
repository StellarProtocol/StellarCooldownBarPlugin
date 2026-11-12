using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

/// <summary>
/// Settings for Placed display mode (approved 2026-11-13, settings layout A):
///   DISPLAY panel: [Show as] [Bar] [Placed] … [?]  ·  Bar → Background row / Placed → Size row (75 … 200 %)  ·  Hidden effects
///   WHAT TO SHOW, Placed: the mode-chip row becomes a muted one-line note with an accent dot + [?] (Only ticked is forced);
///   the hint reads "N / 30 placed"; each TICKED row shows a 62×20 size chip ("Default" muted / "150%" accent) that
///   opens the size popover "SIZE · NAME" [Default] [75%] … [200%]; a ticked row over the cap reads amber "not placed".
/// The popover is one shared, title-bar-less GlassMenu window anchored under the clicked chip (the help popover recipe).
/// </summary>
public sealed partial class Plugin
{
    private const float DisplayChipW   = 100f;   // "Bar" / "Placed" (fil "Nakalagay" ≈ 70 bold 13)
    private const float SizeChipW      = 46f;    // "125%" bold 13 ≈ 36
    private const float RowSizeChipW   = 62f;
    private const float RowSizeChipH   = 20f;
    private const float PopDefaultW    = 64f;    // "Default" in the popover (th "ปกติ", ja "既定" are shorter)
    private const float SizePopW       = 24f + PopDefaultW + 5f * SizeChipW + 5f * 4f;   // 338
    private const float NoteDot        = 8f;
    private static readonly string[] SizeLabels = { "75%", "100%", "125%", "150%", "200%" };

    // ── DISPLAY panel rows ────────────────────────────────────────────────────────────────────────────────────────
    private HudElement DisplayModeRow() => SettingRow(
        new CellElement(new TextElement(() => _loc.T("cd.display.mode"), Color: () => TextColor(), NoWrap: true)
            { Ellipsize = true }, Width: BgLabelW),
        ChoiceChip("cd.display.bar",    () => !_placedMode, () => SetDisplayMode(false), DisplayChipW),
        ChoiceChip("cd.display.placed", () => _placedMode,  () => SetDisplayMode(true),  DisplayChipW),
        new SpacerElement(0f),   // flexible: the "?" lands on the panel's right edge
        HelpDot("cd.display.mode", "cd.help.display", "cd.sec.display"));

    // "Size" + five chips (46 px each); the DISPLAY size every placed tile follows unless it has its own override.
    private HudElement PlacedSizeRow()
    {
        var chips = new HudElement[PlacedSizes.Length];
        for (int i = 0; i < PlacedSizes.Length; i++)
        {
            int pct = PlacedSizes[i]; string label = SizeLabels[i];
            chips[i] = StateChip(() => label, () => label, () => _placedSize == pct, () => SetPlacedSize(pct), SizeChipW);
        }
        return SettingRow(
            new CellElement(new TextElement(() => _loc.T("cd.size"), Color: () => TextColor(), NoWrap: true)
                { Ellipsize = true }, Width: BgLabelW),
            new RowElement(chips, Gap: 4f));
    }

    // Placed: replaces the mode chips (same 24 px row) — Only ticked is forced, each kind's own mode is kept for Bar.
    // The note is regular 12 via the TMP face (RichEmoji = the toolkit's regular-weight-at-a-size path; plain Menu text
    // is a fixed 14 px): the en sentence is ≈ 325 px at 12 vs ≈ 330 free; longer locales cut with "…" (the "?" explains).
    private HudElement PlacedNoteRow() => new RowElement(new HudElement[]
    {
        new CellElement(new RowElement(new HudElement[]
            {
                new BubbleElement(new SpacerElement(NoteDot, NoteDot))
                {
                    FillColorFn = () => Fade(AccentColor()), CornerRadius = RoundRadius(NoteDot), PaddingX = 0f, PaddingY = 0f,
                },
            }, Gap: 0.01f, Justify: RowJustify.Center), Width: NoteDot + 4f),
        new CellElement(new TextElement(() => _loc.T("cd.placed.note"), Color: () => MutedText(), NoWrap: true, FontSize: 12)
            { RichEmoji = true, Ellipsize = true }, Weight: 1f),
        HelpDot("cd.filterMode", "cd.help.placedNote", "cd.sec.show"),
    }, Gap: 4f);

    // ── per-row placed state ──────────────────────────────────────────────────────────────────────────────────────
    private long SlotKey(int slot)
    {
        int i = SelOffset + slot;
        return i >= 0 && i < SelFiltCount ? PKey(ActiveKind, SelFiltIds[i]) : -1L;
    }

    private bool SlotPlaced(int slot)    => _placedMode && SlotTicked(slot) && _placedKeys.Contains(SlotKey(slot));
    private bool SlotNotPlaced(int slot) => _placedMode && SlotTicked(slot) && _notPlacedKeys.Contains(SlotKey(slot));

    private ColorRgba AmberText() => EnsureContrast(Menu.Caution, TileColor(), PillMinContrast);

    // Right-aligned 62×20 chip in the row's right column: "Default" muted on the Neutral fill, or the override bold in the
    // accent on its wash. A nested Bare selectable (the icon pattern): its click opens the popover, not the row toggle.
    private HudElement RowSizeChip(int slot)
    {
        var hover = new HoverFlag();
        HudElement Shell(HudElement label, Func<ColorRgba> fill) => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[] { new SpacerElement(0f, RowSizeChipH), label }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { })
            { Bare = true, OnHover = over => hover.On = over, OnClickWithRect = r => ToggleSizePopover(r, slot) })
        {
            FillColorFn = () => Fade(hover.On ? HoverFill(fill()) : fill()),
            CornerRadius = RoundRadius(RowSizeChipH), PaddingX = 0f, PaddingY = 0f, MaxWidth = RowSizeChipW,
        };
        var chip = new ConditionalElement(() => SlotSizeOverride(slot) > 0,
            Shell(new TextElement(() => SizeLabel(SlotSizeOverride(slot)), Color: () => ChipOnText(), Width: RowSizeChipW,
                Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true }, ChipOnFill),
            Shell(new TextElement(() => _loc.T("cd.size.default"), Color: () => MutedText(), Width: RowSizeChipW,
                Align: TextAlign.Center, NoWrap: true) { Ellipsize = true }, NeutralFill));
        return new RowElement(new HudElement[] { new SpacerElement(0f), new CellElement(chip, Width: RowSizeChipW) },
            Gap: 0.01f);
    }

    private int SlotSizeOverride(int slot) => _sizeOverrides.TryGetValue(SlotKey(slot), out int s) ? s : 0;

    private static string SizeLabel(int pct)
    {
        int i = Array.IndexOf(PlacedSizes, pct);
        return i >= 0 ? SizeLabels[i] : string.Empty;
    }

    // ── size popover ──────────────────────────────────────────────────────────────────────────────────────────────
    private readonly IWindowControl _sizePop;
    private long _sizePopKey = -1;
    private string _sizePopCaption = string.Empty;   // "SIZE · NAME", composed at open
    private WindowRect _sizePopRect;
    private int _sizePopTicks;
    private readonly HoverFlag _sizePopCloseHover = new();

    private IWindowControl RegisterSizePopover() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          "cooldownbar.sizepop",
            Title:       _loc.T("cd.size"),
            DefaultRect: new WindowRect(930f, 260f, SizePopW, 0f),   // constant; every open re-anchors it
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        {
            TitleProvider = () => _loc.T("cd.size"),
            Draggable = true, Closable = true, StartVisible = false, ShowTitleBar = false,
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                 && (_services.ClientState.UiState & GameUIState.Loading) == 0,
        },
        BuildSizePopRoot(),
        OnClose: CloseSizePopover));

    private HudElement BuildSizePopRoot()
    {
        var chips = new HudElement[PlacedSizes.Length + 1];
        chips[0] = StateChip(() => _loc.T("cd.size.default"), () => _loc.T("cd.size.default"),
            () => !_sizeOverrides.ContainsKey(_sizePopKey), () => PickPopSize(0), PopDefaultW);
        for (int i = 0; i < PlacedSizes.Length; i++)
        {
            int pct = PlacedSizes[i]; string label = SizeLabels[i];
            chips[i + 1] = StateChip(() => label, () => label,
                () => _sizeOverrides.TryGetValue(_sizePopKey, out int s) && s == pct, () => PickPopSize(pct), SizeChipW);
        }
        return new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => _sizePopCaption, Color: () => TextColor(), NoWrap: true, FontSize: 12)
                    { Bold = true, Ellipsize = true }, Weight: 1f),
                SizePopCloseButton(),
            }, Gap: 8f),
            new RowElement(chips, Gap: 4f),
        }, Gap: 8f);
    }

    // Round 24 px ✕ (the help popover's close, own hover flag).
    private HudElement SizePopCloseButton() => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpSize),
            new TextElement(() => "✕", Color: () => _sizePopCloseHover.On ? TextColor() : MutedText(), Width: HelpSize,
                Align: TextAlign.Center, NoWrap: true),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: CloseSizePopover) { Bare = true, OnHover = over => _sizePopCloseHover.On = over })
    {
        FillColorFn = () => _sizePopCloseHover.On ? Fade(Over(TextColor() with { A = HoverMix }, PanelColor())) : Transparent,
        CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
    };

    // Re-clicking the open row's chip closes it; another row's chip re-targets and re-anchors it (already visible →
    // BringToFront, CLAUDE.md Rule 5 exception, as the help popover).
    private void ToggleSizePopover(WindowRect r, int slot)
    {
        long key = SlotKey(slot);
        if (key < 0) return;
        if (_sizePop.IsShown && key == _sizePopKey) { CloseSizePopover(); return; }
        _sizePopKey = key;
        _sizePopCaption = (_loc.T("cd.size") + " · " + SlotName(slot)).ToUpperInvariant();
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 1f ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;   // px → canvas
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float canvasH = fw.CanvasHeight > 1f ? fw.CanvasHeight : fw.ScreenHeight;
        _sizePopRect = new WindowRect(MathF.Max(10f, r.X + r.Width - SizePopW), MathF.Min(r.Y + r.Height + 4f, canvasH - 40f),
            SizePopW, 0f);
        _sizePop.SetVisible(true);
        _sizePop.SetRect(_sizePopRect);
        _sizePop.BringToFront();
        _sizePop.MarkDirty();
        _sizePopTicks = 4;   // the first open mounts after this SetRect → re-apply for a few ticks
    }

    private void PickPopSize(int pct)
    {
        if (_sizePopKey < 0) return;
        SetSizeOverride(_sizePopKey, pct);   // Plugin.Placed.cs — persisted; the placed window swaps its variant
    }

    private void CloseSizePopover()
    {
        _sizePopTicks = 0;
        _sizePopKey = -1;
        if (_sizePop.IsShown) _sizePop.SetVisible(false);
    }

    // TickSettingsUi: re-assert the popover position while the reposition window is open (early-out otherwise).
    private void SizePopRepositionTick()
    {
        if (_sizePopTicks <= 0) return;
        _sizePopTicks--;
        if (_sizePop.IsShown) _sizePop.SetRect(_sizePopRect);
    }

    // ── live text ─────────────────────────────────────────────────────────────────────────────────────────────────
    private readonly FmtCache _placedHintFmt = new();
    private readonly FmtCache _placedStatusFmt = new();

    private string PlacedHint() => _placedHintFmt.Get(_loc.T("cd.hint.placed"), _placedCount, MaxPlaced);

    // "Placed: 18 effects" — the number of placed TILES, not "on screen": a tile shows only while its effect is active.
    private string PlacedStatus()
        => _stBaseLoaded && _buffTabLoaded
            ? _placedStatusFmt.Get(_loc.T(_placedCount == 1 ? "cd.settings.status.placed.one" : "cd.settings.status.placed"), _placedCount)
            : PendingText;
}
