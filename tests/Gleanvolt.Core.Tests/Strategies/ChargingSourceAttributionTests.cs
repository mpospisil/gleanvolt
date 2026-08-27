using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class ChargingSourceAttributionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds a physically consistent reading: the grid is whatever the rest of the house, the car and
    /// the battery don't get from the sun, which is the relationship the real meters always satisfy.
    /// Deriving it here rather than passing it in stops a test asserting on an impossible installation.
    /// </summary>
    private static EnergyState Reading(
        double solarWatts,
        double otherLoadsWatts,
        double evWatts,
        double batteryWatts)
    {
        var gridWatts = otherLoadsWatts + evWatts + batteryWatts - solarWatts;

        return new EnergyState(
            Noon,
            BatterySocPercent: 80,
            BatteryPowerWatts: batteryWatts,
            SolarPowerWatts: solarWatts,
            GridPowerWatts: gridWatts,
            EvChargerStatus: EvChargerStatus.Charging,
            EvChargerPowerWatts: evWatts);
    }

    [Fact]
    public void SurplusSunCoversTheWholeChargeWhenThereIsEnoughOfIt()
    {
        // 6 kW of sun, 1 kW of house: 5 kW spare and the car is taking 4 kW of it.
        var split = ChargingSourceAttribution.Split(Reading(solarWatts: 6000, otherLoadsWatts: 1000, evWatts: 4000, batteryWatts: 0));

        Assert.Equal(4000, split.SolarWatts, 1);
        Assert.Equal(0, split.GridWatts, 1);
        Assert.Equal(0, split.BatteryWatts, 1);
    }

    [Fact]
    public void MoreSurplusThanTheCarIsTakingDoesNotInflateTheSolarShare()
    {
        // The spare 5 kW exceeds the 3 kW the car draws; the share is capped at what was delivered.
        var split = ChargingSourceAttribution.Split(Reading(solarWatts: 6000, otherLoadsWatts: 1000, evWatts: 3000, batteryWatts: 0));

        Assert.Equal(3000, split.SolarWatts, 1);
        Assert.Equal(3000, split.TotalWatts, 1);
    }

    [Fact]
    public void TheBatteryIsCreditedBeforeTheGrid()
    {
        // 5 kW spare sun, an 8 kW charge, and the pack pushing out 2 kW: sun first, then the pack,
        // and only the last kilowatt is bought from the grid.
        var split = ChargingSourceAttribution.Split(
            Reading(solarWatts: 6000, otherLoadsWatts: 1000, evWatts: 8000, batteryWatts: -2000));

        Assert.Equal(5000, split.SolarWatts, 1);
        Assert.Equal(2000, split.BatteryWatts, 1);
        Assert.Equal(1000, split.GridWatts, 1);
    }

    [Fact]
    public void AfterDarkTheChargeSplitsBetweenTheBatteryAndTheGrid()
    {
        var split = ChargingSourceAttribution.Split(
            Reading(solarWatts: 0, otherLoadsWatts: 500, evWatts: 4000, batteryWatts: -1500));

        Assert.Equal(0, split.SolarWatts, 1);
        Assert.Equal(1500, split.BatteryWatts, 1);
        Assert.Equal(2500, split.GridWatts, 1);
    }

    [Fact]
    public void AHouseAlreadyImportingContributesNoSolarShareRatherThanANegativeOne()
    {
        // The house is drawing more than the sun makes, so there is no surplus at all: a naive
        // subtraction would hand back a negative solar share and a grid share above the draw.
        var split = ChargingSourceAttribution.Split(
            Reading(solarWatts: 500, otherLoadsWatts: 2000, evWatts: 4000, batteryWatts: 0));

        Assert.Equal(0, split.SolarWatts, 1);
        Assert.Equal(4000, split.GridWatts, 1);
    }

    /// <summary>
    /// Builds a reading exactly as the meters reported it, consistent or not. <see cref="Reading"/>
    /// derives the grid from the other three and so can only ever describe an installation whose
    /// meters agree -- which is why nothing here caught the bug below.
    /// </summary>
    private static EnergyState AsMeasured(
        double solarWatts,
        double gridWatts,
        double evWatts,
        double batteryWatts) =>
        new(
            Noon,
            BatterySocPercent: 80,
            BatteryPowerWatts: batteryWatts,
            SolarPowerWatts: solarWatts,
            GridPowerWatts: gridWatts,
            EvChargerStatus: EvChargerStatus.Charging,
            EvChargerPowerWatts: evWatts);

    [Fact]
    public void AMeterThatMissesPartOfTheChargerDoesNotInventSunAtNight()
    {
        // Session 01a044f8, 2026-08-27 22:45 local, four hours after sunset. The charger reported
        // 10.8 kW, the grid meter only 7.4 kW, the pack was idle and the roof was making nothing --
        // an inconsistent set no correct installation produces, and exactly what this site reports
        // whenever the charger runs. The derived surplus comes out at +3.4 kW; the sun did not.
        var split = ChargingSourceAttribution.Split(
            AsMeasured(solarWatts: 0, gridWatts: 7427, evWatts: 10806, batteryWatts: 37));

        Assert.Equal(0, split.SolarWatts, 1);
        Assert.Equal(10806, split.GridWatts, 1);
        Assert.Equal(0, split.BatteryWatts, 1);
    }

    [Fact]
    public void TheSolarShareNeverExceedsWhatThePanelsAreMaking()
    {
        // The same mismatch in daylight, where real house load hides it: 2 kW on the roof cannot
        // fund a 5 kW solar share however favourably the arithmetic works out.
        var split = ChargingSourceAttribution.Split(
            AsMeasured(solarWatts: 2000, gridWatts: 6000, evWatts: 10000, batteryWatts: 0));

        Assert.Equal(2000, split.SolarWatts, 1);
        Assert.Equal(8000, split.GridWatts, 1);
        Assert.Equal(10000, split.TotalWatts, 1);
    }

    [Fact]
    public void AnUnderReportingMeterStillReconcilesToTheMeasuredDraw()
    {
        // The invariant has to survive the inconsistency too, not just be true when the meters agree.
        var split = ChargingSourceAttribution.Split(
            AsMeasured(solarWatts: 0, gridWatts: 7427, evWatts: 10806, batteryWatts: -1500));

        Assert.Equal(10806, split.TotalWatts, 1);
        Assert.Equal(0, split.SolarWatts, 1);
        Assert.Equal(1500, split.BatteryWatts, 1);
    }

    [Theory]
    [InlineData(6000, 1000, 4000, 0)]
    [InlineData(6000, 1000, 8000, -2000)]
    [InlineData(0, 500, 4000, -1500)]
    [InlineData(500, 2000, 4000, 0)]
    [InlineData(9000, 300, 11000, 2000)]
    public void TheThreeSharesAlwaysAddBackUpToTheMeasuredDraw(
        double solar,
        double otherLoads,
        double ev,
        double battery)
    {
        // The invariant the whole attribution rests on: however the split is argued, a session's
        // per-source totals must reconcile to the energy the car actually took.
        var reading = Reading(solar, otherLoads, ev, battery);

        Assert.Equal(reading.EvChargerPowerWatts, ChargingSourceAttribution.Split(reading).TotalWatts, 1);
    }

    [Fact]
    public void NoChargeMeansNothingIsAttributedAnywhere()
    {
        var split = ChargingSourceAttribution.Split(Reading(solarWatts: 4000, otherLoadsWatts: 1000, evWatts: 0, batteryWatts: 3000));

        Assert.Equal(default, split);
    }
}
