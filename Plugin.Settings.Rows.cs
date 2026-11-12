using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

/// <summary>
/// Picker rows, house style. ONE scroll-windowed list serves the active kind: every slot reads that kind's filtered
/// arrays (Plugin.Settings.Tables.cs) at its offset + slot. A row is a filled 26 px tile in a 28 px slot (ListSlot):
///   [16 px tick box] [game icon 20] [name …] [muted ×N | "hidden" | green flash word]
/// The whole row toggles (skills: the id; debuffs / buffs: every member id of the name group); the ICON alone opens the
/// item's tooltip (the old icon-click behaviour, kept). Ticked = the kind colour's wash + a kind-coloured box with "✓";
/// in "All except ticked" a ticked row is one the bar HIDES: red wash, red box with "✕", muted struck-through name and
/// "hidden" on the right. Hover follows the rounded tile (Bubble fill around a Bare selectable).
/// Per-refresh cost: each Func is O(1) — a group's ticked state is cached per slot and recomputed only when the slot
/// shows another row or <see cref="_selVersion"/> moved; the ×N label is a per-slot FmtCache.
/// </summary>
public sealed partial class Plugin
{
    private const float SelCheckBoxSize = 16f;
    private const float SelIconSize     = 20f;
    // Right column: the bold flash words (fil "Naidagdag" ≈ 66, th "นำออกแล้ว" ≈ 63) and "hidden" (id "tersembunyi"
    // ≈ 78 plain) fit, so the name column keeps one width on every row (house rule 10).
    private const float SelValueColW    = 80f;

    // Bumped on every selection / filter / load change: invalidates the slot caches and the ticked counts.
    private int _selVersion;

    // ── active-kind views (no allocation: they return the existing arrays) ────────────────────────────────────────
    private int      SelFiltCount => _activeTab == 0 ? _stFiltCount : _activeTab == 1 ? _dtFiltCount : _btFiltCount;
    private int      SelOffset    => _activeTab == 0 ? _stOffset    : _activeTab == 1 ? _dtOffset    : _btOffset;
    private int[]    SelFiltIds   => _activeTab == 0 ? _stFiltIds   : _activeTab == 1 ? _dtFiltIds   : _btFiltIds;
    private string[] SelFiltNames => _activeTab == 0 ? _stFiltNames : _activeTab == 1 ? _dtFiltNames : _btFiltNames;
    private string   SelFilter    => _activeTab == 0 ? _stFilter    : _activeTab == 1 ? _dtFilter    : _btFilter;
    private bool     SelExcludeMode => ActiveTabMode() == TrackMode.ExcludeBelow;
    private ColorRgba SelKindColor  => _activeTab == 0 ? CooldownCol : _activeTab == 1 ? DebuffCol : BuffCol;

    // Bound once (a method-group conversion allocates a delegate each time it is written).
    private Func<int, bool>? _isDebuffTrackedFn, _isBuffTrackedFn;

    // Is filtered row i ticked? Skills: its id. Debuffs / buffs: every member of its name group.
    private bool RowTicked(int i)
    {
        if (_activeTab == 0) return _selection.IsCooldownTracked(_stFiltIds[i]);
        return _activeTab == 1
            ? GroupAllTracked(_dtFiltMembers[i], _isDebuffTrackedFn ??= _selection.IsDebuffTracked)
            : GroupAllTracked(_btFiltMembers[i], _isBuffTrackedFn ??= _selection.IsBuffTracked);
    }

    // ── ticked counts per kind (header status, kind chips, footer, confirm, placed hint) ─────────────────────────
    // ALWAYS "ticked rows as the list shows them": skills = list rows whose id is tracked; debuffs / buffs = fully
    // ticked name GROUPS (a variant group = 1). Until a kind's table is loaded its count is TickPending (shown as "…"),
    // never a raw saved-id count — those disagreed with the grouped number (11 raw debuff ids vs 4 rows, in game
    // 2026-11-13). Recounted only when _selVersion moved — a walk over the rows, never per refresh.
    private const int TickPending = -1;
    private const string PendingText = "…";
    private int _tickCountVer = -1, _stTicked = TickPending, _dtTicked = TickPending, _btTicked = TickPending;

