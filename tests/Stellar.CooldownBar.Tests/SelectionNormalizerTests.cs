using System.Collections.Generic;
using Stellar.CooldownBar;
using Xunit;

namespace Stellar.CooldownBar.Tests;

// The stale saved-id conversion (user chose "convert", 2026-11-13): each saved id becomes the id the settings list shows.
public class SelectionNormalizerTests
{
    // Named SkillTable rows in this fake: 3923 ("Arcane! Furious Hammer") and 2233. 392301 / 555 exist without a name.
    private static readonly HashSet<int> Named = new() { 3923, 2233 };
    private static readonly HashSet<int> Rows  = new() { 3923, 2233, 392301, 555 };

    // Same rule as Plugin.ResolveNamedBaseSkill.
    private static int Resolve(int id) => Named.Contains(id) ? id : Named.Contains(id / 100) ? id / 100 : 0;

    [Fact]
    public void Skills_MapToNamedBase_KeepUnnamedRows_DropUnknown_Dedupe()
    {
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Skills(new[] { 392301, 2233, 3923, 555, 999999 }, Resolve, Rows.Contains, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 3923, 2233, 555 }, r);   // 392301 → 3923 (dedup with the saved 3923)
        Assert.Equal(1, conv);
        Assert.Equal(1, drop);                                    // 999999: no row at all
    }

    [Fact]
    public void Skills_UserConfig_392301_And_2233()
    {
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Skills(new[] { 392301, 2233 }, Resolve, Rows.Contains, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 3923, 2233 }, r);
        Assert.Equal((1, 0), (conv, drop));
    }

    [Fact]
    public void Groups_CompletePartialGroup_KeepUngroupedRows_DropUnknown()
    {
        var members = new[] { new[] { 10, 11, 12 }, new[] { 20 } };
        var rows = new HashSet<int> { 10, 11, 12, 20, 30 };
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Groups(new[] { 11, 20, 30, 40 }, members, 2, rows.Contains, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 10, 11, 12, 20, 30 }, r);   // 30: a real row in no group (hidden) → kept
        Assert.Equal(2, conv);                                        // 10 and 12 added to complete group 0
        Assert.Equal(1, drop);                                        // 40: no row
    }

    [Fact]
    public void Groups_AlreadyNormal_IsUnchanged()
    {
        var members = new[] { new[] { 10, 11 } };
        int conv = 0, drop = 0;
        var r = SelectionNormalizer.Groups(new[] { 10, 11 }, members, 1, _ => true, ref conv, ref drop);
        Assert.Equal(new HashSet<int> { 10, 11 }, r);
        Assert.Equal((0, 0), (conv, drop));
    }
}
