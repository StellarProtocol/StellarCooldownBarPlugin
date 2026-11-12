using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

/// <summary>
/// Settings window, house style (approved redesign 2026-11-13, <c>_scratch\prototypes\cooldownbar-redesign*.{md,html}</c>).
/// A fixed-size 420 px GlassMenu window, opened by F8 / the bar's gear / the launcher tile:
///   ToolHeader: [40 px accent tile, white hourglass] [bold "Cooldown Bar" / muted "Showing 2 skills · 11 debuffs · 342 buffs"]
///   BAR panel ("how the bar looks"): Background [round-knob slider] [bold 80 %] [?] · Hidden effects [Off/On] [?]
///   WHAT TO SHOW panel: kind chips · mode chips + [?] · search + ✕ · list (or "No … match") · footer / Untick-all confirm
/// The WHAT TO SHOW panel: Plugin.Settings.Show.cs; rows: Plugin.Settings.Rows.cs; row flash / footer / Untick all:
/// Plugin.Settings.Feedback.cs; "?" popover: Plugin.Settings.Help.cs; table load + filter: Plugin.Settings.Tables.cs.
/// Tables load lazily the first time the window is SHOWN (game tables may not be ready at plugin init). A toggle writes
/// the config at once; the bar reflects it on its next 10 Hz rebuild.
/// </summary>
public sealed partial class Plugin
{
    // Window: 420 wide, FIXED height (WindowSpec.FixedHeight), so nothing it shows — search results, the empty note, the
    // Untick-all confirm, feedback — can grow or shrink it (house rule 14). Height = title bar 32 + body padding 2×12 +
    // header ≈42 + BAR panel ≈102 (padding 16 + caption ≈18 + 2 rows of 28 + 2 gaps of 6) + WHAT TO SHOW panel without
    // its list ≈144 (padding 16 + caption ≈18 + kind chips 24 + mode row 24 + search 28 + footer 28 + 5 gaps of 6) +
    // 2 gaps of 10 = 364 + the list. 620 → the list gets ≈256 (9 rows, the prototype's list height); the confirm block
    // (≈90, vs the 28 px footer) takes 62 of that → 7 rows. The list viewport is FLEXIBLE (ConditionalElement Fill, base
    // 6 rows): it takes the column's spare height, so the footer ↔ confirm swap moves the list's bottom edge, never the
    // window's (TargetLens Select-window recipe).
    private const float SettingsWindowW = 420f;
    private const float SettingsWindowH = 620f;
    private const float SettingsGap     = 10f;   // between header / panels (the prototype's 10)
    private const float PanelGap        = 6f;    // inside a panel
    // Body 396 (420 − 2×12) − panel padding 2×10 = 376 inside a panel.
    private const float PanelInnerW     = SettingsWindowW - 24f - 20f;
    private const float SettingsRowH    = 28f;
    private const float BgLabelW        = 110f;  // the prototype's fixed label column ("Background", th "พื้นหลัง")
    private const float BgValueW        = 40f;   // bold "100%"

