using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.CooldownBar.Tests;

// Owner report 2026-11-21: "Physical Immunity" (55226) vanished from the picker. 2.4.1 bundled the raw Season-4 buff
// table (Chinese placeholder 气刃突刺计数), so every hidden buff got that one label. And because every hidden buff
// shares the placeholder as its RAW game name, a name group keyed on it would have tracked all ~10k of them.
public class PlaceholderBuffTests
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

    private const string PhEn = "Spirit Blade Thrust count";
    private const string PhCn = "气刃突刺计数";
    private const int PhysImmunity = 55226, PhysImmunity2 = 683314, OtherHidden = 201;

    [Fact]
    public void Physical_immunity_resolves_to_its_english_design_name()
    {
        TranslatedBuffText.LoadNowForTests();
        // The live game hands the placeholder as the buff's own name; the label must be the distinct NameDesign.
        Assert.Equal("Physical immunity BUFF", TranslatedBuffText.ResolveLabel(PhysImmunity, PhEn));
        Assert.Equal("Physical immunity buff", TranslatedBuffText.ResolveLabel(PhysImmunity2, PhCn));
    }

    [Fact]
    public void Season4_rows_are_present_and_placeholder_rows_never_label_as_the_chinese_placeholder()
    {
        TranslatedBuffText.LoadNowForTests();
        Assert.True(TranslatedBuffText.TryGet(2033211, out var potion, out _));   // S4-only Potion id
        Assert.Equal("Potion", potion);
        Assert.True(TranslatedBuffText.TryGet(2032311, out var cuisine, out _));  // S4-only Cuisine id
        Assert.Equal("Cuisine", cuisine);
        Assert.NotEqual(PhCn, TranslatedBuffText.ResolveLabel(10104, PhCn));      // an S4-only placeholder-named row
    }

    [Theory]
    [InlineData(PhEn)]
    [InlineData(PhCn)]
    [InlineData("")]
    public void A_group_whose_raw_name_is_a_placeholder_or_empty_is_never_recorded(string raw)
    {
        var sel = CooldownBarSelection.Load(new FakeConfig());
        sel.SetBuffGroup(PhysImmunity, raw, true, _ => raw);
        Assert.Empty(sel.BuffGroupReps);
        Assert.False(sel.IsBuffGroupTracked(raw, _ => raw));
    }

    [Fact]
    public void A_saved_placeholder_rep_tracks_nothing_and_is_dropped()
    {
        var cfg = new FakeConfig();
        cfg.Store["track.buffs"] = new[] { PhysImmunity };
        cfg.Store["track.buffGroups"] = new[] { PhysImmunity };   // what 2.4.1 saved when the row was ticked
        var sel = CooldownBarSelection.Load(cfg);
        Func<int, string> raw = _ => PhEn;                         // every hidden buff's raw game name
        Assert.False(sel.IsBuffGroupTracked(raw(OtherHidden), raw));
        Assert.Equal(1, sel.DropPlaceholderReps(raw));
        Assert.Empty(sel.BuffGroupReps);
        Assert.True(sel.IsBuffTracked(PhysImmunity));              // the ticked row itself stays ticked by id
        Assert.False(sel.IsBuffTracked(OtherHidden));
    }
}
