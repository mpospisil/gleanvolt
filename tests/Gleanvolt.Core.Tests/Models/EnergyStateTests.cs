using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;

namespace Gleanvolt.Core.Tests.Models;

public class EnergyStateTests
{
    // Grid uses the import-positive convention (negative = exporting).
    private static EnergyState StateWith(double solar, double grid, double battery, double ev) =>
        new(
            DateTimeOffset.UtcNow,
            BatterySocPercent: 98,
            BatteryPowerWatts: battery,
            SolarPowerWatts: solar,
            GridPowerWatts: grid,
            EvChargerStatus: EvChargerStatus.Charging,
            EvChargerPowerWatts: ev);

    [Fact]
    public void SolarSurplus_IsSolarMinusHouseholdConsumption()
    {
        // 9000W sun, car taking 5000W, 3500W exported, battery idle.
        // Household consumption = 9000 - 3500 - 5000 = 500W, so the car may have 9000 - 500 = 8500W
        // (what it already draws plus what is being exported).
        var state = StateWith(solar: 9000, grid: -3500, battery: 0, ev: 5000);

        Assert.Equal(500, state.OtherLoadsPowerWatts);
        Assert.Equal(8500, state.SolarSurplusPowerWatts);
    }

    [Fact]
    public void SolarSurplus_WhenImporting_IsOnlyWhatTheSunActuallyCovers()
    {
        // The real failure case: 2422W of sun, car pulling 10785W, battery covering 5251W and the
        // rest imported. Household load is 500W, so only 1922W is genuinely solar -- far below the
        // 4140W three-phase 6A floor, so charging must pause.
        var state = StateWith(solar: 2422, grid: 3612, battery: -5251, ev: 10785);

        Assert.Equal(500, state.OtherLoadsPowerWatts);
        Assert.Equal(1922, state.SolarSurplusPowerWatts);
        Assert.True(state.SolarSurplusPowerWatts < state.SolarPowerWatts);
    }

    [Fact]
    public void SolarSurplus_ExcludesBatteryCharging_SoTheCarCanOutbidIt()
    {
        // Household consumption deliberately excludes battery charging, so the 1500W the battery is
        // taking still counts as available to the car.
        var state = StateWith(solar: 6000, grid: -4000, battery: 1500, ev: 0);

        Assert.Equal(500, state.OtherLoadsPowerWatts);
        Assert.Equal(5500, state.SolarSurplusPowerWatts);
    }

    [Fact]
    public void OtherLoads_CannotGoNegative_WhenTheGridMeterMissesPartOfTheCharger()
    {
        // Session 01a04533, 2026-08-27 23:49 local, well after dark. The charger reported 10880W and
        // the grid meter only 7399W with the pack idle and the roof making nothing -- the site's CT
        // does not see one phase of the three-phase charger. The raw residual is -3520W, which claims
        // the house is generating 3.5kW in the dark.
        var state = StateWith(solar: 0, grid: 7399, battery: 39, ev: 10880);

        Assert.Equal(0, state.OtherLoadsPowerWatts);
    }

    [Fact]
    public void SolarSurplus_NeverExceedsWhatTheRoofIsMaking()
    {
        // The consequence that reached the control loop: the same reading used to yield Surplus=3520W
        // at midnight, and Solar and Forecasted steer on exactly this number.
        var state = StateWith(solar: 0, grid: 7399, battery: 39, ev: 10880);

        Assert.Equal(0, state.SolarSurplusPowerWatts);
        Assert.True(state.SolarSurplusPowerWatts <= state.SolarPowerWatts);
    }

    [Fact]
    public void SolarSurplus_IsStillCappedByTheRoofInDaylight()
    {
        // Daylight, where real house load hides the mismatch: 2000W on the roof cannot fund a surplus
        // above 2000W however the residual comes out.
        var state = StateWith(solar: 2000, grid: 6000, battery: 0, ev: 10000);

        Assert.Equal(0, state.OtherLoadsPowerWatts);
        Assert.Equal(2000, state.SolarSurplusPowerWatts);
    }

    [Fact]
    public void TheFloorLeavesConsistentReadingsAlone()
    {
        // The floor must be invisible on an installation whose meters agree, which is every reading
        // the rest of this suite describes: a genuine 500W residual is untouched.
        var state = StateWith(solar: 9000, grid: -3500, battery: 0, ev: 5000);

        Assert.Equal(500, state.OtherLoadsPowerWatts);
        Assert.Equal(8500, state.SolarSurplusPowerWatts);
    }

    [Fact]
    public void OtherLoadsPowerWatts_IsHouseholdBaseLoad_ExcludingPvEvAndBattery()
    {
        // Solar 7267W, exporting 7016W to grid (import convention -> Grid = -7016), battery idle,
        // no EV: household base load = 7267 - 7016 = 251W (the residual seen in real telemetry).
        var state = new EnergyState(
            DateTimeOffset.UtcNow,
            BatterySocPercent: 100,
            BatteryPowerWatts: 0,
            SolarPowerWatts: 7267,
            GridPowerWatts: -7016,
            EvChargerStatus: EvChargerStatus.Available,
            EvChargerPowerWatts: 0);

        Assert.Equal(251, state.OtherLoadsPowerWatts);
    }
}
