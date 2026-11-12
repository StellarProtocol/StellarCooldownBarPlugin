using System;
using Stellar.CooldownBar;
using Xunit;

namespace Stellar.CooldownBar.Tests;

// The tile footer with a count chip: the chip is pinned ≥ 2 px inside the cell's right edge, and "∞" at the NORMAL
// seconds size fits its box whole (so it never touches the chip or crosses the cell's left edge), at the bar (100 %,
// legacy 14 px) and every Placed size (TMP bold 12·s). Inputs mirror Plugin.Bar.Tile.cs TileGeom: InnerW = round(44·s),
// ChipH = round(14·s), SecsFont = round(12·s).
public class TileFooterFitTests
{
    private static float Px(float v) => MathF.Round(v, MidpointRounding.AwayFromZero);

    [Theory]
    [InlineData(0.75f)] [InlineData(1.00f)] [InlineData(1.25f)] [InlineData(1.50f)] [InlineData(2.00f)]
    public void ChipPinnedInside_AndInfinityFits(float s)
    {
        float inner = Px(44f * s), chipH = Px(14f * s);
        var f = new TileFooterFit(s, inner, chipH);

        Assert.True(f.Margin >= 2f);
        Assert.Equal(inner - 2f * f.Margin, f.SecsW + f.Gap + f.ChipW);           // chip's right edge = Margin inside
        Assert.True(2f * TileFooterFit.DigitEm * f.ChipFont2 <= f.ChipLabelW);   // "99"
        Assert.True(3f * TileFooterFit.DigitEm * f.ChipFont3 <= f.ChipLabelW);   // "99+"
        Assert.True(f.ChipW >= chipH);                                           // never narrower than a circle
        Assert.True(f.ChipFont3 >= 6);

        // Normal seconds size: Placed = TMP 12·s; the bar (s = 1) = the shadowed legacy fixed 14 px.
        float secsPx = s == 1f ? 14f : Px(12f * s);
        Assert.True(TileFooterFit.InfinityMaxEm * secsPx <= f.SecsW);
    }

    [Theory]
    [InlineData(1, "1")] [InlineData(9, "9")] [InlineData(10, "10")] [InlineData(99, "99")]
    [InlineData(100, "99+")] [InlineData(12345, "99+")]
    public void ChipText_ExactTo99_Then99Plus(int n, string expected) => Assert.Equal(expected, TileFooterFit.ChipText(n));
}
