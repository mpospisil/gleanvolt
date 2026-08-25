using Microsoft.Extensions.Configuration;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Reading an installation out of configuration (issue #111). Two rules carry most of these tests: an
/// older key wins wherever it is set, so upgrading changes nothing about how a running site behaves;
/// and a site that cannot be described stops the host at startup rather than at the first Modbus call.
/// </summary>
public class PvSystemResolverTests
{
    private static PvSystemResolution Resolve(params (string Key, string? Value)[] settings) =>
        PvSystemResolver.Resolve(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    private static readonly (string Key, string? Value)[] PvDevices =
    [
        ("Pv:Inverter:Host", "192.168.2.10"),
        ("Pv:Chargers:0:Host", "192.168.2.6"),
    ];

    private static (string Key, string? Value)[] WithDevices(params (string Key, string? Value)[] settings) =>
        [.. PvDevices, .. settings];

    [Fact]
    public void ASystemDescribedInThePvSectionIsWhatComesOut()
    {
        var resolution = Resolve(
            ("Pv:Id", "home-roof"),
            ("Pv:Name", "Home Roof"),
            ("Pv:Address", "Krásného 12, Praha"),
            ("Pv:Latitude", "50.0755"),
            ("Pv:Longitude", "14.4378"),
            ("Pv:TiltDegrees", "35"),
            ("Pv:CapacityKwp", "9.2"),
            ("Pv:LossFactor", "0.9"),
            ("Pv:InstallDate", "2024-05-01"),
            ("Pv:Inverter:Model", "SolaX X3-HYB-G4 PRO"),
            ("Pv:Inverter:Host", "192.168.2.10"),
            ("Pv:Chargers:0:Id", "wallbox"),
            ("Pv:Chargers:0:Name", "Garage wallbox"),
            ("Pv:Chargers:0:Model", "SolaX X3-HAC"),
            ("Pv:Chargers:0:Host", "192.168.2.6"));

        var site = resolution.Site;

        Assert.Equal("home-roof", site.Id);
        Assert.Equal("Home Roof", site.Name);
        Assert.Equal("Krásného 12, Praha", site.Address);
        Assert.Equal(50.0755, site.Latitude);
        Assert.Equal(14.4378, site.Longitude);
        Assert.Equal(35, site.TiltDegrees);
        Assert.Equal(9.2, site.CapacityKwp);
        Assert.Equal(0.9, site.LossFactor);
        Assert.Equal(new DateOnly(2024, 5, 1), site.InstallDate);
        Assert.True(site.HasLocation);

        Assert.Equal("SolaX X3-HYB-G4 PRO", site.Inverter.Model);
        Assert.Equal("192.168.2.10", site.Inverter.Connection.Host);

        var charger = Assert.Single(site.Chargers);
        Assert.Equal("wallbox", charger.Id);
        Assert.Equal("Garage wallbox", charger.Name);
        Assert.Equal("SolaX X3-HAC", charger.Model);
        Assert.Equal("192.168.2.6", charger.Connection.Host);

        // Nothing deprecated was used, so there is nothing for the log to tell the operator to change.
        Assert.Empty(resolution.Deprecations);
    }

    [Fact]
    public void AnUnsetPortOrIntervalTakesTheDeviceDefaultRatherThanZero()
    {
        var site = Resolve(WithDevices()).Site;

        Assert.Equal(502, site.Inverter.Connection.Port);
        Assert.Equal(1, site.Inverter.Connection.UnitId);
        Assert.Equal(TimeSpan.FromMilliseconds(250), site.Inverter.Connection.MinRequestInterval);
        Assert.Equal(502, site.Chargers[0].Connection.Port);
    }

    [Fact]
    public void AChargerWithNoIdOfItsOwnGetsOne()
    {
        var site = Resolve(WithDevices()).Site;

        Assert.Equal(PvSystemResolver.DefaultChargerId, Assert.Single(site.Chargers).Id);
    }

    [Fact]
    public void TheOlderDeviceKeysStillWin()
    {
        // The whole point of the additive phase: a deployment that upgrades without touching its .env
        // must talk to exactly the devices it talked to yesterday, whatever the new section says.
        var resolution = Resolve(
            ("Pv:Inverter:Host", "10.0.0.1"),
            ("Pv:Chargers:0:Host", "10.0.0.2"),
            ("Solax:Inverter:Host", "192.168.2.10"),
            ("Solax:EvCharger:Host", "192.168.2.6"));

        Assert.Equal("192.168.2.10", resolution.Site.Inverter.Connection.Host);
        Assert.Equal("192.168.2.6", resolution.Site.Chargers[0].Connection.Host);

        // And says so, once per key, because the log is the migration checklist.
        Assert.Equal(2, resolution.Deprecations.Count);
        Assert.Contains(resolution.Deprecations, message => message.Contains("Solax:Inverter"));
        Assert.Contains(resolution.Deprecations, message => message.Contains("Solax:EvCharger"));
    }

    [Fact]
    public void WhatADeviceIsCalledComesFromThePvSectionEvenWhenItsAddressDoesNot()
    {
        // The older keys are an address and nothing else -- they cannot express a model or a name -- so
        // reading those from the only section that has them takes nothing away from anyone.
        var site = Resolve(
            ("Pv:Inverter:Model", "SolaX X3-HYB-G4 PRO"),
            ("Pv:Chargers:0:Id", "wallbox"),
            ("Pv:Chargers:0:Model", "SolaX X3-HAC"),
            ("Solax:Inverter:Host", "192.168.2.10"),
            ("Solax:EvCharger:Host", "192.168.2.6")).Site;

        Assert.Equal("SolaX X3-HYB-G4 PRO", site.Inverter.Model);
        Assert.Equal("192.168.2.10", site.Inverter.Connection.Host);

        var charger = Assert.Single(site.Chargers);
        Assert.Equal("wallbox", charger.Id);
        Assert.Equal("SolaX X3-HAC", charger.Model);
        Assert.Equal("192.168.2.6", charger.Connection.Host);
    }

    [Fact]
    public void ADeploymentDescribedOnlyByTheOlderKeysStillResolves()
    {
        // The upgrade case with nothing added to .env at all: two addresses, no Pv section.
        var site = Resolve(("Solax:Inverter:Host", "192.168.2.10"), ("Solax:EvCharger:Host", "192.168.2.6")).Site;

        Assert.Equal("192.168.2.10", site.Inverter.Connection.Host);
        Assert.Equal(PvSystemResolver.DefaultChargerId, Assert.Single(site.Chargers).Id);
        Assert.Equal("192.168.2.6", site.Chargers[0].Connection.Host);
    }

    [Fact]
    public void TheOlderCoordinatesStillWin()
    {
        var resolution = Resolve(WithDevices(
            ("Pv:Latitude", "50.0755"),
            ("Pv:Longitude", "14.4378"),
            ("Weather:Latitude", "49.2678"),
            ("Weather:Longitude", "16.5295")));

        Assert.Equal(49.2678, resolution.Site.Latitude);
        Assert.Equal(16.5295, resolution.Site.Longitude);
        Assert.Contains(resolution.Deprecations, message => message.Contains("Weather:Latitude"));
    }

    [Fact]
    public void CoordinatesOnlyInThePvSectionAreUsedWithNothingToReport()
    {
        var resolution = Resolve(WithDevices(("Pv:Latitude", "50.0755"), ("Pv:Longitude", "14.4378")));

        Assert.Equal(50.0755, resolution.Site.Latitude);
        Assert.Empty(resolution.Deprecations);
    }

    [Fact]
    public void ASiteWithNoCoordinatesAtAllIsFine()
    {
        // The weather integration is optional, and an unconfigured one is a configuration state rather
        // than a failure -- see WeatherOptions.
        var site = Resolve(WithDevices()).Site;

        Assert.False(site.HasLocation);
        Assert.Null(site.Latitude);
    }

    [Fact]
    public void HalfAPairOfCoordinatesDescribesNowhereAndStopsTheHost()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(WithDevices(("Pv:Latitude", "50.0755"))));

        Assert.Contains("Pv:Latitude", error.Message);
        Assert.Contains("Pv:Longitude", error.Message);
    }

    [Fact]
    public void NoInverterIsAStartupFailureRatherThanAConnectionErrorLater()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(("Pv:Chargers:0:Host", "192.168.2.6")));

        Assert.Contains("Pv:Inverter:Host", error.Message);
    }

    [Fact]
    public void NoChargerIsAStartupFailureToo()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(("Pv:Inverter:Host", "192.168.2.10")));

        Assert.Contains("Pv:Chargers:0:Host", error.Message);
    }

    [Fact]
    public void ASecondChargerIsRefusedRatherThanSilentlyIgnored()
    {
        // Configuration can express two so that it need not change shape when the control logic can
        // drive two. Accepting the first and dropping the second would mean a car that never charges
        // and nothing anywhere saying why.
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(
            ("Pv:Inverter:Host", "192.168.2.10"),
            ("Pv:Chargers:0:Host", "192.168.2.6"),
            ("Pv:Chargers:1:Host", "192.168.2.7")));

        Assert.Contains("Only 1 EV charger is supported", error.Message);
    }

    [Fact]
    public void EveryProblemIsReportedAtOnce()
    {
        // One restart per mistake is a miserable way to configure anything.
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(
            ("Pv:Id", "Home Roof"),
            ("Pv:TiltDegrees", "120"),
            ("Pv:LossFactor", "9"),
            ("Pv:InstallDate", "last spring")));

        Assert.Contains("Pv:Id", error.Message);
        Assert.Contains("Pv:TiltDegrees", error.Message);
        Assert.Contains("Pv:LossFactor", error.Message);
        Assert.Contains("Pv:InstallDate", error.Message);
        Assert.Contains("Pv:Inverter:Host", error.Message);
    }

    [Theory]
    [InlineData("Home Roof")]   // spaces are not topic segments
    [InlineData("HomeRoof")]    // nor are capitals, in a lower-cased id
    [InlineData("-roof")]       // nor a leading separator
    public void AnIdThatCannotBeATopicSegmentIsRefused(string id)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(WithDevices(("Pv:Id", id))));

        Assert.Contains("Pv:Id", error.Message);
    }

    [Fact]
    public void AnIdIsOptionalWhileNothingConsumesIt()
    {
        // It becomes required in the phase that puts it in a topic. Demanding it now would stop every
        // existing deployment over a value that would change nothing about how it ran.
        var site = Resolve(WithDevices()).Site;

        Assert.Equal(string.Empty, site.Id);
    }

    [Fact]
    public void TwoChargersCannotShareAnId()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(
            ("Pv:Inverter:Host", "192.168.2.10"),
            ("Pv:Chargers:0:Id", "wallbox"),
            ("Pv:Chargers:0:Host", "192.168.2.6"),
            ("Pv:Chargers:1:Id", "wallbox"),
            ("Pv:Chargers:1:Host", "192.168.2.7")));

        Assert.Contains("used by more than one charger", error.Message);
    }

    [Theory]
    [InlineData("-90", 270)]
    [InlineData("270", 270)]
    [InlineData("172", 172)]
    [InlineData("360", 0)]
    public void AnAzimuthIsStoredAsOneBearingHoweverItWasWritten(string configured, double expected)
    {
        var site = Resolve(WithDevices(("Pv:AzimuthDegrees", configured))).Site;

        Assert.Equal(expected, site.AzimuthDegrees);
    }

    [Fact]
    public void TheSiteDescribesItselfForTheLog()
    {
        var description = Resolve(WithDevices(
            ("Pv:Id", "home-roof"),
            ("Pv:Name", "Home Roof"),
            ("Pv:Latitude", "50.0755"),
            ("Pv:Longitude", "14.4378"),
            ("Pv:Inverter:Model", "SolaX X3-HYB-G4 PRO"))).Site.Describe();

        Assert.Contains("Home Roof (home-roof)", description);
        Assert.Contains("50.0755,14.4378", description);
        Assert.Contains("SolaX X3-HYB-G4 PRO at 192.168.2.10:502", description);
    }
}
