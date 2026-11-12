using System.Collections.Generic;
using Stellar.CooldownBar;
using Xunit;

namespace Stellar.CooldownBar.Tests;

// The stale saved-id conversion (user chose "convert", 2026-11-13): each saved id becomes the id the settings list shows.
// Membership in the list is the only test (v2) — the game data's opinion of an id never keeps it.
public class SelectionNormalizerTests
{
    // The Skills list shows 3923 ("Arcane! Furious Hammer") and 2233. 392301 is a sub-id of 3923 that the list never shows.
    private static readonly HashSet<int> List = new() { 3923, 2233 };
    // Every SkillTable row: the listed ones + 7001, a hidden skill (no name/icon → not listed while Hidden effects is off).
    private static readonly HashSet<int> All  = new() { 3923, 2233, 7001 };

    // The bar's live cooldown → skill mapping is the same function: a Furious Hammer CD reported as 392301 lands on 3923.
    [Fact]
    public void MapSkill_LiveCdSubId_MapsToListedBase()
    {
        Assert.Equal(3923, SelectionNormalizer.MapSkill(392301, List, All));
        Assert.Equal(3923, SelectionNormalizer.MapSkill(3923, List, All));
        Assert.Equal(7001, SelectionNormalizer.MapSkill(7001, List, All));     // hidden row: itself
        Assert.Equal(7001, SelectionNormalizer.MapSkill(700105, List, All));   // sub-id of a hidden row
        Assert.Equal(0, SelectionNormalizer.MapSkill(123456, List, All));      // exists nowhere
    }

    [Fact]
    public void Skills_UserConfig_392301_And_2233()
    {
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Skills(new[] { 392301, 2233 }, List, All, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 3923, 2233 }, r);
        Assert.Equal((1, 0), (conv, drop));
    }

    [Fact]
    public void Skills_KeepListed_ConvertSubId_DropUnlisted_Dedupe()
    {
        int conv = 0, drop = 0;
        // 555: not listed, and 5 isn't either → dropped. 999999: 9999 not listed → dropped.
        var r = SelectionNormalizer.Skills(new[] { 392301, 2233, 3923, 555, 999999 }, List, All, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 3923, 2233 }, r);   // 392301 → 3923 (dedup with the saved 3923)
        Assert.Equal(1, conv);
        Assert.Equal(2, drop);
    }

    [Fact]
    public void Skills_AlreadyNormal_IsUnchanged()
    {
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Skills(new[] { 3923, 2233 }, List, All, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 3923, 2233 }, r);
        Assert.Equal((0, 0), (conv, drop));
    }

    // Every BuffTable row in the group tests: the listed groups' members + 30, a hidden effect (no name/icon → in no
    // listed group while "Hidden effects" is off). 40 exists nowhere.
    private static readonly HashSet<int> BuffRows = new() { 10, 11, 12, 20, 30 };

    [Fact]
    public void Groups_CompletePartialGroup_KeepHiddenRow_DropUnknown()
    {
        var members = new[] { new[] { 10, 11, 12 }, new[] { 20 } };
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Groups(new[] { 11, 20, 30, 40 }, members, 2, BuffRows, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 10, 11, 12, 20, 30 }, r);
        Assert.Equal(2, conv);   // 10 and 12 added to complete group 0
        Assert.Equal(1, drop);   // 40: exists nowhere
    }

    // "Hidden effects" off: the hidden effect's tick is in no listed group, but it is a real row → it stays.
    [Fact]
    public void Groups_HiddenEffect_StaysWhenHiddenFilterOff()
    {
        var listedGroups = new[] { new[] { 10, 11, 12 } };   // the hidden 30 is filtered out of the list
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Groups(new[] { 30 }, listedGroups, 1, BuffRows, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 30 }, r);
        Assert.Equal((0, 0), (conv, drop));
    }

    // Skills: a hidden SkillTable row stays too (not listed while "Hidden effects" is off).
    [Fact]
    public void Skills_HiddenSkill_StaysWhenHiddenFilterOff()
    {
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Skills(new[] { 7001, 2233 }, List, All, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 7001, 2233 }, r);
        Assert.Equal((0, 0), (conv, drop));
    }

    [Fact]
    public void Groups_AlreadyNormal_IsUnchanged()
    {
        var members = new[] { new[] { 10, 11 } };
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Groups(new[] { 10, 11 }, members, 1, BuffRows, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 10, 11 }, r);
        Assert.Equal((0, 0), (conv, drop));
    }
}