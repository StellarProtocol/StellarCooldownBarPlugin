using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

/// <summary>
/// HUD tracker for the local player's skill cooldowns and self-debuffs. The user curates which cooldowns/debuffs
/// appear via the settings picker (a live "seen this session" list); the bar shows only tracked items that are
/// currently active. Imagine-lockout debuffs render with their source Imagine's artwork. Skill cooldowns are
/// cyan, debuffs red. Hotkey F8 toggles the settings picker.
/// </summary>
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "CooldownBar";

    private readonly IPluginServices _services;
    private readonly ILocalization _loc;
    private readonly IConfigSection _cfg;
    private readonly CooldownBarSelection _selection;
    private readonly DebuffAttribution _attr;
    private IWindowControl? _bar;              // null in Placed mode (Plugin.Placed.cs)
    private readonly IWindowControl _settings;
    private readonly IWindowControl _tooltip;
    private readonly IHotkeyAction _toggleAction;
    private readonly IDisposable _launcherEntry;

    // Live snapshot rebuilt each tick; read by the overlay Funcs on the same main thread (no lock).
    private TrackedTile[] _tiles = Array.Empty<TrackedTile>();
    private int _tileCount;
    private const int MaxTiles = 64;

    // Bar panel opacity (the Background slider): the rounded theme panel behind the tiles at this alpha (0 = fully
    // see-through, 1 = solid). Persisted in config (same key as the old black box's opacity). Plugin.Bar.Frame.cs.
    private float _bgOpacity;

    // "Show hidden effects": when on, the picker lists ALL buff/skill rows (no Name/Icon filter) and the bar reads
    // the full unfiltered buff list, so internal/icon-less effects can be tracked. Persisted in config.
    private bool _showHidden;

    // "Hide when empty" (Bar mode): the bar doesn't draw while it has no active tile, except in the layout editor.
    // Position/size untouched (ShouldRender only). Persisted in config, default off.
    private bool _hideEmpty;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;
        _cfg = _services.Config.GetSection("cooldownbar");
        InitPerf();   // Plugin.Perf.cs — diagnostics-gated [CdPerf] probe; must precede BuildRoot
        _bgOpacity = _cfg.Get("bar.bg_opacity", 0f);
        _showHidden = _cfg.Get("bar.show_hidden", false);
        _hideEmpty  = _cfg.Get("bar.hide_empty", false);
        BuffTrackPatch.ShowHidden = _showHidden;               // full-vs-showed buff list source for the bar
        TranslatedBuffText.EnsureLoaded();   // English NameDesign labels — parsed on a background task; OnUpdate polls ConsumeLoaded
        _selection = CooldownBarSelection.Load(_cfg);
        InitNormalize();      // Plugin.Normalize.cs — has the stale-id conversion run on this install?
        _attr = new DebuffAttribution(
            buffSkillId:    id => _services.GameData.Combat.GetBuff(id)?.SkillId ?? 0,
            imagineSkillOf: sk => _services.ResonanceData.GetImagineForSkill(sk)?.SkillId ?? 0);
        _tiles = new TrackedTile[TileCap];   // live tiles + one edit-mode sample per placed effect (Plugin.Placed.cs)
        SkillCDPatch.Install(_services.Harmony.Create("skillcd"), _services.Log.Info);
        BuffTrackPatch.Install(_services.Harmony.Create("buff"), _services.Log.Info,
            () => _services.CombatSnapshot.ServerNowMs);   // framework server clock (Server-Clock-and-Ping-HUD.md)

        InitBarMigration();   // Plugin.Bar.Frame.cs — one-time saved-height → row-count mapping for the taller tile
        InitPlaced();         // Plugin.Placed.cs — display mode, size, placement order, per-effect sizes
        // Bar mode only: in Placed mode the bar is not registered at all (a removed window also leaves the layout
        // editor; a hidden one would still be force-shown there). Placed windows register once the tables are loaded.
        if (!_placedMode) _bar = RegisterBar();

        _helpWindow = RegisterHelpWindow();   // before Settings: its "?" buttons read _helpWindow on every refresh
        _sizePop    = RegisterSizePopover();  // Plugin.Settings.Placed.cs — the per-effect size picker
        _settings = BuildAndRegisterSettings();
        _tooltip  = BuildAndRegisterTooltip();

        _toggleAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction(
                Id:               "cooldownbar.settings",
                Description:      _loc.T("cd.hotkey.toggle"),
                SuggestedDefault: new KeyBinding(StellarKeyCode.F8)),
            callback: ToggleSettings);

        // Launcher tile: the HUD bar is always-on in world, so the meaningful click action is opening the
        // settings picker (the same thing F8 toggles). Title stays the fixed literal "CooldownBar" — the
        // stable pin-identity key (ILauncher.cs:49-50) — so a pinned tile survives a language change;
        // TitleProvider carries the live-localized display text.
        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   "CooldownBar",
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  OpenSettingsFromLauncher)
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile DISPLAY on a language change; Title above never changes (pin identity).
          TitleProvider = () => _loc.T("cd.launcher.title"),
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });

        _services.Framework.Update += OnUpdate;
    }

    // Borderless window (chrome-less, HUD-like) via WindowBuilder — the icon-capable render path. Its rounded theme
    // panel is the root Image, restyled by Plugin.Bar.Frame.cs (no WindowSpec.BackgroundOpacity: that painted it
    // black). Height limits = exactly 1 and 3 tile rows (BarHeightFor, Plugin.Seen.cs); MinWidth 150 still fits two
    // 50 px tiles (2·50 + 6 + 2·12 = 130). Re-registered (same id → saved rect) when switching back from Placed mode.
    private IWindowControl RegisterBar()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "cooldownbar.main",
                Title:       _loc.T("cd.window.title"),
                DefaultRect: new WindowRect(897f, 940f, 320f, BarHeightFor(1)),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = true, Draggable = true,
              EditModeDragOnly = true,
              // In-world HUD: draw only in the World phase, and hide behind blocking overlays (old AutoHideBehindGameMenus)
              // and the line-selector / any menu.
              ShouldRender = () => BarGateOpen()
                                   && !_perfOff              // [CdPerf] in-session off control (always false unless probe on)
                                   && !BarHiddenWhenEmpty,   // "Hide when empty": a few bool/int reads, no allocation
              Resizable = true, MinWidth = 150f, MaxWidth = 1600f,
              MinHeight = BarHeightFor(1), MaxHeight = BarHeightFor(MaxRows) },
            BuildRoot()));

    // The bar's game-state gate: in the World phase, no blocking overlay, no menu / line selector.
    private bool BarGateOpen()
        => _services.ClientState.Phase == GamePhase.World
           && (_services.ClientState.UiState & (GameUIState.Blocking | GameUIState.AnyMenu)) == 0;

    // "Hide when empty" is holding the bar back: no active tile, and not in the layout editor (there the empty bar
    // shows, so it can be moved and resized).
    private bool BarHiddenWhenEmpty => _hideEmpty && _totalTileCount == 0 && !_services.Windows.IsLayoutEditing;

    public void Dispose()
    {
        FlushBgSave();   // an opacity drag released just before unload still persists
        _launcherEntry.Dispose();
        _services.Framework.Update -= OnUpdate;
        SkillCDPatch.Uninstall();
        BuffTrackPatch.Uninstall();
        _toggleAction.Dispose();
        _tooltip.Remove();
        _helpWindow.Remove();
        _sizePop.Remove();
        _settings.Remove();
        _bar?.Remove();
        RemoveAllPlaced();  // Plugin.Placed.cs
        DisposeBarFrame();  // Plugin.Bar.Frame.cs — the baked panel sprite
        ClearBarCaches();   // Plugin.Bar.Cache.cs
    }

    private float _rebuildAccum;
    // Game reads (buff list walk + CD queries) at 10 Hz. The bar still moves smoothly: the overlay Funcs extrapolate each
    // tile's sample every frame (TrackedTile.RemainAt / Plugin.Smooth.cs). The demand window (500 ms) stays > 100 ms,
    // so the postfixes stay awake between reads. (The old 1/60 s cap never engaged: OnUpdate arrives at ~30 Hz.)
    private const float RebuildIntervalS = 0.1f;

    private void OnUpdate(float deltaTime)
    {
        var perfUpdate = CdPerf.Begin();   // Plugin.Perf.cs — default (no Stopwatch read) unless the probe is on
        // Only read buff/CD state when the bar is actually visible, and at 10 Hz — not every frame.
        // RefreshActiveBuffs walks the buff lists via reflection; running it every frame (even while the bar is
        // hidden) was a steady combat-time GC source. The window's Funcs still draw every frame from the last-built
        // _tiles samples, so the bar stays smooth; only the data refresh throttles.
        // Placed mode: the same 10 Hz read while any placed tile COULD show (in world, no menu, or the layout editor) —
        // the placed windows are hidden until their effect is active, so their own IsShown can't gate it.
        // "Hide when empty" hides the bar through ShouldRender, so IsShown goes false while nothing is active: keep reading
        // then too (only when nothing ELSE hides it — not in world, a menu, Placed mode), or it would never come back.
        bool barShown = _bar != null && _bar.IsShown && !_perfOff;
        bool barLive = barShown || (_bar != null && !_perfOff && _hideEmpty && BarGateOpen());
        bool placedLive = PlacedLive();   // Plugin.Placed.cs — false in Bar mode
        if (!placedLive && _placedLive) ClearPlacedTiles();   // gate closed: no stale tile may show on the next open
        _placedLive = placedLive;
        if (barLive || placedLive)
        {
            _rebuildAccum += deltaTime;
            if (_rebuildAccum >= RebuildIntervalS)
            {
                // Carry the remainder so ~30 Hz ticks still average 10 reads/s (a reset to 0 would give 7.5 Hz);
                // clamp after a long stall so we don't fire a burst of catch-up reads.
                _rebuildAccum -= RebuildIntervalS;
                if (_rebuildAccum > RebuildIntervalS) _rebuildAccum = 0f;
                var perfRefresh = CdPerf.Begin();
                BuffTrackPatch.MarkDemand();   // keep the per-entity buff/CD postfixes awake only while refreshing
                SkillCDPatch.MarkDemand();
                RebuildSnapshot();             // Plugin.Seen.cs
                CdPerf.End(CdPerf.Refresh, perfRefresh);
            }
            if (barShown)
            {
                TickBarFrame();                // Plugin.Bar.Frame.cs — re-style a remounted root, live panel colour
                TickBarMigration(deltaTime);   // one-time; a single compare once done
            }
        }
        TickEagerTables();        // Plugin.Normalize.cs — table load + stale-id conversion, until done
        TickPlaced();             // Plugin.Placed.cs — placed-window sync on a selection change, edit-mode icon fade
        if (TranslatedBuffText.ConsumeLoaded(out var loadMsg)) OnTranslatedTextLoaded(loadMsg);   // one int read/frame
        BgSaveTick();             // Plugin.Settings.cs — debounced config write after an opacity-slider drag
        TickTooltipPlace();       // Plugin.Tooltip.cs — re-assert cursor rect after destroy-on-hide remount
        TickSettingsUi();         // Plugin.Settings.Open.cs — help re-anchor, open centring, Untick-all confirm timeout
        LogSnapshotDiag(deltaTime);   // Plugin.Diagnostics.cs — gated on STELLAR_DIAGNOSTICS
        if (CdPerf.On) PerfTick(deltaTime, perfUpdate);   // Plugin.Perf.cs
    }

    // Main thread, once, when the background BuffTable.json parse lands (TranslatedBuffText). Labels resolved during the
    // load window used the game-name fallback: drop the bar's cached badges, and if the Show-hidden picker tables were
    // already built from fallback labels, rebuild them (now if the settings window is open, else on next access).
    private void OnTranslatedTextLoaded(string msg)
    {
        _services.Log.Info(msg);
        ClearBarCaches();   // Plugin.Bar.Cache.cs
        if (_showHidden) _btJob = null;   // a half-done sliced buff walk used fallback labels: restart it
        if (_showHidden && _buffTabLoaded)
        {
            _buffTabLoaded = false;
            if (_settings.IsShown) EnsureBuffTabLoaded();   // re-applies the active debuff/buff search filters
        }
    }

    private static byte[]? LoadIconPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.CooldownBar.cooldownbar-icon.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