    private void EnsureTickCounts()
    {
        if (_tickCountVer == _selVersion) return;
        _tickCountVer = _selVersion;
        _stTicked = TickPending;
        if (_stBaseLoaded)
        {
            int n = 0;
            for (int i = 0; i < _stCount; i++) if (_selection.IsCooldownTracked(_stIds[i])) n++;
            _stTicked = n;
        }
        if (!_buffTabLoaded) { _dtTicked = _btTicked = TickPending; return; }
        _dtTicked = CountTicked(_dtMembers, _dtCount, _isDebuffTrackedFn ??= _selection.IsDebuffTracked);
        _btTicked = CountTicked(_btMembers, _btCount, _isBuffTrackedFn ??= _selection.IsBuffTracked);
    }

    private bool TickCountsReady
    {
        get { EnsureTickCounts(); return _stTicked >= 0 && _dtTicked >= 0 && _btTicked >= 0; }
    }

    private static int CountTicked(int[][] members, int count, Func<int, bool> isTracked)
    {
        int n = 0;
        for (int i = 0; i < count; i++) if (GroupAllTracked(members[i], isTracked)) n++;
        return n;
    }

    private int SelActiveTicked
    {
        get { EnsureTickCounts(); return _activeTab == 0 ? _stTicked : _activeTab == 1 ? _dtTicked : _btTicked; }
    }

    // ── per-slot cache ────────────────────────────────────────────────────────────────────────────────────────────
    // Keyed by (kind, filtered row index, _selVersion): a filter / load / toggle bumps the version.
    private readonly int[]  _slotRow    = new int[SettingsPoolSize];
    private readonly int[]  _slotTab    = new int[SettingsPoolSize];
    private readonly int[]  _slotVer    = NewFilled(SettingsPoolSize, -1);
    private readonly bool[] _slotTicked = new bool[SettingsPoolSize];
    private readonly FmtCache[] _slotVarFmt = NewFmtCaches(SettingsPoolSize);

    private static int[] NewFilled(int n, int v) { var a = new int[n]; Array.Fill(a, v); return a; }

    private static FmtCache[] NewFmtCaches(int n)
    {
        var a = new FmtCache[n];
        for (int i = 0; i < n; i++) a[i] = new FmtCache();
        return a;
    }

    private bool SlotTicked(int slot)
    {
        int i = SelOffset + slot;
        if (i < 0 || i >= SelFiltCount) return false;
        if (_slotRow[slot] != i || _slotTab[slot] != _activeTab || _slotVer[slot] != _selVersion)
        {
            _slotRow[slot] = i; _slotTab[slot] = _activeTab; _slotVer[slot] = _selVersion;
            _slotTicked[slot] = RowTicked(i);
        }
        return _slotTicked[slot];
    }

    private bool SlotHidden(int slot) => SelExcludeMode && SlotTicked(slot);   // the bar hides this row's effect

