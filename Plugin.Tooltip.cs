using System.Text.RegularExpressions;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.CooldownBar;

// Click-to-tooltip for bar tiles and settings icons. Left-clicking shows a context-menu-style panel popup
// (PanelElement — themed background + 1px border) with the icon, name, and description.
// DismissOnOutsideClick (framework per-frame ticker, never misses a one-frame edge) handles click-away dismiss;
// clicking the same bar tile again also dismisses.
public sealed partial class Plugin
{
    private string  _tipName = "";
    private string  _tipDesc = "";
    private object? _tipTex;
    private UvRect  _tipUv;
    private int     _tipShownFor = -1;   // bar tile index; -1 when opened from settings
    private bool    _tipOpen;
    private bool    _tipPlaced;
    private WindowRect _tipRect;

    private static readonly Regex TagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static string StripTags(string? s) => s == null ? "" : TagPattern.Replace(s, "");

    private IWindowControl BuildAndRegisterTooltip()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "cooldownbar.tip",
                Title:       "",
                DefaultRect: new WindowRect(897f, 830f, 380f, 130f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, HideUntilInWorld = true, DismissOnOutsideClick = true },
            new PanelElement(
                new ColumnElement(new HudElement[]
                {
                    new RowElement(new HudElement[]
                    {
                        new CellElement(
                            new GameTextureElement(() => _tipTex, 36, 36, () => _tipUv),
                            Width: 44f),
                        new TextElement(() => _tipName, Emphasis: true),
                    }, Gap: 6f),
                    new SeparatorElement(),
                    new TextElement(() => _tipDesc),
                }, Gap: 4f),
                Padding: 8f),
            OnClose: CloseTooltip));

    // Re-assert cursor position after the window remounts (destroy-on-hide: SetRect no-ops on the null token at
    // open time; IsShown becoming true signals the mount landed). Called from OnUpdate.
    internal void TickTooltipPlace()
    {
        if (!_tipOpen || _tipPlaced) return;
        if (_tooltip.IsShown) { _tooltip.SetRect(_tipRect); _tipPlaced = true; }
    }

    private void CloseTooltip()
    {
        _tipOpen     = false;
        _tipPlaced   = false;
        _tipShownFor = -1;
        _tooltip.SetVisible(false);
    }

    // Shared: position the tooltip above-right of the current cursor and show it.
    private void ShowTipAtCursor()
    {
        const float TipW = 380f, TipH = 130f;
        float mx = Input.mousePosition.x + 8f;
        float my = Screen.height - Input.mousePosition.y - TipH - 8f;
        mx = System.Math.Clamp(mx, 0f, Screen.width  - TipW);
        my = System.Math.Clamp(my, 0f, Screen.height - TipH);
        _tipRect   = new WindowRect(mx, my, TipW, TipH);
        _tipPlaced = false;
        _tipOpen   = true;
        _tooltip.SetRect(_tipRect);   // may no-op if still unmounted; TickTooltipPlace re-asserts
        _tooltip.SetVisible(true);
    }

    // Called when a bar tile is left-clicked.
    internal void OnTileClick(int idx)
    {
        if (idx >= _tileCount || (_tipShownFor == idx && _tooltip.IsShown))
        {
            CloseTooltip();
            return;
        }
        var t = _tiles[idx];
        if (t.Kind == TileKind.Cooldown)
        {
            var info = _services.GameData.Combat.GetSkill(t.Id);
            _tipName = StripTags(info?.Name);
            _tipDesc = StripTags(info?.Description);
        }
        else
        {
            var info = _services.GameData.Combat.GetBuff(t.Id);
            _tipName = StripTags(info?.Name);
            _tipDesc = StripTags(info?.Description);
        }
        _tipTex      = TileIcon(idx);
        _tipUv       = _uv[idx];
        _tipShownFor = idx;
        ShowTipAtCursor();
    }

    // Called when a settings list icon is left-clicked (Skills / Debuffs / Buffs tabs).
    internal void OnSettingIconClick(TileKind kind, int idx)
    {
        if (kind == TileKind.Cooldown)
        {
            int fi = _stOffset + idx;
            if (fi >= _stFiltCount) return;
            var info = _services.GameData.Combat.GetSkill(_stFiltIds[fi]);
            _tipName = StripTags(info?.Name);
            _tipDesc = StripTags(info?.Description);
            _tipTex  = StIcon(idx);
            _tipUv   = _stUv[idx];
        }
        else if (kind == TileKind.Debuff)
        {
            int fi = _dtOffset + idx;
            if (fi >= _dtFiltCount) return;
            var info = _services.GameData.Combat.GetBuff(_dtFiltIds[fi]);
            _tipName = StripTags(info?.Name);
            _tipDesc = StripTags(info?.Description);
            _tipTex  = DtIcon(idx);
            _tipUv   = _dtUv[idx];
        }
        else
        {
            int fi = _btOffset + idx;
            if (fi >= _btFiltCount) return;
            var info = _services.GameData.Combat.GetBuff(_btFiltIds[fi]);
            _tipName = StripTags(info?.Name);
            _tipDesc = StripTags(info?.Description);
            _tipTex  = BtIcon(idx);
            _tipUv   = _btUv[idx];
        }
        _tipShownFor = -1;
        ShowTipAtCursor();
    }
}
