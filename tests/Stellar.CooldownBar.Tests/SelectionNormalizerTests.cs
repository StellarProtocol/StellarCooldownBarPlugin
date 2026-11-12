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

    [Fact]
    public void Skills_UserConfig_392301_And_2233()
    {
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Skills(new[] { 392301, 2233 }, List, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 3923, 2233 }, r);
        Assert.Equal((1, 0), (conv, drop));
    }

    [Fact]
    public void Skills_KeepListed_ConvertSubId_DropUnlisted_Dedupe()
    {
        int conv = 0, drop = 0;
        // 555: not listed, and 5 isn't either → dropped. 999999: 9999 not listed → dropped.
        var r = SelectionNormalizer.Skills(new[] { 392301, 2233, 3923, 555, 999999 }, List, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 3923, 2233 }, r);   // 392301 → 3923 (dedup with the saved 3923)
        Assert.Equal(1, conv);
        Assert.Equal(2, drop);
    }

    [Fact]
    public void Skills_AlreadyNormal_IsUnchanged()
    {
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Skills(new[] { 3923, 2233 }, List, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 3923, 2233 }, r);
        Assert.Equal((0, 0), (conv, drop));
    }

    [Fact]
    public void Groups_CompletePartialGroup_DropUngrouped()
    {
        var members = new[] { new[] { 10, 11, 12 }, new[] { 20 } };
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Groups(new[] { 11, 20, 30, 40 }, members, 2, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 10, 11, 12, 20 }, r);
        Assert.Equal(2, conv);   // 10 and 12 added to complete group 0
        Assert.Equal(2, drop);   // 30, 40: in no group the list shows
    }

    [Fact]
    public void Groups_AlreadyNormal_IsUnchanged()
    {
        var members = new[] { new[] { 10, 11 } };
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Groups(new[] { 10, 11 }, members, 1, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 10, 11 }, r);
        Assert.Equal((0, 0), (conv, drop));
    }
}
