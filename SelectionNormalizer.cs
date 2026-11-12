using System.Collections.Generic;

namespace Stellar.CooldownBar;

/// <summary>
/// The stale-saved-id conversion (user chose "convert", 2026-11-13), Unity-free so it is unit-tested. A saved tracked id
/// is normalised to the id the settings list actually SHOWS, so the ticked counts equal the ticks visible in the lists.
/// The ONLY test is membership in what the list shows — never "the game data resolves a name for it": the first rule
/// (v1) asked the game's skill lookup, which answers with a name for sub-ids such as 392301 too, so the stale id was
/// "kept" and the Skills count read 1 instead of 2 (in game 2026-11-13).
/// <list type="bullet">
/// <item>Skills (<see cref="MapSkill"/>): kept when the list shows the id; else converted to id/100 when the list shows
/// THAT (392301 → 3923 "Arcane! Furious Hammer"); else the same against every SkillTable row (hidden ones); else dropped.</item>
/// <item>Debuffs / buffs: a member id of a displayed name GROUP ticks the whole group (the list reads a group as ticked
/// only when every member is), so a partially ticked group from an old per-id config becomes one ticked row. An id in
/// no listed group is kept while it is any BuffTable row (hidden effects), else dropped.</item>
/// </list>
/// The "Hidden effects" display filter never drops a tick: only ids that exist nowhere in the game tables go.
/// Results are de-duplicated (sets). The caller runs this only once the tables are loaded, so "exists nowhere" is never
/// "table not built yet".
/// </summary>
internal static class SelectionNormalizer
{
    /// <summary>
    /// The ONE skill-id mapping, shared by this conversion and the bar's live cooldown → skill resolution
    /// (Plugin.Seen.cs), so a live cooldown reported as 392301 lands on the 3923 tick. In order: the id the list shows;
    /// its id/100 the list shows; else the same two against EVERY SkillTable row (hidden ones included — a hidden skill
    /// is still a real skill, shown once "Hidden effects" is on or once it fired); else 0 (exists nowhere).
    /// </summary>
    /// <param name="listed">The rows the Skills list shows right now (the table filtered by "Hidden effects").</param>
    /// <param name="all">Every SkillTable row id, unfiltered.</param>
    public static int MapSkill(int id, ISet<int> listed, ISet<int> all)
    {
        int b = id / 100;
        if (listed.Contains(id)) return id;
        if (b > 0 && listed.Contains(b)) return b;
        if (all.Contains(id)) return id;
        if (b > 0 && all.Contains(b)) return b;
        return 0;
    }

    public static HashSet<int> Skills(IEnumerable<int> saved, ISet<int> listed, ISet<int> all, ref int converted, ref int dropped)
    {
        var result = new HashSet<int>();
        foreach (int id in saved)
        {
            int m = MapSkill(id, listed, all);
            if (m == 0) { dropped++; continue; }
            if (m != id) converted++;
            result.Add(m);
        }
        return result;
    }

    // `members[g]` = every id of display group g (the settings list's grouping, Plugin.Settings.Tables.cs). `all` = every
    // BuffTable row id, unfiltered: an id in no listed group (e.g. a hidden effect while "Hidden effects" is off) is KEPT
    // when it is still a real row — a display filter never drops a saved tick; only ids that exist nowhere are dropped.
    public static HashSet<int> Groups(IReadOnlyCollection<int> saved, int[][] members, int count, ISet<int> all,
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
            else if (all.Contains(id)) result.Add(id);
            else dropped++;
        }
        return result;
    }
}
