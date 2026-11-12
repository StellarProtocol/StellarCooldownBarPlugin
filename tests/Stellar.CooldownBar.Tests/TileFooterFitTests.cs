using System;
using Stellar.CooldownBar;
using Xunit;

namespace Stellar.CooldownBar.Tests;

// The tile footer with a count chip must fit its cell with ≥ 2 px to spare on each side for the worst case, at the bar
// (100 %) and every Placed size. Inputs mirror Plugin.Bar.Tile.cs TileGeom: InnerW = round(44·s), ChipH = round(14·s).
public class TileFooterFitTests
{
    private static float Px(float v) => MathF.Round(v, MidpointRounding.AwayFromZero);

    // Advance widths in Noto Sans Bold (the framework's StellarUIThaiBold.ttf), measured with fontTools, in em.
    private static readonly (string Label, float Em)[] SecondsLabels =
        { ("9.8s", 1.926f), ("57s", 1.641f), ("16m", 2.126f), ("99m", 2.126f), ("2h", 1.229f), ("23h", 1.801f) };

    [Theory]
    [InlineData(0.75f)] [InlineData(1.00f)] [InlineData(1.25f)] [InlineData(1.50f)] [InlineData(2.00f)]
    public void WorstCase_FitsWithMargin(float s)
    {
        float inner = Px(44f * s), chipH = Px(14f * s);
        var f = new TileFooterFit(s, inner, chipH);

        Assert.True(f.Margin >= 2f);
        Assert.Equal(inner - 2f * f.Margin, f.SecsW + f.Gap + f.ChipW);           // the centred pair leaves Margin each side
        foreach (var (_, em) in SecondsLabels) Assert.True(em * f.SecsFont <= f.SecsW);
        Assert.True(2f * TileFooterFit.DigitEm * f.ChipFont2 <= f.ChipLabelW);   // "99"
        Assert.True(3f * TileFooterFit.DigitEm * f.ChipFont3 <= f.ChipLabelW);   // "99+"
        Assert.True(f.ChipW >= chipH);                                           // never narrower than a circle
        Assert.True(f.SecsFont >= 7 && f.ChipFont3 >= 6);
    }

    [Theory]
    [InlineData(1, "1")] [InlineData(9, "9")] [InlineData(10, "10")] [InlineData(99, "99")]
    [InlineData(100, "99+")] [InlineData(12345, "99+")]
    public void ChipText_ExactTo99_Then99Plus(int n, string expected) => Assert.Equal(expected, TileFooterFit.ChipText(n));
}
