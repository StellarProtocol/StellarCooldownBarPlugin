using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// One placed effect's window (Plugin.Placed.cs): Borderless HUD, drag in the layout editor only, width = content
// (AutoSizeWidth: every text in it is one fixed-width line), height = content.
//   [name tag slot, TagH]   orange name tag while the layout editor is open, else an empty strut of the same size — so
//                           the tile never moves between editing and playing
//   [tile]                  T1 at the effect's size: Default (the DISPLAY size) or its own override; each size is a
//                           separately built variant (TileGeom) swapped by nested ConditionalElements — only the chosen
//                           one is ever built (a Conditional builds a branch lazily on first show)
// Window id "cooldownbar.placed.<kind>.<id>" is stable per effect, so the framework's layout storage keeps each
// position; Title = the effect name (the layout editor labels its outline with it). Visible ONLY while the effect is
// active (ShouldRender reads the 10 Hz TileIdx — an O(1) read); the layout editor force-shows it regardless.
public sealed partial class Plugin
{
    private const float PlacedTagH   = 16f;
    private const float PlacedTagW   = 76f;    // ≥ the 75–150 % tiles (37–75 px); the 200 % tile (100) is wider
    private const float PlacedTagGap = 3f;
    private const float SampleFade   = 0.45f;  // inactive tiles in the layout editor
    private static readonly ColorRgba TagOrange = new(1.00f, 0.60f, 0.18f, 1f);   // the layout editor's outline orange
    private static readonly ColorRgba TagInk    = new(0.10f, 0.07f, 0.04f, 1f);

    // One geometry per size (TileGeom), built once and shared by every placed window.
    private static readonly TileGeom[] PlacedGeoms =
    {
        new(0.75f, shadow: false), new(1.00f, shadow: false), new(1.25f, shadow: false),
        new(1.50f, shadow: false), new(2.00f, shadow: false),
    };

