using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stellar.CooldownBar.Tests;

/// <summary>
/// Drift pin: CooldownBar's curated lockout map must equal CombatMeter's, entry for entry. The fixture is a verbatim
/// copy of CombatMeter origin/main's <c>LockoutArcaneSkill</c> (header names the commit + refresh command). When a new
/// lockout is added to either plugin, add it to BOTH and refresh the fixture — this test fails until they agree.
/// </summary>
public class LockoutMapDriftTests
{
    private static Dictionary<int, int> CombatMeterMap()
    {
        var src = File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "combatmeter-lockout-map.cs.txt"));
        Assert.Contains("LockoutArcaneSkill", src);
        return Regex.Matches(src, @"^\s*(\d+)\s*=>\s*(\d+)\s*,", RegexOptions.Multiline)
            .ToDictionary(m => int.Parse(m.Groups[1].Value), m => int.Parse(m.Groups[2].Value));
    }

    [Fact]
    public void Fixture_parses_to_a_non_empty_map()
    {
        var cm = CombatMeterMap();
        Assert.Equal(3971, cm[2110049]);
        Assert.Equal(3957, cm[2110050]);
    }

    [Fact]
    public void CooldownBar_map_equals_CombatMeter_map()
    {
        var cm = CombatMeterMap().OrderBy(kv => kv.Key).ToArray();
        var cb = DebuffAttribution.LockoutArcaneMap.OrderBy(kv => kv.Key).ToArray();
        Assert.Equal(cm, cb);
    }

    [Fact]
    public void Lookup_reads_the_map()
    {
        foreach (var (buff, skill) in DebuffAttribution.LockoutArcaneMap)
            Assert.Equal(skill, DebuffAttribution.LockoutArcaneSkill(buff));
        Assert.Null(DebuffAttribution.LockoutArcaneSkill(2110069));
    }
}
