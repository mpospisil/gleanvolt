using Microsoft.Extensions.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Resolving the car from configuration (#124). The <see cref="PvSystemResolver"/> posture applied to
/// the vehicle: an unusable description is a startup failure naming the key, and an absent one is a
/// perfectly good state that changes nothing.
/// </summary>
public class EvResolverTests
{
    private static EvInfo Resolve(params (string Key, string? Value)[] settings) =>
        EvResolver.Resolve(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Build(),
            chargerMinAmps: 6,
            chargerMaxAmps: 16);

    [Fact]
    public void NoSectionIsAnUnknownCarRatherThanAFailure()
    {
        var ev = Resolve();

        Assert.Same(EvInfo.Unknown, ev);
        Assert.False(ev.IsConfigured);
        Assert.False(ev.CanTargetSoc);
    }

    [Fact]
    public void AnEntryWithNothingInItAlsoReadsAsUnconfigured()
    {
        // The two ways of saying nothing must read the same. This one is what the shipped
        // appsettings.json produces, so it is the case every default install actually runs.
        var ev = Resolve(
            ("Ev:Vehicles:0:Id", ""),
            ("Ev:Vehicles:0:Name", ""),
            ("Ev:Vehicles:0:BatteryCapacityKWh", "0"),
            ("Ev:Vehicles:0:Telemetry:Topic", "gleanvolt/vehicle/state"));

        Assert.False(ev.IsConfigured);
        Assert.Equal("gleanvolt/vehicle/state", ev.TelemetryTopic);
        Assert.Contains("no vehicle configured", ev.Describe());
    }

    [Fact]
    public void EmptyStringsBindToUnstatedLimitsRatherThanFailing()
    {
        // Compose writes `Ev__Vehicles__0__Phases: ${EV_PHASES:-}`, so an operator who sets nothing
        // hands the binder an empty string for an int?. If that threw, every default deployment would
        // fail to start -- and only on the Pi, never in a unit test that omits the key.
        var ev = Resolve(
            ("Ev:Vehicles:0:Phases", ""),
            ("Ev:Vehicles:0:MinChargingCurrentAmps", ""),
            ("Ev:Vehicles:0:MaxChargingCurrentAmps", ""),
            ("Ev:Vehicles:0:BatteryCapacityKWh", "0"));

        Assert.Null(ev.Phases);
        Assert.Null(ev.MinChargingCurrentAmps);
        Assert.Null(ev.MaxChargingCurrentAmps);
    }

    [Fact]
    public void ReadsTheCarItIsGiven()
    {
        var ev = Resolve(
            ("Ev:Vehicles:0:Id", "id4"),
            ("Ev:Vehicles:0:Name", "The ID.4"),
            ("Ev:Vehicles:0:Make", "Volkswagen"),
            ("Ev:Vehicles:0:Model", "ID.4 Pro"),
            ("Ev:Vehicles:0:BatteryCapacityKWh", "77"),
            ("Ev:Vehicles:0:ChargeEfficiency", "0.9"),
            ("Ev:Vehicles:0:Phases", "3"),
            ("Ev:Vehicles:0:MinChargingCurrentAmps", "6"),
            ("Ev:Vehicles:0:MaxChargingCurrentAmps", "16"),
            ("Ev:Vehicles:0:Telemetry:Topic", "gleanvolt/vehicle/id4/state"));

        Assert.True(ev.IsConfigured);
        Assert.Equal("id4", ev.Id);
        Assert.Equal("The ID.4", ev.Name);
        Assert.Equal(77, ev.BatteryCapacityKWh);
        Assert.Equal(77_000, ev.BatteryCapacityWh);
        Assert.True(ev.CanTargetSoc);
        Assert.Equal(3, ev.Phases);
        Assert.Equal("gleanvolt/vehicle/id4/state", ev.TelemetryTopic);
    }

    [Fact]
    public void TheNameFallsBackToTheId()
    {
        Assert.Equal("id4", Resolve(("Ev:Vehicles:0:Id", "id4")).Name);
    }

    [Fact]
    public void DescribesItselfForTheStartupLog()
    {
        var ev = Resolve(
            ("Ev:Vehicles:0:Name", "The ID.4"),
            ("Ev:Vehicles:0:Make", "Volkswagen"),
            ("Ev:Vehicles:0:Model", "ID.4 Pro"),
            ("Ev:Vehicles:0:BatteryCapacityKWh", "77"),
            ("Ev:Vehicles:0:Phases", "3"),
            ("Ev:Vehicles:0:MinChargingCurrentAmps", "6"),
            ("Ev:Vehicles:0:MaxChargingCurrentAmps", "16"));

        var described = ev.Describe();

        Assert.Contains("The ID.4", described);
        Assert.Contains("Volkswagen ID.4 Pro", described);
        Assert.Contains("77kWh usable", described);
        Assert.Contains("6-16A on 3 phase(s)", described);
    }

    [Fact]
    public void RefusesASecondVehicle()
    {
        // Silently driving the first of two is worse than not starting: the other is configured,
        // visible in the file, and doing nothing.
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(
            ("Ev:Vehicles:0:Id", "id4"),
            ("Ev:Vehicles:1:Id", "leaf")));

        Assert.Contains("exactly 1 is supported", error.Message);
    }

    [Fact]
    public void RefusesAnIdThatIsNotASlug()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(("Ev:Vehicles:0:Id", "The ID.4")));

        Assert.Contains("Ev:Vehicles:0:Id", error.Message);
        Assert.Contains("slug", error.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("-0.2")]
    public void RefusesAnImpossibleChargeEfficiency(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Resolve(("Ev:Vehicles:0:Id", "id4"), ("Ev:Vehicles:0:ChargeEfficiency", value)));

        Assert.Contains("ChargeEfficiency", error.Message);
    }

    [Fact]
    public void RefusesAPhaseCountNoWallboxHas()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Resolve(("Ev:Vehicles:0:Id", "id4"), ("Ev:Vehicles:0:Phases", "4")));

        Assert.Contains("must be 1, 2 or 3", error.Message);
    }

    [Fact]
    public void RefusesACarWhoseOwnFloorIsAboveItsOwnCeiling()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(
            ("Ev:Vehicles:0:Id", "id4"),
            ("Ev:Vehicles:0:MinChargingCurrentAmps", "16"),
            ("Ev:Vehicles:0:MaxChargingCurrentAmps", "10")));

        Assert.Contains("no current satisfies both", error.Message);
    }

    [Fact]
    public void RefusesACarThatCouldNeverChargeHere()
    {
        // Its floor is above the installation's ceiling. Every symptom of that is silence, which is
        // why it is caught at startup naming both keys.
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(
            ("Ev:Vehicles:0:Id", "id4"),
            ("Ev:Vehicles:0:MinChargingCurrentAmps", "20")));

        Assert.Contains("no current in common", error.Message);
        Assert.Contains("ChargeControl:MaxChargingCurrentAmps", error.Message);
    }

    [Fact]
    public void CollectsEveryProblemRatherThanStoppingAtTheFirst()
    {
        // Fixing configuration one restart per mistake is a miserable way to spend an evening.
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(
            ("Ev:Vehicles:0:Id", "Not A Slug"),
            ("Ev:Vehicles:0:Phases", "7"),
            ("Ev:Vehicles:0:ChargeEfficiency", "3")));

        Assert.Contains("Ev:Vehicles:0:Id", error.Message);
        Assert.Contains("Phases", error.Message);
        Assert.Contains("ChargeEfficiency", error.Message);
    }
}