    private static string PlacedWindowId(PlacedEffect p)
        => "cooldownbar.placed." + (p.Kind == TileKind.Cooldown ? "skill" : p.Kind == TileKind.Debuff ? "debuff" : "buff")
           + "." + p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private IWindowControl RegisterPlacedWindow(PlacedEffect p)
    {
        string name = p.Name.Length > 0 ? p.Name : _loc.T("cd.window.title");
        return _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          PlacedWindowId(p),
                Title:       name,
                DefaultRect: PlacedDefaultRect(p.Slot, _placedGridPct),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                StartVisible = true, Draggable = true, EditModeDragOnly = true,
                AutoSizeWidth = true,
                ShouldRender = () => p.TileIdx >= 0 && PlacedGateOpen(),
            },
            BuildPlacedRoot(p)));
    }

    // New placed effects first appear in a column block right of the screen centre (up to 6 per column, then the next
    // column) until the user moves them. The cells are spaced for `pct` — the GRID size (PlacedGridPct: the largest size
    // any placed tile uses), so tiles never overlap at 125–200 % (at 150 % the old fixed 100 % pitch stacked them, in game
    // 2026-11-13). Fewer rows per column when a column wouldn't fit the canvas height. At 100 % this is exactly the old
    // grid (100 × 113 cells, 6 rows), which is what LegacyGridPct relies on. Canvas units; 1920 × 1080 until the canvas
    // reports its size.
    private WindowRect PlacedDefaultRect(int slot, int pct)
    {
        var fw = _services.Framework;
        float cw = fw.CanvasWidth > 1f ? fw.CanvasWidth : 1920f, ch = fw.CanvasHeight > 1f ? fw.CanvasHeight : 1080f;
        var g = PlacedGeoms[Math.Max(0, Array.IndexOf(PlacedSizes, pct))];
        float tileH = 2f * g.Pad + g.Icon + 2f * g.ColGap + g.BarH + g.FooterH;   // the T1 column (70 at 100 %)
        float w = MathF.Max(PlacedTagW, g.W) + ChromePad * 2f;                    // 100 at 100 %
        float h = ChromePad * 2f + PlacedTagH + PlacedTagGap + tileH;             // 113 at 100 %
        int perCol = Math.Clamp((int)((ch - 80f) / (h + 4f)), 1, 6);
        int col = slot / perCol, row = slot % perCol;
        return new WindowRect(cw / 2f + 140f + col * (w + 4f), ch / 2f - perCol * (h + 4f) / 2f + row * (h + 4f), w, 0f);
    }

    // ── keeping unmoved tiles on the grid when the grid size changes ──────────────────────────────────────────────
    // The framework persists every window's first settled rect, so "has a saved position" can't tell a user-placed tile
    // from one still sitting on its default spot. A tile counts as UNMOVED when its rect is still the default rect of
    // the grid it was laid out for (PlacedEffect.GridPct); only those follow a grid change. Windows from before the
    // grid was size-aware sit on the 100 % grid (LegacyGridPct), so a newly registered window is checked against that
    // once its saved rect has been applied (GridSettleMs after registering).
    private const int  LegacyGridPct = 100;
    private const long GridSettleMs  = 500;
    private int _placedGridPct;   // the grid the registered windows are laid out for (0 = none registered yet)

    private int PlacedGridPct()
    {
        int m = _placedSize;
        for (int s = 0; s < MaxPlaced; s++) if (_placed[s] is { } p) m = Math.Max(m, EffectiveSize(p));
        return m;
    }

    // After a size change or a new effect: a new grid → the pump re-checks every registered window (spread over ticks).
    private void RegridPlaced()
    {
        int grid = PlacedGridPct();
        if (grid == _placedGridPct) return;
        _placedGridPct = grid;
        _placedPumpDue = true;
    }

    // Pump step for one registered window whose GridPct differs from the current grid. False = not yet (its saved rect
    // may not be applied right after registering) → retry next tick.
    private bool MoveToGrid(PlacedEffect p, long now)
    {
        if (now < p.GridCheckAt) return false;
        var was = PlacedDefaultRect(p.Slot, p.GridPct);
        p.GridPct = _placedGridPct;
        var cur = p.Win!.Rect;
        if (Math.Abs(cur.X - was.X) >= 1f || Math.Abs(cur.Y - was.Y) >= 1f) return true;   // the user moved it: keep
        var to = PlacedDefaultRect(p.Slot, _placedGridPct);
        p.Win.SetRect(new WindowRect(to.X, to.Y, cur.Width, cur.Height));   // persists (one config write)
        return true;
    }

    private HudElement BuildPlacedRoot(PlacedEffect p)
    {
        Func<int> slot = () => PlacedSlot(p);
        Func<float> fade = () => PlacedSample(p) ? SampleFade : 1f;
        Func<int> size = () => EffectiveSize(p);
        HudElement Variant(int i) => BuildTile(slot, PlacedGeoms[i], fade);
        var tile = new ConditionalElement(P(() => size() == 75), Variant(0),
                   new ConditionalElement(P(() => size() == 100), Variant(1),
                   new ConditionalElement(P(() => size() == 125), Variant(2),
                   new ConditionalElement(P(() => size() == 150), Variant(3), Variant(4)))));
        return new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new CellElement(new ConditionalElement(P(() => _services.Windows.IsLayoutEditing),
                    PlacedTag(p), new SpacerElement(PlacedTagW, PlacedTagH)), Width: PlacedTagW),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            new RowElement(new HudElement[] { tile }, Gap: 0.01f, Justify: RowJustify.Center),
        }, Gap: PlacedTagGap);
    }

    // Orange name tag (the layout editor's own colour, so it reads as editor chrome): bold 10 dark ink, one line, cut
    // with "…" at the tag width. Solid even on a faded sample — it names the tile being arranged.
    private HudElement PlacedTag(PlacedEffect p) => new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, PlacedTagH),
            new TextElement(() => p.Name, P<ColorRgba?>(() => TagInk), Width: PlacedTagW - 8f, Align: TextAlign.Center,
                NoWrap: true, FontSize: 10) { Bold = true, Ellipsize = true, Height = PlacedTagH },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = P<ColorRgba?>(() => TagOrange),
        CornerRadius = 4f, PaddingX = 4f, PaddingY = 0f, MaxWidth = PlacedTagW,
    };
}
