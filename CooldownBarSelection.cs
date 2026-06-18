using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

/// <summary>
/// Persisted user selection for the CooldownBar: which skill cooldowns and which debuffs to show, plus an
/// imagine opt-out set so auto-tracking an Imagine-lockout debuff never overrides a user who un-ticked it.
/// Unity-free — unit-testable. Stored as int[] arrays in the "cooldownbar" config section.
/// </summary>
internal sealed class CooldownBarSelection
{
    private readonly HashSet<int> _skills = new();
    private readonly HashSet<int> _debuffs = new();
    private readonly HashSet<int> _imagineOptOut = new();

    /// <summary>Returns true when the skill cooldown with <paramref name="skillId"/> is in the tracked set.</summary>
    public bool IsCooldownTracked(int skillId) => _skills.Contains(skillId);

    /// <summary>Returns true when the debuff with <paramref name="baseId"/> is in the tracked set.</summary>
    public bool IsDebuffTracked(int baseId) => _debuffs.Contains(baseId);

    /// <summary>Adds or removes <paramref name="skillId"/> from the tracked-cooldowns set.</summary>
    public void SetCooldown(int skillId, bool on)
    {
        if (on) _skills.Add(skillId); else _skills.Remove(skillId);
    }

    /// <summary>
    /// Adds or removes <paramref name="baseId"/> from the tracked-debuffs set.
    /// Removing records it in the imagine opt-out set; re-adding clears it.
    /// </summary>
    public void SetDebuff(int baseId, bool on)
    {
        if (on) { _debuffs.Add(baseId); _imagineOptOut.Remove(baseId); }
        else    { _debuffs.Remove(baseId); _imagineOptOut.Add(baseId); }
    }

    /// <summary>True when an imagine-lockout debuff should be auto-tracked (not already tracked, not opted out).</summary>
    public bool ShouldAutoTrackImagine(int baseId) =>
        !_debuffs.Contains(baseId) && !_imagineOptOut.Contains(baseId);

    /// <summary>Auto-track an imagine-lockout debuff on first sight (no-op when already tracked or opted out).</summary>
    public void AutoTrackImagine(int baseId)
    {
        if (ShouldAutoTrackImagine(baseId)) _debuffs.Add(baseId);
    }

    /// <summary>Populates a new <see cref="CooldownBarSelection"/> from a persisted config section.</summary>
    public static CooldownBarSelection Load(IConfigSection cfg)
    {
        var sel = new CooldownBarSelection();
        foreach (var id in cfg.Get("track.skills",         Array.Empty<int>()) ?? Array.Empty<int>()) sel._skills.Add(id);
        foreach (var id in cfg.Get("track.debuffs",        Array.Empty<int>()) ?? Array.Empty<int>()) sel._debuffs.Add(id);
        foreach (var id in cfg.Get("track.imagineOptOut",  Array.Empty<int>()) ?? Array.Empty<int>()) sel._imagineOptOut.Add(id);
        return sel;
    }

    /// <summary>Persists all three sets to <paramref name="cfg"/> and calls <see cref="IConfigSection.Save"/>.</summary>
    public void Save(IConfigSection cfg)
    {
        cfg.Set("track.skills",         _skills.ToArray());
        cfg.Set("track.debuffs",        _debuffs.ToArray());
        cfg.Set("track.imagineOptOut",  _imagineOptOut.ToArray());
        cfg.Save();
    }
}
