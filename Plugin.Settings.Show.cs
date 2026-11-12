using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

/// <summary>
/// The WHAT TO SHOW panel (house style; TargetLens Select-window recipe):
///   caption "WHAT TO SHOW" … muted "632 skills" / "48 / 312 debuffs" (filtered / total while searching)
///   [Skills n] [Debuffs n] [Buffs n] — kind chips, on = bold in the bar's own kind colour on its 22 % wash
///   [Only ticked] [All except ticked] … [?] — the ACTIVE kind's TrackMode
///   [search …] [✕]
///   ONE scroll-windowed list serving the active kind (Plugin.Settings.Rows.cs), or "No skills match “q”"
///   footer "N ticked" … [Untick all] / the inline confirm (Plugin.Settings.Feedback.cs)
/// Switching kind keeps each kind's own search, re-sorts (ticked to the top) and resets the scroll.
/// </summary>
public sealed partial class Plugin
{
    // Kind chips "Debuffs 11": a right-aligned label box (longest: en / fil "Debuffs" ≈ 52 bold 13) + a left-aligned
    // count box (3 digits), so the seam stays put while the count changes and the chip never resizes.
    // 3 × 107 + 2 × 4 = 329 ≤ 376 (house rule 10).
    private const float KindChipLabelW = 56f;
    private const float KindChipCountW = 30f;
    private const float KindChipW      = KindChipLabelW + 5f + KindChipCountW + 16f;   // 107
    // Mode chips: both the same width = the longest label in any locale + padding; 2×168 + "?" 24 + 3 gaps of 4 = 372.
    private const float ModeChipW      = 168f;
    private const float SearchW        = PanelInnerW - IconButtonSize - PanelGap;
    private static readonly float ListBaseH = ListHeight(6);   // 166 — flexible: takes the window's spare height

    private int  _activeTab;        // 0 = Skills, 1 = Debuffs, 2 = Buffs
    private bool _selScrollReset;

    private HudElement BuildShowSection()
    {
        var pool = new HudElement[SettingsPoolSize];
        for (int i = 0; i < SettingsPoolSize; i++) pool[i] = ListSlot(BuildEffectRow(i));   // Plugin.Settings.Rows.cs
        var list = new VirtualListElement(SelListCount, ListRowPitch, pool, SetSelOffset, Height: ListBaseH)
        {
            ResetScroll = ConsumeSelScrollReset,
        };
        // Search with no matches: a centred muted note in the same (flexible) list area.
        var empty = new ColumnElement(new HudElement[]
        {
            new SpacerElement(0f, ListBaseH / 2f - 10f),
            new TextElement(SelectEmptyText, Color: () => MutedText(), Align: TextAlign.Center, NoWrap: true)
                { Ellipsize = true },
        }, Gap: 0.01f);

        return SectionPanel(() => _loc.T("cd.sec.show"), ShowHint, new HudElement[]
        {
            new RowElement(new HudElement[] { KindChip(0), KindChip(1), KindChip(2) }, Gap: 4f),
            new RowElement(new HudElement[]
            {
                ChoiceChip("cd.mode.only",   () => ActiveTabMode() == TrackMode.IncludeOnly,
                    () => SetActiveTabMode(TrackMode.IncludeOnly), ModeChipW),
                ChoiceChip("cd.mode.except", () => ActiveTabMode() == TrackMode.ExcludeBelow,
                    () => SetActiveTabMode(TrackMode.ExcludeBelow), ModeChipW),
                new SpacerElement(0f),   // flexible: the "?" lands on the panel's right edge
                HelpDot("cd.filterMode", "cd.help.mode", "cd.sec.show"),
            }, Gap: 4f),
            new RowElement(new HudElement[]
            {
                // One field for all kinds: Get returns the ACTIVE kind's filter, so a switch re-seeds it with that
                // kind's own search; typing filters the active kind only.
                new InputElement(() => SelFilter, SetSelFilter, SearchW, OnChange: SetSelFilter)
                {
                    SingleLine  = true,   // one fixed 28 px line: a long paste scrolls sideways instead of growing the panel
                    Placeholder = () => _loc.T(_activeTab == 0 ? "cd.search.skills"
                                              : _activeTab == 1 ? "cd.search.debuffs" : "cd.search.buffs"),
                },
                ClearButton(() => SetSelFilter(string.Empty)),
            }, Gap: PanelGap),
            new ConditionalElement(SelectShowsEmpty, empty, list, Fill: true),
            BuildSelectFooter(),   // Plugin.Settings.Feedback.cs
        });
    }

    // One kind chip "Skills 2": on = the kind's colour (cyan / red / green — the bar's tile colours) bold on its 22 %
    // wash, off = muted on the Tile; the ticked count is regular weight in both. Two complete chips in one fixed cell,
    // swapped by a Conditional (bold can't toggle live), sharing one hover flag — switching never moves anything.
    private HudElement KindChip(int tab)
    {
        var hover = new HoverFlag();
        string key = tab == 0 ? "cd.tab.skills" : tab == 1 ? "cd.tab.debuffs" : "cd.tab.buffs";
        var kind = tab == 0 ? CooldownCol : tab == 1 ? DebuffCol : BuffCol;
        ColorRgba OnFill() => Over(kind with { A = AccentWashAlpha }, PanelColor());
        ColorRgba OnText() => EnsureContrast(kind, OnFill(), SolidMinContrast);
        string Label() => _loc.T(key);
        string Count() => KindTickedText(tab);
        void Pick() => SwitchTab(tab);
        HudElement Inner(bool on) => new RowElement(new HudElement[]
        {
            new CellElement(on
                ? DropBold(new TextElement(Label, Color: () => OnText(), Align: TextAlign.Right, NoWrap: true,
                    FontSize: 13) { Bold = true, Ellipsize = true })
                : new TextElement(Label, Color: () => MutedText(), Align: TextAlign.Right, NoWrap: true) { Ellipsize = true },
                Width: KindChipLabelW),
            new CellElement(new TextElement(Count, Color: () => on ? Mix(OnText(), OnFill(), 0.2f) : MutedText(),
                NoWrap: true), Width: KindChipCountW),
        }, Gap: 5f, Justify: RowJustify.Center);
        return new CellElement(new ConditionalElement(() => _activeTab == tab,
            ChipShell(Inner(true), OnFill, hover, Pick, KindChipW),
            ChipShell(Inner(false), TileColor, hover, Pick, KindChipW)), Width: KindChipW);
    }

