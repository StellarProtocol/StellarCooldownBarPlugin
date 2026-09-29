using Xunit;

namespace Stellar.CooldownBar.Tests;

/// <summary>
/// Imagine-lockout debuffs must draw the Battle-Imagine CARD the debuff locks out, exactly as CombatMeter does.
/// Owner report 2026-09-29: CooldownBar 2.2.3 drew Mechanical Failure with the generic buff_abnormal icon while
/// CombatMeter drew the "Arcane! Superconductor Surge" card.
/// </summary>
public class LockoutIconTests
{
    private const int MechanicalFailure = 2110049;
    private const int ElementStasis     = 2110050;
    private const int TimeStasis        = 2110056;
    private const int Tetanus           = 2110069;   // ordinary (non-lockout) debuff
    private const int SuperconductorSurgeCard  = 3971;
    private const int SuperconductorSurgeSummon = 111069;   // same-named slot-[0] variant, not an imagine
    private const int FatalSpiralCard = 3957;
    private const int FatalSpiralSummon = 1100740;
    private const int TimeDecreeSummon = 2900340;           // slot-[7,8] variant: GetImagineForSkill maps it

    private static readonly GameTables G = GameTables.Load;

    [Fact]
    public void Fixture_names_the_arcane_ids_the_map_points_at()
    {
        Assert.Equal("Mechanical Failure", G.BuffNames[MechanicalFailure]);
        Assert.Equal("Arcane! Superconductor Surge", G.SkillNames[SuperconductorSurgeCard]);
        Assert.Equal("Element Stasis", G.BuffNames[ElementStasis]);
        Assert.Equal("Arcane! Fatal Spiral", G.SkillNames[FatalSpiralCard]);
        Assert.Equal(0, G.BuffSkillId(MechanicalFailure));   // why a curated map is needed at all
        Assert.Equal(0, G.BuffSkillId(ElementStasis));
    }

    [Theory]
    [InlineData(0)]                           // no live source
    [InlineData(SuperconductorSurgeSummon)]   // live source = the unmappable slot-[0] summon variant
    public void Mechanical_failure_tile_draws_the_superconductor_surge_card(int liveSource)
    {
        var cls = G.NewAttribution().Classify(MechanicalFailure, liveSource);
        Assert.True(cls.IsImagine);
        Assert.Equal(SuperconductorSurgeCard, cls.ImagineSkillId);
        Assert.Equal(SuperconductorSurgeCard, DebuffAttribution.TileIconSkill(cls, liveSource));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(FatalSpiralSummon)]
    public void Element_stasis_tile_draws_the_fatal_spiral_card(int liveSource)
    {
        var cls = G.NewAttribution().Classify(ElementStasis, liveSource);
        Assert.True(cls.IsImagine);
        Assert.Equal(FatalSpiralCard, DebuffAttribution.TileIconSkill(cls, liveSource));
    }

    [Fact]
    public void Live_imagine_source_still_resolves_without_the_map()
    {
        // Time Stasis is not in the curated map: its live source (Tina's 2900340) is itself a slot-[7,8] imagine.
        var cls = G.NewAttribution().Classify(TimeStasis, TimeDecreeSummon);
        Assert.True(cls.IsImagine);
        Assert.Equal(TimeDecreeSummon, DebuffAttribution.TileIconSkill(cls, TimeDecreeSummon));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(123456)]   // an arbitrary non-imagine source skill
    public void Non_lockout_debuff_keeps_its_own_icon_path(int liveSource)
    {
        var cls = G.NewAttribution().Classify(Tetanus, liveSource);
        Assert.False(cls.IsImagine);
        Assert.Equal(0, cls.ImagineSkillId);
        // The tile keeps the raw source skill (0 -> the render layer's LoadBuffIcon of the debuff itself).
        Assert.Equal(liveSource, DebuffAttribution.TileIconSkill(cls, liveSource));
    }

    [Fact]
    public void Memo_is_keyed_on_the_source_too()
    {
        var attr = G.NewAttribution();
        Assert.False(attr.Classify(TimeStasis, 0).IsImagine);
        Assert.True(attr.Classify(TimeStasis, TimeDecreeSummon).IsImagine);
    }
}
