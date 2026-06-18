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
    private readonly IConfigSection _cfg;
    private readonly CooldownBarSelection _selection;
    private readonly SeenRegistry _seen = new();
    private readonly DebuffAttribution _attr;
    private readonly IWindowControl _bar;
    private readonly IWindowControl _settings;
    private readonly IHotkeyAction _toggleAction;

    // Live snapshot rebuilt each tick; read by the overlay Funcs on the same main thread (no lock).
    private TrackedTile[] _tiles = Array.Empty<TrackedTile>();
    private int _tileCount;
    private const int MaxTiles = 16;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _cfg = _services.Config.GetSection("cooldownbar");
        _selection = CooldownBarSelection.Load(_cfg);
        _attr = new DebuffAttribution(
            buffSkillId:          id => _services.GameData.Combat.GetBuff(id)?.SkillId ?? 0,
            isImagineSkill:       sk => _services.ResonanceData.GetImagineForSkill(sk) is not null,
            curatedImagineByBuff: ImagineLockouts.Map);
        _tiles = new TrackedTile[MaxTiles];
        // Cooldown reduction (11760 ratio per-10000, 11750 flat ms) + acceleration attrs (11960/11980, gear/buffs)
        // feed EffectiveDur. These are FLOAT-stored — the stats probe now reads them via TryGetAttr<float>, so the
        // live value (per-gear baseline + temp buffs like Tina's cd-accel) is reflected instead of null.
        _services.PlayerStats.Subscribe(CdReductionAttr);
        _services.PlayerStats.Subscribe(CdReductionFixedAttr);
        _services.PlayerStats.Subscribe(CdAccelSkillAttr);
        _services.PlayerStats.Subscribe(CdAccelImagineAttr);

        // Borderless window (chrome-less, HUD-like) via WindowBuilder — the icon-capable render path
        // (the HUD builder has no GameTextureElement support). Mirrors CombatMeter's overlay.
        _bar = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "cooldownbar.main",
                Title:       "CooldownBar",
                DefaultRect: new WindowRect(897f, 940f, 320f, 96f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = true, HideUntilInWorld = true, Draggable = true,
              EditModeDragOnly = true, AutoHideBehindGameMenus = true },
            BuildRoot()));

        _settings = BuildAndRegisterSettings();

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
        _toggleAction.Dispose();
        _settings.Remove();
        _bar.Remove();
    }

    private void OnUpdate(float deltaTime)
    {
        RebuildSnapshot();   // Plugin.Seen.cs — the window auto-refreshes its Funcs on the framework tick
        LogSnapshotDiag(deltaTime);   // Plugin.Diagnostics.cs — gated on STELLAR_DIAGNOSTICS
    }
}
