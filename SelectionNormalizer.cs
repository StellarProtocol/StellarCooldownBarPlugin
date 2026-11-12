using System;
using System.Collections.Generic;

namespace Stellar.CooldownBar;

/// <summary>
/// The stale-saved-id conversion (user chose "convert", 2026-11-13), Unity-free so it is unit-tested. A saved tracked id
/// is normalised to the id the settings list actually SHOWS, so the ticked counts equal the ticks visible in the lists:
/// <list type="bullet">
/// <item>Skills: the bar's own resolver (<c>ResolveNamedBaseSkill</c>: the id itself when it has a named SkillTable row,
/// else id/100 when that has one) — e.g. 392301 → 3923 "Arcane! Furious Hammer". An id with a row but no name (the
/// Show-hidden list lists these as "Skill N") is kept as it is.</item>
/// <item>Debuffs / buffs: a member id maps to its displayed name GROUP and the whole group is ticked (the list reads a
/// group as ticked only when every member is), so a partially ticked group from an old per-id config becomes one ticked
/// row. An id in no group (e.g. a hidden effect while Show hidden is off) is kept when the game still has its row.</item>
/// </list>
/// Results are de-duplicated (sets). An id that maps to NOTHING (no game row at all) is dropped — the caller runs this only
/// once the game tables are loaded, so "no row" is never "table not ready yet".
/// </summary>
internal static class SelectionNormalizer
{
    public static HashSet<int> Skills(IEnumerable<int> saved, Func<int, int> resolveNamed, Func<int, bool> rowExists,
        ref int converted, ref int dropped)
    {
        var result = new HashSet<int>();
        foreach (int id in saved)
        {
            int b = resolveNamed(id);
            if (b != 0) { if (b != id) converted++; result.Add(b); }
            else if (rowExists(id)) result.Add(id);
            else dropped++;
        }
        return result;
    }

    // `members[g]` = every id of display group g (the settings list's grouping, Plugin.Settings.Tables.cs).
    public static HashSet<int> Groups(IReadOnlyCollection<int> saved, int[][] members, int count, Func<int, bool> rowExists,
        ref int converted, ref int dropped)
    {
        var groupOf = new Dictionary<int, int>();
        for (int g = 0; g < count; g++)
            foreach (int m in members[g]) groupOf.TryAdd(m, g);

        var savedSet = new HashSet<int>(saved);
        var result = new HashSet<int>();
        foreach (int id in saved)
        {
            if (groupOf.TryGetValue(id, out int g))
            {
                foreach (int m in members[g])
                    if (result.Add(m) && !savedSet.Contains(m)) converted++;   // a group member added to complete the row
            }
            else if (rowExists(id)) result.Add(id);
            else dropped++;
        }
        return result;
    }
}
