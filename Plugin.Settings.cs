using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

// The "what to track" picker: a live list of every cooldown/debuff seen this session, grouped Skill cooldowns /
// Debuffs (imagine lockouts pinned to the top of Debuffs, ★-prefixed), each row [icon] [name] [toggle]. Toggling
// writes the config immediately; the bar reflects it on the next refresh.
public sealed partial class Plugin
{
    private const int   MaxRows = 64;
    private const float ScrollH = 380f;

    // Flattened, grouped view of the seen registry, rebuilt each refresh into a reusable buffer (no per-frame alloc).
    private readonly List<(TileKind Kind, int Id, bool IsImagine)> _rows = new(MaxRows);
    private readonly UvRect[] _rowUv = new UvRect[MaxRows];

    private IWindowControl BuildAndRegisterSettings()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "cooldownbar.settings",
                Title:       "CooldownBar — Track",
                DefaultRect: new WindowRect(900f, 120f, 320f, 460f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, HideUntilInWorld = true, Closable = true, Draggable = true },
            BuildSettingsRoot(),
            OnClose: () => _settings.SetVisible(false)));

    private HudElement BuildSettingsRoot()
    {
        var rows = new HudElement[MaxRows];
        for (var i = 0; i < MaxRows; i++)
        {
            var idx = i;
            rows[i] = new RowElement(new HudElement[]
            {
                new GameTextureElement(() => RowIcon(idx), 22, 22, () => _rowUv[idx]),
                new TextElement(() => RowLabel(idx)),
                new SpacerElement(Width: 0f),   // flexible → pushes the toggle to the right edge
                new ToggleElement(() => "", () => RowTracked(idx), v => SetRowTracked(idx, v)),
            }, Gap: 6f);
        }
        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Track what appears on the bar", Emphasis: true),
            new TextElement(() => "Cyan = cooldown · Red = debuff · ★ = Imagine lockout (auto-tracked)"),
            new SeparatorElement(),
            new ScrollElement(new ListElement(() => VisibleRows(), rows), ScrollH),
        }, Gap: 4f);
    }

    // Rebuild the grouped row buffer: cooldowns first, then debuffs (imagine lockouts pinned to the top of debuffs).
    private int VisibleRows()
    {
        _rows.Clear();
        foreach (var e in _seen.Entries)
            if (e.Kind == TileKind.Cooldown) _rows.Add((e.Kind, e.Id, false));
        foreach (var e in _seen.Entries)
            if (e.Kind == TileKind.Debuff && _attr.Classify(e.Id).IsImagine) _rows.Add((e.Kind, e.Id, true));
        foreach (var e in _seen.Entries)
            if (e.Kind == TileKind.Debuff && !_attr.Classify(e.Id).IsImagine) _rows.Add((e.Kind, e.Id, false));
        return Math.Min(_rows.Count, MaxRows);
    }

    private object? RowIcon(int i)
    {
        if (i >= _rows.Count) { _rowUv[i] = new UvRect(0f, 0f, 1f, 1f); return null; }
        var r = _rows[i];
        if (r.IsImagine)                 return _services.GameAssets.LoadImagineIcon(_attr.Classify(r.Id).ImagineSkillId, out _rowUv[i]);
        if (r.Kind == TileKind.Cooldown) return _services.GameAssets.LoadSkillIcon(r.Id, out _rowUv[i]);
        return _services.GameAssets.LoadBuffIcon(r.Id, out _rowUv[i]);
    }

    private string RowLabel(int i)
    {
        if (i >= _rows.Count) return "";
        var r = _rows[i];
        var name = r.Kind == TileKind.Cooldown
            ? (_services.GameData.Combat.GetSkill(r.Id) is { Name.Length: > 0 } s ? s.Name : $"Skill {r.Id}")
            : (_services.GameData.Combat.GetBuff(r.Id) is { Name.Length: > 0 } b ? b.Name : $"Buff {r.Id}");
        return r.IsImagine ? "★ " + name : name;
    }

    private bool RowTracked(int i)
    {
        if (i >= _rows.Count) return false;
        var r = _rows[i];
        return r.Kind == TileKind.Cooldown ? _selection.IsCooldownTracked(r.Id) : _selection.IsDebuffTracked(r.Id);
    }

    private void SetRowTracked(int i, bool on)
    {
        if (i >= _rows.Count) return;
        var r = _rows[i];
        if (r.Kind == TileKind.Cooldown) _selection.SetCooldown(r.Id, on);
        else                             _selection.SetDebuff(r.Id, on);
        _selection.Save(_cfg);   // persist immediately; bar reflects it next tick
    }
}
