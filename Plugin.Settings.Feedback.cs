using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

/// <summary>
/// Picker feedback (house rule 13 — every click answers where you clicked) and the footer (TargetLens
/// Plugin.SelectWindow.Feedback.cs port). A row click flashes that row green with bold "Added" / "Removed" for
/// <see cref="SelFlashDurS"/> — keyed by the item's identity (kind + representative id) + time, so a scroll, a re-filter
/// or a re-bind keeps the flash on the right row — and puts "✓ Added “x” · N ticked" in the footer for
/// <see cref="SelFooterNoteDurS"/> (otherwise "N ticked"). The footer's Caution "Untick all" pill (recessed while
/// nothing is ticked) swaps the footer for an inline confirm: bold "Untick all N skills?" + a muted consequence line
/// (worded per mode) + [Untick all] [Cancel]; unanswered, it falls back after <see cref="SelConfirmDurS"/>. The confirm
/// is taller than the footer: the flexible list above gives up the difference, so the window never changes height.
/// </summary>
public sealed partial class Plugin
{
    private const float SelFlashDurS      = 1.0f;
    private const float SelFooterNoteDurS = 3.0f;
    private const float SelConfirmDurS    = 4.0f;
    // Pill label boxes = the longest label in any shipped locale (bold 13; id "Hapus centang" ≈ 92, fil "Kanselahin"
    // ≈ 69, ja "キャンセル" ≈ 65) + headroom, so a language switch never resizes a pill (house rule 10).
    private const float UntickPillLabelW = 96f;
    private const float SelCancelLabelW  = 72f;
    // Confirm block: bold question (one line, pinned 18) + consequence (≤ 2 plain lines, pinned 36) + the 28 px pill
    // row, 4 px apart = 90.
    private const float ConfirmQuestionH    = 18f;
    private const float ConfirmConsequenceH = 36f;

    private int    _selFlashTab = -1, _selFlashId;
    private float  _selFlashAt;
    private bool   _selFlashAdded;
    private bool   _selFlashFull;                   // Placed mode was full: amber flash "30 max" + amber footer
    private string _selFlashFullLabel = string.Empty;
    private string _selFooterNote = string.Empty;   // composed once per click (not per refresh)
    private float  _selFooterNoteAt = float.NegativeInfinity;
    private float  _untickConfirmAt = -1f;          // ≥ 0 while the inline "Untick all" confirm is showing

    private bool UntickConfirming => _untickConfirmAt >= 0f;

    private readonly FmtCache _selFooterFmt  = new();
    private readonly FmtCache _selConfirmFmt = new();

