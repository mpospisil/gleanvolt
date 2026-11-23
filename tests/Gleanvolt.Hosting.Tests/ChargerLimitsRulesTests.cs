using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Chargers;
using Microsoft.Extensions.Configuration;
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
    public void The_contract_rule_and_the_startup_rule_say_the_same(int min, int max, int phases, int chargerMin, int chargerMax, int chargerPhases)
    {
        var limits = new ChargerLimits(min, max, phases, 230);
        var charger = new ChargerCapabilities(chargerMin, chargerMax, chargerPhases);
        var gleanvolt = new ConfigurationProblems();
        PvSystemRules.CheckChargeLimits(ChargerHub.Describe("Test plugin", charger), min, max, phases, gleanvolt);

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

    [Theory]
    [InlineData("external", 0)]
    [InlineData("solax", 1)]
    public void At_startup_a_plugins_charger_is_not_checked_since_its_hello_is(string driver, int problems)
    {
        // 40 A is beyond what a 1.0 plugin was taken to do (32 A), and beyond SolaX's.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ChargeControl:MaxChargingCurrentAmps"] = "40" })
            .Build();
        var found = new ConfigurationProblems();

        ChargerDrivers.CheckLimits(configuration, Site(driver), found);

        Assert.Equal(problems, found.Count);
    }

    private static PvSystemInfo Site(string driver) => new(
        Id: "test-site",
        Name: "Test site",
        Address: string.Empty,
        Latitude: 49.2678,
        Longitude: 16.5295,
        AzimuthDegrees: 180,
        TiltDegrees: 35,
        PeakPowerWatts: 5000,
        InverterCapacityWatts: null,
        LossFactor: null,
        InstallDate: null,
        Inverter: new PvDeviceInfo("Inverter", string.Empty, string.Empty, new DeviceConfig { Host = "127.0.0.1" }),
        Chargers: [new PvDeviceInfo("charger", string.Empty, string.Empty, new DeviceConfig { Host = "192.168.2.6" }) { Driver = driver }]);
}
