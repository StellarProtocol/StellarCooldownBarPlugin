using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.CooldownBar.Tests;

// Owner report 2026-11-20: Season-4 Potion/Cuisine buffs never showed on the bar ("0 active"). The saved selection held
// only the Season-3 ids of the groups the owner ticked; Season 4 added 54 Potion + 24 Cuisine ids. A whole ticked name
// group now keeps every member the game adds later — including for configs saved before this existed.
public class SeasonGroupTrackingTests
{
    private sealed class FakeConfig : IConfigSection
    {
        public readonly Dictionary<string, object?> Store = new();
        public T? Get<T>(string key, T? defaultValue) => Store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => Store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
        public void RemoveByPrefix(string prefix) { }
    }

    private const int S4Potion = 2033211, S4Cuisine = 2032311, Other = 2099001;

    // Raw game names in the client's own language (here the build's Chinese strings).
    private static string Raw(int id) => id == Other ? "加速" : id is >= 2033000 and < 2034000 ? "药剂" : "料理";

    private static bool Tracked(CooldownBarSelection s, int id) => s.IsBuffTracked(id) || s.IsBuffGroupTracked(Raw(id), Raw);

    private static int[] Concat(int[] a, int[] b) { var r = new int[a.Length + b.Length]; a.CopyTo(r, 0); b.CopyTo(r, a.Length); return r; }

    [Fact]
    public void A_saved_season3_selection_holding_both_whole_groups_now_tracks_season4_members()
    {
        var cfg = new FakeConfig();
        cfg.Store["track.buffs"] = Concat(KnownSeasonGroups.PotionS3, KnownSeasonGroups.CuisineS3);
        var sel = CooldownBarSelection.Load(cfg);
        Assert.True(Tracked(sel, S4Potion));
        Assert.True(Tracked(sel, S4Cuisine));
        Assert.False(Tracked(sel, Other));
    }

    [Fact]
    public void A_partial_selection_is_left_exactly_as_saved()
    {
        var cfg = new FakeConfig();
        cfg.Store["track.buffs"] = new[] { KnownSeasonGroups.PotionS3[0] };
        var sel = CooldownBarSelection.Load(cfg);
        Assert.False(Tracked(sel, S4Potion));
        Assert.Empty(sel.BuffGroupReps);
    }

    [Fact]
    public void Ticking_a_group_follows_new_members_and_survives_save_load()
    {
        var cfg = new FakeConfig();
        var sel = CooldownBarSelection.Load(cfg);
        sel.SetBuff(KnownSeasonGroups.CuisineRep, true);
        sel.SetBuffGroup(KnownSeasonGroups.CuisineRep, Raw(KnownSeasonGroups.CuisineRep), true, Raw);
        Assert.True(Tracked(sel, S4Cuisine));
        sel.Save(cfg);
        var again = CooldownBarSelection.Load(cfg);
        Assert.True(Tracked(again, S4Cuisine));
        again.SetBuffGroup(S4Cuisine, Raw(S4Cuisine), false, Raw);       // untick through another member
        again.SetBuff(KnownSeasonGroups.CuisineRep, false);
        Assert.False(Tracked(again, S4Cuisine));
    }

    [Fact]
    public void The_known_season3_memberships_are_the_full_lists()
    {
        Assert.Equal(156, KnownSeasonGroups.PotionS3.Length);
        Assert.Equal(136, KnownSeasonGroups.CuisineS3.Length);
        Assert.Contains(KnownSeasonGroups.PotionRep, KnownSeasonGroups.PotionS3);
        Assert.Contains(KnownSeasonGroups.CuisineRep, KnownSeasonGroups.CuisineS3);
    }
}
