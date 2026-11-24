using Gleanvolt.Core.Configuration;
using Gleanvolt.Hosting.Chargers;
using Gleanvolt.Providers.Contracts.Charger;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The limits rule a plugin's tests use (<see cref="ChargerLimitsRules"/>, #440) says what Gleanvolt's charger hub says:
/// it refuses by <see cref="PvSystemRules.CheckChargeLimits"/>, so the two must agree word for word.
/// </summary>
public sealed class ChargerLimitsRulesTests
{
    [Theory]
    [InlineData(6, 16, 3, 6, 16, 3)]
    [InlineData(6, 32, 1, 6, 32, 1)]
    [InlineData(6, 20, 3, 6, 16, 1)]
    [InlineData(5, 20, 1, 6, 32, 3)]
    [InlineData(8, 40, 3, 6, 32, 3)]
    [InlineData(1, 81, 3, 6, 80, 1)]
    public void The_contract_rule_and_the_hubs_rule_say_the_same(int min, int max, int phases, int chargerMin, int chargerMax, int chargerPhases)
    {
        var limits = new ChargerLimits(min, max, phases, 230);
        var charger = new ChargerCapabilities(chargerMin, chargerMax, chargerPhases);
        var gleanvolt = new ConfigurationProblems();
        PvSystemRules.CheckChargeLimits("Test plugin", ExternalChargerDriver.CapabilitiesOf(charger), min, max, phases, gleanvolt);

        Assert.Equal([.. gleanvolt], ChargerLimitsRules.Check(limits, charger, "Test plugin"));
        Assert.Equal(gleanvolt.Count, gleanvolt.Phrases.Count);
    }

    [Theory]
    [InlineData(6, 16, 3, 0, true)]
    [InlineData(6, 32, 1, 0, true)]
    [InlineData(0, 16, 3, 0, false)]
    [InlineData(16, 6, 3, 0, false)]
    [InlineData(6, 81, 3, 0, false)]
    [InlineData(6, 16, 4, 0, false)]
    [InlineData(6, 16, 0, 0, false)]
    [InlineData(6, 16, 3, 6, false)]
    [InlineData(6, 16, 3, -1, false)]
    public void Capabilities_are_usable_only_within_the_contract(int min, int max, int phases, int pause, bool usable)
    {
        Assert.Equal(usable, new ChargerCapabilities(min, max, phases, pause).Check().Count == 0);
    }
}
