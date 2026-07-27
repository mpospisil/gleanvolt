using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Core.Tests.Strategies;

public class BatteryDischargeHoldStrategyTests
{
    // Builds a state with the given PV, grid and battery figures (positive Grid = importing, positive
    // Battery = charging), from which HouseLoad = PV + Grid - Battery follows.
    private static EnergyState State(double solarWatts, double gridWatts, double batteryWatts = 0, double evWatts = 0) =>
        new(DateTimeOffset.UnixEpoch, BatterySocPercent: 50, batteryWatts, solarWatts, gridWatts,
            EvChargerStatus.Charging, evWatts);

    [Fact]
    public void PvCoversTheHouse_PushesOutTheWholeLoad_LeavingTheSurplusForTheBattery()
    {
        // 6 kW PV, 2 kW house load (the other 4 kW is charging the battery).
        var state = State(solarWatts: 6000, gridWatts: 0, batteryWatts: 4000);

        Assert.Equal(2000, state.HouseLoadPowerWatts);
        // Push out exactly the load: PV serves the house, and the 4 kW it doesn't need has nowhere to
        // go but the battery, so surplus charging survives the hold.
        Assert.Equal(-2000, BatteryDischargeHoldStrategy.ActivePowerTargetWatts(state));
    }

    [Fact]
    public void PvFallsShort_PushesOutOnlyThePv_SoTheGridCoversTheRest()
    {
        // 1 kW PV against an 8 kW load (EV charging): 7 kW is being imported.
        var state = State(solarWatts: 1000, gridWatts: 7000, evWatts: 7500);

        Assert.Equal(8000, state.HouseLoadPowerWatts);
        // Command only the PV that exists; the inverter cannot make up the rest, so the grid does —
        // never the battery.
        Assert.Equal(-1000, BatteryDischargeHoldStrategy.ActivePowerTargetWatts(state));
    }

    [Fact]
    public void BatteryDischarging_IsCountedAsLoadTheGridShouldCover()
    {
        // The situation the feature exists for: no sun, EV charging, battery discharging 5 kW into it.
        var state = State(solarWatts: 0, gridWatts: 2000, batteryWatts: -5000, evWatts: 7000);

        Assert.Equal(7000, state.HouseLoadPowerWatts);
        // No PV to push, so the target is 0: the inverter contributes nothing and the grid takes over.
        Assert.Equal(0, BatteryDischargeHoldStrategy.ActivePowerTargetWatts(state));
    }

    [Fact]
    public void TargetIsNeverPositive_SoTheHoldNeverCommandsAnImport()
    {
        // Exporting heavily with a negative residual load — the target must still not flip sign.
        var state = State(solarWatts: 5000, gridWatts: -6000, batteryWatts: 0);

        Assert.True(state.HouseLoadPowerWatts < 0);
        Assert.Equal(0, BatteryDischargeHoldStrategy.ActivePowerTargetWatts(state));
    }

    [Fact]
    public void TargetIsNeverMoreNegativeThanThePvAvailable()
    {
        // Whatever the load, the inverter is never asked to push out more than it is producing —
        // which is what makes upstream's minimum-SOC clamp redundant here.
        var state = State(solarWatts: 3000, gridWatts: 9000, evWatts: 11000);

        Assert.Equal(-3000, BatteryDischargeHoldStrategy.ActivePowerTargetWatts(state));
    }

    [Fact]
    public void AbsurdTelemetry_IsClampedToTheSanityCeiling()
    {
        var state = State(solarWatts: 500_000, gridWatts: 500_000);

        Assert.Equal(-BatteryDischargeHoldStrategy.MaxTargetMagnitudeWatts,
            BatteryDischargeHoldStrategy.ActivePowerTargetWatts(state));
    }

    [Fact]
    public void NegativePvReading_IsTreatedAsZeroRatherThanFlippingTheSign()
    {
        var state = State(solarWatts: -50, gridWatts: 1000);

        Assert.Equal(0, BatteryDischargeHoldStrategy.ActivePowerTargetWatts(state));
    }
}