    // ── footer ────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildSelectFooter() => new ConditionalElement(() => UntickConfirming,
        new ColumnElement(new HudElement[]
        {
            new TextElement(UntickQuestion, Color: () => TextColor(), NoWrap: true, FontSize: 13)
                { Bold = true, Ellipsize = true, Height = ConfirmQuestionH },
            new TextElement(UntickConsequence, Color: () => MutedText()) { MaxLines = 2, Height = ConfirmConsequenceH },
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f),   // flexible: the pills sit at the right edge
                CautionButton(() => _loc.T("cd.sel.untickAll"), ConfirmUntickAll, UntickPillLabelW),
                NeutralButton(() => _loc.T("cd.sel.cancel"), CancelUntickAll, SelCancelLabelW),
            }, Gap: 8f),
        }, Gap: 4f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(SelFooterText, Color: () => !SelFooterNoteShowing() ? MutedText() : _selFlashFull ? AmberText() : SuccessColor(),
                NoWrap: true) { Ellipsize = true }, Weight: 1f),
            CautionButton(() => _loc.T("cd.sel.untickAll"), BeginUntickAll, UntickPillLabelW,
                enabled: () => SelActiveTicked > 0),
        }, Gap: 8f))
    { Prebuild = true, PrebuildName = "cd.settings.confirm" };

    private bool SelFooterNoteShowing() => _services.Framework.TimeNow - _selFooterNoteAt < SelFooterNoteDurS;

    private string SelFooterText() => SelFooterNoteShowing()
        ? _selFooterNote
        : SelActiveTicked < 0 ? PendingText : _selFooterFmt.Get(_loc.T("cd.sel.footer"), SelActiveTicked);

    // "Untick all 4 skills?" / "Untick 1 skill?" (a ".one" key for exactly one, like the header's count phrases).
    private string UntickQuestion()
    {
        int n = SelActiveTicked;
        string key = _activeTab == 0 ? (n == 1 ? "cd.sel.confirm.skills.one"  : "cd.sel.confirm.skills")
                   : _activeTab == 1 ? (n == 1 ? "cd.sel.confirm.debuffs.one" : "cd.sel.confirm.debuffs")
                   :                   (n == 1 ? "cd.sel.confirm.buffs.one"   : "cd.sel.confirm.buffs");
        return _selConfirmFmt.Get(_loc.T(key), n);
    }

    // What the bar does once nothing is ticked depends on the mode: "Only ticked" → it shows none of this kind;
    // "All except ticked" → nothing is hidden any more.
    private string UntickConsequence() => _loc.T(SelExcludeMode
        ? (_activeTab == 0 ? "cd.sel.consequence.exceptSkills" : _activeTab == 1 ? "cd.sel.consequence.exceptDebuffs" : "cd.sel.consequence.exceptBuffs")
        : (_activeTab == 0 ? "cd.sel.consequence.onlySkills"   : _activeTab == 1 ? "cd.sel.consequence.onlyDebuffs"   : "cd.sel.consequence.onlyBuffs"));

    // ── row flash ─────────────────────────────────────────────────────────────────────────────────────────────────
    private bool SlotFlashing(int slot)
    {
        if (_selFlashTab != _activeTab || _services.Framework.TimeNow - _selFlashAt >= SelFlashDurS) return false;
        int i = SelOffset + slot;
        return i >= 0 && i < SelFiltCount && SelFiltIds[i] == _selFlashId;
    }

    private string SelFlashLabel() => _selFlashFull ? _selFlashFullLabel : _loc.T(_selFlashAdded ? "cd.sel.added" : "cd.sel.removed");

    // Green flash, or amber when the tick could not be placed (Placed mode holds MaxPlaced).
    private ColorRgba SelFlashColor() => _selFlashFull ? Menu.Caution : SuccessColor();

    // The green (amber) word sits on the green- (amber-) washed tile; keep it readable on the Light theme too.
    private ColorRgba SelFlashTextColor()
        => EnsureContrast(SelFlashColor(), Over(SelFlashColor() with { A = FlashWashAlpha }, TileColor()), SolidMinContrast);

    // ── events ────────────────────────────────────────────────────────────────────────────────────────────────────
    private void OnEffectToggled(int tab, int id, string name, bool added, bool full = false)
    {
        var now = _services.Framework.TimeNow;
        _selFlashTab = tab; _selFlashId = id;
        _selFlashAt = now;
        _selFlashAdded = added;
        _selFlashFull = full;
        if (full) _selFlashFullLabel = _loc.TFormat("cd.sel.max", MaxPlaced);
        _selFooterNote = full
            ? _loc.TFormat("cd.placed.full", MaxPlaced, name)   // "Placed mode holds up to 30 effects. Untick one to place “X”."
            : _loc.TFormat(added ? "cd.sel.feedback.added" : "cd.sel.feedback.removed", name, SelActiveTicked);
        _selFooterNoteAt = now;
        _untickConfirmAt = -1f;   // any other action dismisses a pending confirm
    }

    private void BeginUntickAll()
    {
        if (SelActiveTicked <= 0) return;
        _untickConfirmAt = _services.Framework.TimeNow;
    }

    private void CancelUntickAll() => _untickConfirmAt = -1f;

    // Clears the ACTIVE kind's whole selection (every id, so a partially ticked group is cleared too) and persists it
    // like a row toggle does. Re-applying the filter re-sorts the list (nothing floats as ticked any more) and bumps
    // _selVersion, so the counts and row caches recompute.
    private void ConfirmUntickAll()
    {
        _untickConfirmAt = -1f;
        int tab = _activeTab;
        if (tab == 0)      _selection.ClearSkills();
        else if (tab == 1) _selection.ClearDebuffs();   // also opts each one out of imagine auto-track, like a row untick
        else               _selection.ClearBuffs();
        _selection.Save(_cfg);
        OnTrackedChanged();   // Plugin.Placed.cs
        if (tab == 0)      ApplySkillTabFilter(_stFilter);
        else if (tab == 1) ApplyDebuffTabFilter(_dtFilter);
        else               ApplyBuffTabFilter(_btFilter);
        _selFlashTab = -1;
        _selFlashFull = false;
        _selFooterNote = _loc.T(tab == 0 ? "cd.sel.feedback.clearedSkills" : tab == 1 ? "cd.sel.feedback.clearedDebuffs"
                                                                          : "cd.sel.feedback.clearedBuffs");
        _selFooterNoteAt = _services.Framework.TimeNow;
    }

    // Per tick (TickSettingsUi): an unanswered confirm falls back to the normal footer. One compare when idle.
    private void AdvanceUntickConfirm()
    {
        if (UntickConfirming && _services.Framework.TimeNow - _untickConfirmAt > SelConfirmDurS) _untickConfirmAt = -1f;
    }
}
