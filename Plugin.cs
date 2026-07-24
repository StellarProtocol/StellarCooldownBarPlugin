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

    private const string HarmonyId = "stellar.cooldownbar";

    private readonly IPluginServices _services;
    private readonly IConfigSection _cfg;
    private readonly CooldownBarSelection _selection;
    private readonly DebuffAttribution _attr;
    private readonly IWindowControl _bar;
    private readonly IWindowControl _settings;
    private readonly IWindowControl _tooltip;
    private readonly IHotkeyAction _toggleAction;

    // Live snapshot rebuilt each tick; read by the overlay Funcs on the same main thread (no lock).
    private TrackedTile[] _tiles = Array.Empty<TrackedTile>();
    private int _tileCount;
    private const int MaxTiles = 64;

    // Bar background: black at this opacity (0 = fully transparent, 1 = fully black). Persisted in config.
    private float _bgOpacity;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _cfg = _services.Config.GetSection("cooldownbar");
        _bgOpacity = _cfg.Get("bar.bg_opacity", 0f);
        _selection = CooldownBarSelection.Load(_cfg);
        _attr = new DebuffAttribution(
            buffSkillId:    id => _services.GameData.Combat.GetBuff(id)?.SkillId ?? 0,
            isImagineSkill: sk => _services.ResonanceData.GetImagineForSkill(sk) is not null);
        _tiles = new TrackedTile[MaxTiles];
        SkillCDPatch.Install(HarmonyId, _services.Log.Info);
        BuffTrackPatch.Install(HarmonyId, _services.Log.Info);

        // Borderless window (chrome-less, HUD-like) via WindowBuilder — the icon-capable render path
        // (the HUD builder has no GameTextureElement support). Mirrors CombatMeter's overlay.
        _bar = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "cooldownbar.main",
                Title:       "CooldownBar",
                DefaultRect: new WindowRect(897f, 940f, 320f, 130f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = true, HideUntilInWorld = true, Draggable = true,
              EditModeDragOnly = true, AutoHideBehindGameMenus = true,
              Resizable = true, MinWidth = 150f, MaxWidth = 1600f, MinHeight = 130f, MaxHeight = 270f,
              BackgroundOpacity = () => _bgOpacity },
            BuildRoot()));

        _settings = BuildAndRegisterSettings();
        _tooltip  = BuildAndRegisterTooltip();

        _toggleAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction(
                Id:               "cooldownbar.settings",
                Description:      "Toggle CooldownBar settings",
                SuggestedDefault: new KeyBinding(StellarKeyCode.F8)),
            callback: () => _settings.SetVisible(!_settings.IsShown));

        _services.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _services.Framework.Update -= OnUpdate;
        SkillCDPatch.Uninstall();
        BuffTrackPatch.Uninstall();
        _toggleAction.Dispose();
        _tooltip.Remove();
        _settings.Remove();
        _bar.Remove();
    }

    private float _rebuildAccum;
    private const float RebuildIntervalS = 1f / 60f;   // cap buff/CD data refresh at 60Hz (smooth; still caps on high-fps setups)

    private void OnUpdate(float deltaTime)
    {
        // Only read buff/CD state when the bar is actually visible, and at ~20Hz — not every frame.
        // RefreshActiveBuffs rebuilds collections + walks the buff lists via reflection; running it every
        // frame (even while the bar is hidden) was a steady combat-time GC source. The window's Funcs still
        // draw every frame from the last-built _tiles, so the bar stays smooth; only the data refresh throttles.
        if (_bar.IsShown)
        {
            _rebuildAccum += deltaTime;
            if (_rebuildAccum >= RebuildIntervalS)
            {
                _rebuildAccum = 0f;
                BuffTrackPatch.MarkDemand();   // keep the per-entity buff/CD postfixes awake only while refreshing
                SkillCDPatch.MarkDemand();
                RebuildSnapshot();             // Plugin.Seen.cs
            }
        }
        TickTooltipPlace();       // Plugin.Tooltip.cs — re-assert cursor rect after destroy-on-hide remount
        LogSnapshotDiag(deltaTime);   // Plugin.Diagnostics.cs — gated on STELLAR_DIAGNOSTICS
    }
}
