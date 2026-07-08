using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// Settings picker — 3 tabs (Skills / Debuffs / Buffs) each with search input + virtual list + toggle per row.
// Tables are loaded lazily on first tab visit (game tables may not be ready at plugin init). Toggling a row
// writes the config immediately; the bar reflects it on the next framework tick.
public sealed partial class Plugin
{
    private static readonly ColorRgba ActiveTabCol      = new(0.35f, 0.78f, 1.00f, 1f);  // cyan  — Skills active
    private static readonly ColorRgba InactiveTabCol    = new(0.65f, 0.65f, 0.65f, 1f);  // grey
    private static readonly ColorRgba DebuffTabActiveCol = new(1.00f, 0.35f, 0.35f, 1f); // red   — Debuffs active
    private static readonly ColorRgba BuffTabActiveCol   = new(0.35f, 1.00f, 0.50f, 1f); // green — Buffs active
    private static readonly string[] ModeOptions = { "Show only selected", "Show all, exclude selected" };

    private int _activeTab = 0;   // 0 = Skills, 1 = Debuffs, 2 = Buffs
    private bool _stScrollReset, _dtScrollReset, _btScrollReset;

    private IWindowControl BuildAndRegisterSettings()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "cooldownbar.settings",
                Title:       "CooldownBar — Track",
                DefaultRect: new WindowRect(900f, 120f, 360f, 500f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, HideUntilInWorld = true, Closable = true, Draggable = true },
            BuildSettingsRoot(),
            OnClose: () => _settings.SetVisible(false)));

    private void SetBgOpacity(float v)
    {
        _bgOpacity = v;
        _cfg.Set("bar.bg_opacity", v);
        _cfg.Save();
    }

    private HudElement BuildSettingsRoot()
    {
        var bgRow = new RowElement(new HudElement[]
        {
            new TextElement(() => "Bar Background"),
            new SpacerElement(Width: 0f),
            new TextElement(() => $"{(int)System.Math.Round(_bgOpacity * 100)}%"),
            new SliderElement(() => _bgOpacity, SetBgOpacity) { Width = 120f },
        }, Gap: 6f);

        var tabStrip = new RowElement(new HudElement[]
        {
            new CellElement(
                new ButtonElement(() => "Skills",
                    OnClick: () => { _activeTab = 0; EnsureSkillTabLoaded(); ApplySkillTabFilter(_stFilter); _stScrollReset = true; },
                    Active: () => _activeTab == 0),
                Weight: 1f),
            new CellElement(
                new ButtonElement(() => "Debuffs",
                    OnClick: () => { _activeTab = 1; EnsureBuffTabLoaded(); ApplyDebuffTabFilter(_dtFilter); _dtScrollReset = true; },
                    Active: () => _activeTab == 1),
                Weight: 1f),
            new CellElement(
                new ButtonElement(() => "Buffs",
                    OnClick: () => { _activeTab = 2; EnsureBuffTabLoaded(); ApplyBuffTabFilter(_btFilter); _btScrollReset = true; },
                    Active: () => _activeTab == 2),
                Weight: 1f),
        }, Gap: 4f);

        var modeRow = new RowElement(new HudElement[]
        {
            new TextElement(() => "Filter mode"),
            new SpacerElement(Width: 0f),
            new DropdownElement(
                Selected: () => (int)ActiveTabMode(),
                Options:  () => ModeOptions,
                OnSelect: v => SetActiveTabMode((TrackMode)v),
                Width: 210f),
        }, Gap: 6f);

        return new ColumnElement(new HudElement[]
        {
            bgRow,
            new SeparatorElement(),
            new TextElement(() => "Toggle what appears on the CooldownBar", Emphasis: true),
            tabStrip,
            modeRow,
            new SeparatorElement(),
            new ConditionalElement(() => _activeTab == 0, BuildSkillsTab()),
            new ConditionalElement(() => _activeTab == 1, BuildDebuffsTab()),
            new ConditionalElement(() => _activeTab == 2, BuildBuffsTab()),
        }, Gap: 4f);
    }

    private TrackMode ActiveTabMode() => _activeTab switch
    {
        0 => _selection.SkillMode,
        1 => _selection.DebuffMode,
        _ => _selection.BuffMode,
    };

    private void SetActiveTabMode(TrackMode m)
    {
        switch (_activeTab)
        {
            case 0: _selection.SetSkillMode(m);  break;
            case 1: _selection.SetDebuffMode(m); break;
            default: _selection.SetBuffMode(m);  break;
        }
        _selection.Save(_cfg);
    }

    // ── Tab content builders (called once; pool HudElements are reused) ───────

    private HudElement BuildSkillsTab()
    {
        var pool = new HudElement[SettingsPoolSize];
        for (int i = 0; i < SettingsPoolSize; i++)
        {
            int idx = i;
            pool[i] = new ConditionalElement(
                () => _stOffset + idx < _stFiltCount,
                new RowElement(new HudElement[]
                {
                    new SelectableElement(
                        new RowElement(new HudElement[]
                        {
                            new CellElement(
                                new GameTextureElement(() => StIcon(idx), 22, 22, () => _stUv[idx]),
                                Width: 26f),
                            new TextElement(() => StLabel(idx)),
                        }, Gap: 4f),
                        OnClick: () => OnSettingIconClick(TileKind.Cooldown, idx)),
                    new SpacerElement(Width: 0f),
                    new ToggleElement(() => "", () => StTracked(idx), v => SetStTracked(idx, v)),
                }, Gap: 4f));
        }
        return new ColumnElement(new HudElement[]
        {
            new InputElement(
                Get: () => _stFilter, Submit: ApplySkillTabFilter,
                Width: 340f, OnChange: ApplySkillTabFilter),
            new TextElement(() => $"{_stFiltCount} / {_stCount} skills"),
            new VirtualListElement(
                Count:    () => { EnsureSkillTabLoaded(); return _stFiltCount; },
                RowHeight: 32f, Pool: pool,
                OnWindow: i => _stOffset = i, Height: 340f)
            { ResetScroll = () => { if (!_stScrollReset) return false; _stScrollReset = false; return true; } },
        }, Gap: 4f);
    }

    private HudElement BuildDebuffsTab()
    {
        var pool = new HudElement[SettingsPoolSize];
        for (int i = 0; i < SettingsPoolSize; i++)
        {
            int idx = i;
            pool[i] = new ConditionalElement(
                () => _dtOffset + idx < _dtFiltCount,
                new RowElement(new HudElement[]
                {
                    new SelectableElement(
                        new RowElement(new HudElement[]
                        {
                            new CellElement(
                                new GameTextureElement(() => DtIcon(idx), 22, 22, () => _dtUv[idx]),
                                Width: 26f),
                            new TextElement(() => DtLabel(idx)),
                        }, Gap: 4f),
                        OnClick: () => OnSettingIconClick(TileKind.Debuff, idx)),
                    new SpacerElement(Width: 0f),
                    new ToggleElement(() => "", () => DtTracked(idx), v => SetDtTracked(idx, v)),
                }, Gap: 4f));
        }
        return new ColumnElement(new HudElement[]
        {
            new InputElement(
                Get: () => _dtFilter, Submit: ApplyDebuffTabFilter,
                Width: 340f, OnChange: ApplyDebuffTabFilter),
            new TextElement(() => $"{_dtFiltCount} / {_dtCount} debuffs"),
            new VirtualListElement(
                Count:    () => { EnsureBuffTabLoaded(); return _dtFiltCount; },
                RowHeight: 32f, Pool: pool,
                OnWindow: i => _dtOffset = i, Height: 340f)
            { ResetScroll = () => { if (!_dtScrollReset) return false; _dtScrollReset = false; return true; } },
        }, Gap: 4f);
    }

    private HudElement BuildBuffsTab()
    {
        var pool = new HudElement[SettingsPoolSize];
        for (int i = 0; i < SettingsPoolSize; i++)
        {
            int idx = i;
            pool[i] = new ConditionalElement(
                () => _btOffset + idx < _btFiltCount,
                new RowElement(new HudElement[]
                {
                    new SelectableElement(
                        new RowElement(new HudElement[]
                        {
                            new CellElement(
                                new GameTextureElement(() => BtIcon(idx), 22, 22, () => _btUv[idx]),
                                Width: 26f),
                            new TextElement(() => BtLabel(idx)),
                        }, Gap: 4f),
                        OnClick: () => OnSettingIconClick(TileKind.Buff, idx)),
                    new SpacerElement(Width: 0f),
                    new ToggleElement(() => "", () => BtTracked(idx), v => SetBtTracked(idx, v)),
                }, Gap: 4f));
        }
        return new ColumnElement(new HudElement[]
        {
            new InputElement(
                Get: () => _btFilter, Submit: ApplyBuffTabFilter,
                Width: 340f, OnChange: ApplyBuffTabFilter),
            new TextElement(() => $"{_btFiltCount} / {_btCount} buffs"),
            new VirtualListElement(
                Count:    () => { EnsureBuffTabLoaded(); return _btFiltCount; },
                RowHeight: 32f, Pool: pool,
                OnWindow: i => _btOffset = i, Height: 340f)
            { ResetScroll = () => { if (!_btScrollReset) return false; _btScrollReset = false; return true; } },
        }, Gap: 4f);
    }

    // ── Skills tab row helpers ────────────────────────────────────────────────

    private object? StIcon(int idx)
    {
        int i = _stOffset + idx;
        if (i >= _stFiltCount) { _stUv[idx] = default; return null; }
        return _services.GameAssets.LoadImagineIcon(_stFiltIds[i], out _stUv[idx])
            ?? _services.GameAssets.LoadSkillIcon(_stFiltIds[i], out _stUv[idx]);
    }

    private string StLabel(int idx)
    {
        int i = _stOffset + idx;
        return i < _stFiltCount ? _stFiltNames[i] : "";
    }

    private bool StTracked(int idx)
    {
        int i = _stOffset + idx;
        return i < _stFiltCount && _selection.IsCooldownTracked(_stFiltIds[i]);
    }

    private void SetStTracked(int idx, bool on)
    {
        int i = _stOffset + idx;
        if (i >= _stFiltCount) return;
        _selection.SetCooldown(_stFiltIds[i], on);
        _selection.Save(_cfg);
    }

    // ── Debuffs tab row helpers ───────────────────────────────────────────────

    private object? DtIcon(int idx)
    {
        int i = _dtOffset + idx;
        if (i >= _dtFiltCount) { _dtUv[idx] = default; return null; }
        return _services.GameAssets.LoadBuffIcon(_dtFiltIds[i], out _dtUv[idx]);
    }

    private string DtLabel(int idx)
    {
        int i = _dtOffset + idx;
        return i < _dtFiltCount ? _dtFiltNames[i] : "";
    }

    private bool DtTracked(int idx)
    {
        int i = _dtOffset + idx;
        return i < _dtFiltCount && _selection.IsDebuffTracked(_dtFiltIds[i]);
    }

    private void SetDtTracked(int idx, bool on)
    {
        int i = _dtOffset + idx;
        if (i >= _dtFiltCount) return;
        _selection.SetDebuff(_dtFiltIds[i], on);
        _selection.Save(_cfg);
    }

    // ── Buffs tab row helpers ─────────────────────────────────────────────────

    private object? BtIcon(int idx)
    {
        int i = _btOffset + idx;
        if (i >= _btFiltCount) { _btUv[idx] = default; return null; }
        return _services.GameAssets.LoadBuffIcon(_btFiltIds[i], out _btUv[idx]);
    }

    private string BtLabel(int idx)
    {
        int i = _btOffset + idx;
        return i < _btFiltCount ? _btFiltNames[i] : "";
    }

    private bool BtTracked(int idx)
    {
        int i = _btOffset + idx;
        return i < _btFiltCount && _selection.IsBuffTracked(_btFiltIds[i]);
    }

    private void SetBtTracked(int idx, bool on)
    {
        int i = _btOffset + idx;
        if (i >= _btFiltCount) return;
        _selection.SetBuff(_btFiltIds[i], on);
        _selection.Save(_cfg);
    }
}