    private TrackMode ActiveTabMode() => _activeTab switch
    {
        0 => _selection.SkillMode,
        1 => _selection.DebuffMode,
        _ => _selection.BuffMode,
    };

    private void SetActiveTabMode(TrackMode m)
    {
        if (ActiveTabMode() == m) return;
        switch (_activeTab)
        {
            case 0: _selection.SetSkillMode(m);  break;
            case 1: _selection.SetDebuffMode(m); break;
            default: _selection.SetBuffMode(m);  break;
        }
        _selection.Save(_cfg);
        CancelUntickAll();   // the confirm's consequence line depends on the mode
    }

    private void SwitchTab(int tab)
    {
        if (_activeTab == tab) return;
        _activeTab = tab;
        CancelUntickAll();
        // Re-apply this kind's filter (keeps its search): re-sorts so ticked rows float to the top, resets the scroll.
        if (tab == 0) { EnsureSkillTabLoaded(); ApplySkillTabFilter(_stFilter); }
        else
        {
            EnsureBuffTabLoaded();
            if (tab == 1) ApplyDebuffTabFilter(_dtFilter);
            else          ApplyBuffTabFilter(_btFilter);
        }
    }

    private void SetSelFilter(string s)
    {
        s ??= string.Empty;
        if (string.Equals(s, SelFilter, StringComparison.Ordinal)) return;
        if (_activeTab == 0)      ApplySkillTabFilter(s);
        else if (_activeTab == 1) ApplyDebuffTabFilter(s);
        else                      ApplyBuffTabFilter(s);
    }

    // The list's Count func. Loads BOTH tables on the first refresh the window is really SHOWN (not during the hidden
    // pre-build fill) — every kind chip, the header status and the footer count grouped rows, so all three kinds must be
    // loaded before any number shows ("…" until then). A failed load (tables not ready yet) retries on the next refresh
    // while it stays open. Each Ensure is a single bool check once loaded.
    private int SelListCount()
    {
        if (_settings != null && _settings.IsShown)
        {
            EnsureSkillTabLoaded();
            EnsureBuffTabLoaded();
        }
        return SelFiltCount;
    }

    private void SetSelOffset(int first)
    {
        if (_activeTab == 0)      _stOffset = first;
        else if (_activeTab == 1) _dtOffset = first;
        else                      _btOffset = first;
    }

    private bool ConsumeSelScrollReset()
    {
        if (!_selScrollReset) return false;
        _selScrollReset = false;
        return true;
    }

    // ── live text (each composed only when its inputs change) ─────────────────────────────────────────────────────
    private readonly FmtCache _hintFmt = new();
    private readonly FmtCache[] _kindCountFmt = { new(), new(), new() };

    // "632 skills" with no search, "48 / 312 debuffs" while searching.
    private string ShowHint()
    {
        int total = _activeTab == 0 ? _stCount : _activeTab == 1 ? _dtCount : _btCount;
        bool searching = !string.IsNullOrWhiteSpace(SelFilter);
        string key = _activeTab == 0 ? (searching ? "cd.count.skills"  : "cd.hint.skills")
                   : _activeTab == 1 ? (searching ? "cd.count.debuffs" : "cd.hint.debuffs")
                   :                   (searching ? "cd.count.buffs"   : "cd.hint.buffs");
        return searching ? _hintFmt.Get(_loc.T(key), SelFiltCount, total) : _hintFmt.Get(_loc.T(key), total);
    }

    private string KindTickedText(int tab)
    {
        EnsureTickCounts();
        int n = tab == 0 ? _stTicked : tab == 1 ? _dtTicked : _btTicked;
        return n < 0 ? PendingText : _kindCountFmt[tab].Get("{0}", n);   // "…" until that kind's table is loaded
    }

    private bool SelectShowsEmpty() => SelFiltCount == 0 && !string.IsNullOrWhiteSpace(SelFilter);

    // "No skills match “q”" — rebuilt only when the query, the kind or the language changes.
    private string? _emptyTpl, _emptyQ;
    private string _emptyText = string.Empty;

    private string SelectEmptyText()
    {
        var tpl = _loc.T(_activeTab == 0 ? "cd.empty.skills" : _activeTab == 1 ? "cd.empty.debuffs" : "cd.empty.buffs");
        var q = SelFilter;
        if (!string.Equals(tpl, _emptyTpl, StringComparison.Ordinal) || !string.Equals(q, _emptyQ, StringComparison.Ordinal))
        {
            _emptyTpl = tpl; _emptyQ = q;
            try { _emptyText = string.Format(CultureInfo.CurrentCulture, tpl, q.Trim()); }
            catch (FormatException) { _emptyText = tpl; }
        }
        return _emptyText;
    }
}