    // ── the tile ──────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildEffectRow(int slot) => new BubbleElement(new SelectableElement(
        new RowElement(new HudElement[]
        {
            new SpacerElement(2f, ListTileHeight),   // height strut + (with the 6 px gap) the 8 px left inset
            EffectTickBox(slot),
            // The icon is its own (Bare) click target → the item tooltip; the rest of the row toggles. Fixed cell +
            // centring Row: a bare picture in a Cell is stretched sideways (the icon-squish gotcha).
            new CellElement(new RowElement(new HudElement[]
                {
                    new SelectableElement(
                        new GameTextureElement(() => SlotIcon(slot), (int)SelIconSize, (int)SelIconSize, () => SlotUv(slot)),
                        OnClick: () => OnSettingIconClick(ActiveKind, slot)) { Bare = true },
                }, Gap: 0.01f, Justify: RowJustify.Center), Width: SelIconSize),
            // Name: normal, or (a row the bar hides) muted + struck through — two complete texts, one fixed slot.
            new CellElement(new ConditionalElement(() => SlotHidden(slot),
                new TextElement(() => SlotName(slot), Color: () => MutedText(), NoWrap: true)
                    { Ellipsize = true, Strikethrough = true },
                new TextElement(() => SlotName(slot), Color: () => TextColor(), NoWrap: true) { Ellipsize = true })
                { Prebuild = true }, Weight: 1f),
            // Right column: muted ×N / "hidden", or for ~1 s after a click on this row the bold green "Added" /
            // "Removed" (bold can't toggle live → two texts). Same fixed cell, so the swap never moves the name.
            new CellElement(new ConditionalElement(() => SlotFlashing(slot),
                new TextElement(SelFlashLabel, Color: () => SelFlashTextColor(), Align: TextAlign.Right, NoWrap: true,
                    FontSize: 13) { Bold = true },
                new TextElement(() => SlotRightLabel(slot), Color: () => MutedText(), Align: TextAlign.Right, NoWrap: true))
                { Prebuild = true }, Width: SelValueColW),
            new SpacerElement(2f, 1f),               // right inset
        }, Gap: 6f),
        OnClick: () => ToggleSlot(slot))
        { Bare = true, OnHover = over => SetSelSlotHover(slot, over) })
    {
        FillColorFn = () => Fade(EffectTileFill(slot)),
        CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
    };

    private TileKind ActiveKind => _activeTab == 0 ? TileKind.Cooldown : _activeTab == 1 ? TileKind.Debuff : TileKind.Buff;

    // Tile → kind wash (ticked) / red wash (ticked = hidden, "All except ticked") → green while its click flash runs;
    // hover lightens whichever.
    private ColorRgba EffectTileFill(int slot)
    {
        var fill = SlotFlashing(slot)  ? Over(SuccessColor() with { A = FlashWashAlpha }, TileColor())
                 : !SlotTicked(slot)   ? TileColor()
                 : SelExcludeMode      ? Over(DangerColor() with { A = DangerWashAlpha }, TileColor())
                 :                       Over(SelKindColor with { A = RowWashAlpha }, TileColor());
        return SelSlotHovered(slot) ? HoverFill(fill) : fill;
    }

    // 16 px rounded tick box: ticked = solid kind colour + "✓" (or solid red + "✕" when ticked means hidden), unticked
    // = an empty Neutral box. The glyph is white or near-black, whichever reads on the fill (white on the light cyan
    // fails 3:1 → dark, as in the prototype). One element (live glyph + live fill), so ticking never rebuilds anything.
    // Text.Height pins the plain 14 px glyph's line box to 16, so the box stays exactly 16 × 16.
    private HudElement EffectTickBox(int slot) => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, SelCheckBoxSize),
            new TextElement(() => !SlotTicked(slot) ? string.Empty : SelExcludeMode ? "✕" : "✓",
                () => EnsureContrast(White, SelExcludeMode ? DangerColor() : SelKindColor, SolidMinContrast),
                Width: SelCheckBoxSize, Align: TextAlign.Center, NoWrap: true) { Height = SelCheckBoxSize },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(!SlotTicked(slot) ? NeutralFill() : SelExcludeMode ? DangerColor() : SelKindColor),
        CornerRadius = 4f, PaddingX = 0f, PaddingY = 0f,
    }, Width: SelCheckBoxSize);

    private object? SlotIcon(int slot) => _activeTab == 0 ? StIcon(slot) : _activeTab == 1 ? DtIcon(slot) : BtIcon(slot);

    // Per-kind icon resolvers (also used by the tooltip, Plugin.Tooltip.cs). Skills prefer the Imagine art.
    private object? StIcon(int idx)
    {
        int i = _stOffset + idx;
        if (i >= _stFiltCount) { _stUv[idx] = default; return null; }
        return _services.GameAssets.LoadImagineIcon(_stFiltIds[i], out _stUv[idx])
            ?? _services.GameAssets.LoadSkillIcon(_stFiltIds[i], out _stUv[idx]);
    }

    private object? DtIcon(int idx)
    {
        int i = _dtOffset + idx;
        if (i >= _dtFiltCount) { _dtUv[idx] = default; return null; }
        return _services.GameAssets.LoadBuffIcon(_dtFiltIds[i], out _dtUv[idx]);
    }

    private object? BtIcon(int idx)
    {
        int i = _btOffset + idx;
        if (i >= _btFiltCount) { _btUv[idx] = default; return null; }
        return _services.GameAssets.LoadBuffIcon(_btFiltIds[i], out _btUv[idx]);
    }
    private UvRect  SlotUv(int slot)   => _activeTab == 0 ? _stUv[slot] : _activeTab == 1 ? _dtUv[slot] : _btUv[slot];

    private string SlotName(int slot)
    {
        int i = SelOffset + slot;
        return i < SelFiltCount ? SelFiltNames[i] : string.Empty;
    }

    // "hidden" for a row the bar hides; otherwise "×N" when same-name variants collapsed into this row (N > 1).
    private string SlotRightLabel(int slot)
    {
        if (SlotHidden(slot)) return _loc.T("cd.sel.hidden");
        int i = SelOffset + slot;
        if (_activeTab == 0 || i >= SelFiltCount) return string.Empty;
        int n = (_activeTab == 1 ? _dtFiltMembers : _btFiltMembers)[i].Length;
        return n > 1 ? _slotVarFmt[slot].Get(_loc.T("cd.group.variants"), n) : string.Empty;
    }

    // ── hover (per pool slot) ─────────────────────────────────────────────────────────────────────────────────────
    // Keyed by the slot's LOGICAL row: when the slot is re-bound to another row (scroll, filter, kind), the flag resets
    // until the pointer re-enters, so a recycled slot never inherits the previous row's hover.
    private readonly bool[] _selSlotHover    = new bool[SettingsPoolSize];
    private readonly int[]  _selSlotHoverRow = new int[SettingsPoolSize];

    private void SetSelSlotHover(int slot, bool over)
    {
        _selSlotHover[slot] = over;
        _selSlotHoverRow[slot] = SelOffset + slot;
    }

    private bool SelSlotHovered(int slot)
    {
        if (_selSlotHoverRow[slot] != SelOffset + slot) { _selSlotHoverRow[slot] = SelOffset + slot; _selSlotHover[slot] = false; }
        return _selSlotHover[slot];
    }

    // ── click ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Toggle the row. A group is ON only when every member id is tracked (a partial group reads OFF, so one tap selects
    // all of it); selection stays per-id. Debuffs go through SetDebuff (keeps the imagine-lockout opt-out per id).
    // No re-sort on click (rows never jump under the cursor); the next kind switch / search re-sorts ticked to the top.
    private void ToggleSlot(int slot)
    {
        int i = SelOffset + slot;
        if (i < 0 || i >= SelFiltCount) return;
        bool on = !RowTicked(i);
        if (_activeTab == 0) _selection.SetCooldown(_stFiltIds[i], on);
        else
        {
            var members = _activeTab == 1 ? _dtFiltMembers[i] : _btFiltMembers[i];
            foreach (int id in members)
            {
                if (_activeTab == 1) _selection.SetDebuff(id, on);
                else                 _selection.SetBuff(id, on);
            }
        }
        _selection.Save(_cfg);
        _selVersion++;
        OnEffectToggled(_activeTab, SelFiltIds[i], SelFiltNames[i], on);   // Plugin.Settings.Feedback.cs
    }
}
