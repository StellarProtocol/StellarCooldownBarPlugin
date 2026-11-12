using Stellar.CooldownBar;
using Xunit;

namespace Stellar.CooldownBar.Tests;

// The approved tile time format (cooldownbar-redesign-APPROVED.md, "Time format"): tenths below 10 s, whole seconds
// below 100 s, floored minutes below 100 min, floored hours above; "∞" for permanent; '*' for the fallback clock.
public class CdTimeFormatTests
{
    [Theory]
    [InlineData(9.8f,    "9.8s")]
    [InlineData(9.99f,   "9.9s")]    // floors: never "10.0s" on the way down
    [InlineData(0.04f,   "0.0s")]
    [InlineData(10f,     "10s")]
    [InlineData(57.3f,   "57s")]
    [InlineData(99.9f,   "99s")]
    [InlineData(100f,    "1m")]
    [InlineData(124f,    "2m")]
    [InlineData(500f,    "8m")]
    [InlineData(999f,    "16m")]
    [InlineData(5999f,   "99m")]
    [InlineData(6000f,   "1h")]
    [InlineData(7300f,   "2h")]
    public void Formats(float secs, string expected) => Assert.Equal(expected, CdTimeFormat.Format(secs, false, false));

    [Fact]
    public void Permanent_IsInfinity_WithoutFallbackStar()
    {
        Assert.Equal("∞", CdTimeFormat.Format(0f, true, false));
        Assert.Equal("∞", CdTimeFormat.Format(0f, true, true));
    }

    [Fact]
    public void Fallback_PrefixesStar() => Assert.Equal("*57s", CdTimeFormat.Format(57.3f, false, true));

    [Fact]
    public void Key_ChangesExactlyWhenTheTextDoes()
    {
        float[] samples = { 9.95f, 9.91f, 9.89f, 57.9f, 57.1f, 56.9f, 124f, 179f, 181f, 6000f, 7199f, 7201f };
        for (int i = 0; i < samples.Length; i++)
        for (int j = 0; j < samples.Length; j++)
        {
            bool sameText = CdTimeFormat.Format(samples[i], false, false) == CdTimeFormat.Format(samples[j], false, false);
            bool sameKey  = CdTimeFormat.Key(samples[i], false, false)    == CdTimeFormat.Key(samples[j], false, false);
            Assert.Equal(sameText, sameKey);
        }
        Assert.NotEqual(CdTimeFormat.Key(57f, false, false), CdTimeFormat.Key(57f, false, true));
    }
}