    private IWindowControl BuildAndRegisterSettings()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "cooldownbar.settings",
                Title:       _loc.T("cd.window.title"),
                DefaultRect: new WindowRect(900f, 120f, SettingsWindowW, SettingsWindowH),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            {
                TitleProvider = () => _loc.T("cd.window.title"),   // live-localized; Title stays the fallback
                StartVisible = false, Closable = true, Draggable = true,
                FixedHeight  = true,   // the height is DefaultRect.Height, never the content's (sizing note above)
                // Gameplay tool: draw only while in-world, and hide during loading screens.
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
                // Framework pre-build (fw 2.30.0): the 14-row pool is built at the title screen and filled off-screen
                // once in the world, so the first open is a re-show. The value lambdas only read plugin fields (+ _loc);
                // the table loads wait for a real show (SelListCount), so the fill never runs the reflection walks
                // behind the player's back.
                Prebuild = true,
            },
            BuildSettingsRoot(),
            OnClose: CloseSettings));

    private HudElement BuildSettingsRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader("hourglass-24.png", () => _loc.T("cd.settings.title"), SettingsStatus),
        BuildBarSection(),
        BuildShowSection(),   // Plugin.Settings.Show.cs
    }, Gap: SettingsGap);

    // F8 / the bar's gear: toggle. Hiding also drops the help tip and a pending confirm.
    private void ToggleSettings()
    {
        if (_settings.IsShown) CloseSettings();
        else _settings.SetVisible(true);
    }

    private void CloseSettings()
    {
        _settings.SetVisible(false);
        CancelUntickAll();
        CloseHelp();
    }

    // OnUpdate: the help popover re-anchor and the confirm timeout. Each is a single compare when idle.
    private void TickSettingsUi()
    {
        HelpRepositionTick();
        AdvanceUntickConfirm();   // Plugin.Settings.Feedback.cs
    }

    // ── header status ─────────────────────────────────────────────────────────────────────────────────────────────
    private readonly FmtCache _statusFmt = new();
    private readonly FmtCache[] _statusPartFmt = { new(), new(), new() };
    private string? _statusP0, _statusP1, _statusP2;
    private string _statusMixed = string.Empty;

    // The ticked count per kind (the kind chips show the same numbers), worded by each kind's mode: a kind in
    // "All except ticked" HIDES its ticked effects. All three in one mode → one verb: "Showing 2 skills · 11 debuffs ·
    // 342 buffs" / "Hiding …"; mixed → a verb per kind: "Showing 2 skills · Hiding 4 debuffs · Showing 10 buffs".
    // Each part is a cached format; the joined line is rebuilt only when a part string changed (reference compare).
    private string SettingsStatus()
    {
        EnsureTickCounts();   // Plugin.Settings.Rows.cs
        bool hs = _selection.SkillMode  == TrackMode.ExcludeBelow;
        bool hd = _selection.DebuffMode == TrackMode.ExcludeBelow;
        bool hb = _selection.BuffMode   == TrackMode.ExcludeBelow;
        if (hs == hd && hd == hb)
            return _statusFmt.Get(_loc.T(hs ? "cd.settings.status.hide" : "cd.settings.status"), _stTicked, _dtTicked, _btTicked);
        var p0 = _statusPartFmt[0].Get(_loc.T(hs ? "cd.status.hide.skills"  : "cd.status.show.skills"),  _stTicked);
        var p1 = _statusPartFmt[1].Get(_loc.T(hd ? "cd.status.hide.debuffs" : "cd.status.show.debuffs"), _dtTicked);
        var p2 = _statusPartFmt[2].Get(_loc.T(hb ? "cd.status.hide.buffs"   : "cd.status.show.buffs"),   _btTicked);
        if (!ReferenceEquals(p0, _statusP0) || !ReferenceEquals(p1, _statusP1) || !ReferenceEquals(p2, _statusP2))
        {
            _statusP0 = p0; _statusP1 = p1; _statusP2 = p2;
            _statusMixed = p0 + " · " + p1 + " · " + p2;
        }
        return _statusMixed;
    }

    // ── BAR panel ─────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildBarSection() => SectionPanel(() => _loc.T("cd.sec.bar"), () => _loc.T("cd.sec.barHint"),
        new HudElement[]
        {
            SettingRow(
                new CellElement(new TextElement(() => _loc.T("cd.bg"), Color: () => TextColor(), NoWrap: true)
                    { Ellipsize = true }, Width: BgLabelW),
                new CellElement(RoundSlider(() => _bgOpacity, SetBgOpacity, 0f, 1f), Weight: 1f),
                new CellElement(new TextElement(BgPercent, Color: () => TextColor(), Align: TextAlign.Right, NoWrap: true,
                    FontSize: 13) { Bold = true }, Width: BgValueW),
                HelpDot("cd.bg", "cd.help.bg", "cd.sec.bar")),
            SettingRow(
                new CellElement(new TextElement(() => _loc.T("cd.showHidden"), Color: () => TextColor(), NoWrap: true)
                    { Ellipsize = true }, Weight: 1f),
                OnOffChip(() => _showHidden, SetShowHidden),
                HelpDot("cd.showHidden", "cd.help.hidden", "cd.sec.bar")),
        });

    // A 28 px row: a fixed strut pins the height, the real content sits in one weighted cell. The strut lives in its OWN
    // fixed cell beside the content (a zero-width SpacerElement inside a RowElement is stretchy and would steal width
    // from the label — WindowBuilder-Patterns, 2026-11-07); outer Gap 0.01 = no gap (Gap 0 would be the DEFAULT gap).
    private static HudElement SettingRow(params HudElement[] inner) => new RowElement(new HudElement[]
    {
        new CellElement(new SpacerElement(0f, SettingsRowH), Width: 0.01f),
        new CellElement(new RowElement(inner, Gap: 6f), Weight: 1f),
    }, Gap: 0.01f);

    // "80%", rebuilt only when the shown whole percent changes (the slider polls every refresh).
    private int _bgPctShown = -1;
    private string _bgPctText = string.Empty;

    private string BgPercent()
    {
        int p = (int)MathF.Round(_bgOpacity * 100f);
        if (p != _bgPctShown) { _bgPctShown = p; _bgPctText = p.ToString(CultureInfo.InvariantCulture) + "%"; }
        return _bgPctText;
    }

    // SliderElement calls this on EVERY drag frame, and a config Save is a file write — one per frame while dragging
    // hitched the game thread. Apply live + set in memory now; write to disk ~0.5 s after the last change (BgSaveTick in
    // OnUpdate), and flush on Dispose. Mirrors StatInspector 7fc445a (Plugin-Performance-Rules.md §5).
    private const long BgSaveDelayMs = 500;
    private long _bgSaveAt = -1;   // Environment.TickCount64 when the pending save is due; -1 = nothing pending

    private void SetBgOpacity(float v)
    {
        if (v == _bgOpacity) return;
        _bgOpacity = v;
        _cfg.Set("bar.bg_opacity", v);
        _bgSaveAt = Environment.TickCount64 + BgSaveDelayMs;
    }

    private void BgSaveTick()
    {
        if (_bgSaveAt < 0 || Environment.TickCount64 < _bgSaveAt) return;
        FlushBgSave();
    }

    private void FlushBgSave()
    {
        if (_bgSaveAt < 0) return;
        _bgSaveAt = -1;
        _cfg.Save();
    }

    private void SetShowHidden(bool on)
    {
        _showHidden = on;
        _cfg.Set("bar.show_hidden", on);
        _cfg.Save();
        BuffTrackPatch.ShowHidden = on;                       // bar reads full-vs-showed buff list

        // The Name/Icon filter (and the buff-group label resolution) depends on this flag, so drop the loaded
        // picker tables and rebuild them on next access, then re-apply the active search filters so an already-open
        // list repopulates immediately — without needing to close and reopen the settings window.
        ClearBarCaches();   // Plugin.Bar.Cache.cs — tile labels/icons re-resolve under the new mode
        _buffTabLoaded = false;
        _stBaseLoaded  = false;
        _seenDirty     = true;
        EnsureSkillTabLoaded();
        EnsureBuffTabLoaded();
        ApplySkillTabFilter(_stFilter);
        ApplyDebuffTabFilter(_dtFilter);
        ApplyBuffTabFilter(_btFilter);
    }
}
