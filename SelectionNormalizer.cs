using System.Collections.Generic;

namespace Stellar.CooldownBar;

/// <summary>
/// The stale-saved-id conversion (user chose "convert", 2026-11-13), Unity-free so it is unit-tested. A saved tracked id
/// is normalised to the id the settings list actually SHOWS, so the ticked counts equal the ticks visible in the lists.
/// The ONLY test is membership in what the list shows — never "the game data resolves a name for it": the first rule
/// (v1) asked the game's skill lookup, which answers with a name for sub-ids such as 392301 too, so the stale id was
/// "kept" and the Skills count read 1 instead of 2 (in game 2026-11-13).
/// <list type="bullet">
/// <item>Skills: kept when the list shows the id; else converted to id/100 when the list shows THAT (392301 → 3923
/// "Arcane! Furious Hammer"); else dropped.</item>
/// <item>Debuffs / buffs: a member id of a displayed name GROUP ticks the whole group (the list reads a group as ticked
/// only when every member is), so a partially ticked group from an old per-id config becomes one ticked row. An id in
/// no group is dropped.</item>
/// </list>
/// Results are de-duplicated (sets). The caller runs this only once the lists are loaded, so "not in the list" is never
/// "list not built yet".
/// </summary>
internal static class SelectionNormalizer
{
    // `listIds` = every id the Skills list shows (Plugin.Settings.Tables.cs _stIds: the SkillTable rows + bar-seen skills).
    public static HashSet<int> Skills(IEnumerable<int> saved, ISet<int> listIds, ref int converted, ref int dropped)
    {
        var result = new HashSet<int>();
        foreach (int id in saved)
        {
            if (listIds.Contains(id)) { result.Add(id); continue; }
            int b = id / 100;
            if (b > 0 && listIds.Contains(b)) { result.Add(b); converted++; }
            else dropped++;
        }
        return result;
    }

    // `members[g]` = every id of display group g (the settings list's grouping, Plugin.Settings.Tables.cs).
    public static HashSet<int> Groups(IReadOnlyCollection<int> saved, int[][] members, int count,
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
            else dropped++;
        }
        return result;
    }
}
