using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Core.Tests.Strategies;

public class LiveSolarChargingControllerTests
{
    // 230V single-phase, 6-20A, 1A steps, 200W resume hysteresis. Battery gate: engage >=95%, release <90%.
    // minWatts = 6 * 230 = 1380W; resume threshold = 1580W.
    private static readonly LiveSolarChargingController Controller = new(
        new ChargePowerConverter(nominalVoltage: 230, phases: 1),
        minChargingCurrentAmps: 6,
        maxChargingCurrentAmps: 20,
        currentStepAmps: 1,
        hysteresisWatts: 200,
        fullSocPercent: 95,
        releaseSocPercent: 90);

    private static readonly EvChargerSettings Charging10A = new(EvChargerMode.Fast, 10);
    private static readonly EvChargerSettings Stopped = new(EvChargerMode.Stop, 0);

    // Live surplus = SolarPowerWatts - OtherLoadsPowerWatts. With EV/Battery = 0 and Grid = -surplus,
    // OtherLoads = Solar + Grid = 0, so the available surplus equals `surplusWatts`.
    private static EnergyState State(double socPercent, EvChargerStatus status, double surplusWatts) =>
        new(
            DateTimeOffset.UtcNow,
            BatterySocPercent: socPercent,
            BatteryPowerWatts: 0,
            SolarPowerWatts: surplusWatts,
            GridPowerWatts: -surplusWatts,
            EvChargerStatus: status,
            EvChargerPowerWatts: 0);

    private static ChargingControlInput Input(
        double socPercent,
        EvChargerStatus status,
        double surplusWatts,
        EvChargerSettings currentSettings,
        bool hasControl) =>
        new(State(socPercent, status, surplusWatts), surplusWatts, currentSettings, hasControl);

    [Fact]
    public void Available_AndStopped_WithConditionsMet_StartsCharging()
    {
        // The charger sits idle (Available + Stop) -- exactly where our own reset leaves it. With the
        // battery full and surplus available, charging must start from here.
        var result = Controller.Decide(Input(100, EvChargerStatus.Available, 5000, Stopped, hasControl: false));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(EvChargerMode.Fast, result.TargetSettings!.Mode);
    }

    [Fact]
    public void Available_WithConditionsNotMet_AndHoldingControl_ResetsToIdle()
    {
        var result = Controller.Decide(Input(50, EvChargerStatus.Available, 5000, Charging10A, hasControl: true));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
    }

    [Fact]
    public void ConditionsNotMet_WithoutControl_LeavesChargerUntouched()
    {
        var result = Controller.Decide(Input(50, EvChargerStatus.Available, 5000, Stopped, hasControl: false));

        Assert.Equal(ChargingControlAction.None, result.Action);
    }

    [Fact]
    public void ConfiguredMaxAboveHardwareLimit_IsClampedTo32A()
    {
        // Configured 40A max, but the hardware only accepts 6-32A: a huge surplus must still target 32A.
        var overConfigured = new LiveSolarChargingController(
            new ChargePowerConverter(nominalVoltage: 230, phases: 1),
            minChargingCurrentAmps: 6, maxChargingCurrentAmps: 40, currentStepAmps: 1,
            hysteresisWatts: 200, fullSocPercent: 95, releaseSocPercent: 90);

        var result = overConfigured.Decide(Input(100, EvChargerStatus.Charging, 30000, Charging10A, hasControl: true));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(EvChargerLimits.MaxCurrentAmps, result.TargetSettings!.ChargeCurrentAmps);
    }

    [Fact]
    public void BatteryBelowFull_NotCharging_DoesNothing()
    {
        // 94% < 95% engage threshold, not currently charging: wait for a full battery.
        var result = Controller.Decide(Input(94, EvChargerStatus.ChargePaused, 5000, Stopped, hasControl: false));

        Assert.Equal(ChargingControlAction.None, result.Action);
    }

    [Fact]
    public void BatteryBelowRelease_WhileCharging_Pauses()
    {
        // Dropped to 89% (< 90% release) while charging: disengage and pause.
        var result = Controller.Decide(Input(89, EvChargerStatus.Charging, 5000, Charging10A, hasControl: true));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
    }

    [Fact]
    public void BatteryBetweenReleaseAndFull_WhileCharging_KeepsCharging()
    {
        // 92% is below the 95% engage threshold but above the 90% release threshold: since we're
        // already charging, hysteresis keeps us going.
        var result = Controller.Decide(Input(92, EvChargerStatus.Charging, 4000, Charging10A, hasControl: true));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
    }

    [Fact]
    public void BatteryBetweenReleaseAndFull_NotCharging_DoesNotStart()
    {
        // 92% below the 95% engage threshold and not charging: don't start yet.
        var result = Controller.Decide(Input(92, EvChargerStatus.ChargePaused, 4000, Stopped, hasControl: false));

        Assert.Equal(ChargingControlAction.None, result.Action);
    }

    [Fact]
    public void BatteryFull_SurplusAboveResumeThreshold_StartsCharging()
    {
        // Not charging (no control held), 96% >= 95%, surplus 4000W clears the resume threshold ->
        // start, charging at floor(4000/230) = 17A.
        var result = Controller.Decide(Input(96, EvChargerStatus.Preparing, 4000, Stopped, hasControl: false));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(new EvChargerSettings(EvChargerMode.Fast, 17), result.TargetSettings);
    }

    [Fact]
    public void BatteryFull_NotCharging_SurplusBetweenMinAndResume_DoesNotStart()
    {
        // Surplus 1450W: above min (1380) but below the resume threshold (1580). Not charging and not
        // holding control, so we don't start and leave the charger alone.
        var result = Controller.Decide(Input(96, EvChargerStatus.ChargePaused, 1450, Stopped, hasControl: false));

        Assert.Equal(ChargingControlAction.None, result.Action);
    }

    [Fact]
    public void BatteryFull_SurplusBelowMinimum_Pauses()
    {
        var result = Controller.Decide(Input(96, EvChargerStatus.Charging, 1000, Charging10A, hasControl: true));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
    }

    [Fact]
    public void CloudPasses_SuspendsCharging_ThenResumesWhenSurplusReturns()
    {
        var suspended = new EvChargerSettings(EvChargerMode.Stop, 6); // what a pause leaves behind

        // 1. Charging happily on 4000W of surplus.
        var charging = Controller.Decide(Input(96, EvChargerStatus.Charging, 4000, Charging10A, hasControl: true));
        Assert.Equal(ChargingControlAction.Charge, charging.Action);

        // 2. A cloud drops the surplus below the 6A floor -> suspend (the coordinator pauses; it never
        //    sends a stop command, so the session survives).
        var paused = Controller.Decide(Input(96, EvChargerStatus.Charging, 1000, Charging10A, hasControl: true));
        Assert.Equal(ChargingControlAction.Pause, paused.Action);

        // 3. The cloud clears. The charger is now suspended (Stop/6A) and control was released, yet the
        //    returning surplus must bring charging straight back.
        var resumed = Controller.Decide(Input(96, EvChargerStatus.ChargePaused, 4000, suspended, hasControl: false));
        Assert.Equal(ChargingControlAction.Charge, resumed.Action);
        Assert.Equal(EvChargerMode.Fast, resumed.TargetSettings!.Mode);
        Assert.Equal(17, resumed.TargetSettings.ChargeCurrentAmps); // 4000W / 230V -> 17A
    }

    // The 6A hard cutoff: a car won't accept less than 6A, so below that we must STOP rather than sit
    // at the 6A minimum -- otherwise the charger makes up the shortfall from the grid.
    [Theory]
    [InlineData(1380, ChargingControlAction.Charge)] // exactly 6A worth -> keep charging
    [InlineData(1379, ChargingControlAction.Pause)]  // one watt short -> must stop, never idle at 6A
    [InlineData(920, ChargingControlAction.Pause)]   // ~4A worth -> must stop
    public void WhileCharging_SurplusAtOrBelowTheSixAmpFloor_ChargesOrStops(double surplusWatts, ChargingControlAction expected)
    {
        var result = Controller.Decide(Input(96, EvChargerStatus.Charging, surplusWatts, Charging10A, hasControl: true));

        Assert.Equal(expected, result.Action);
    }

    [Fact]
    public void ThreePhase_SurplusBelowThreePhaseFloor_Pauses()
    {
        // Three-phase: min = 6 * 230 * 3 = 4140W. A 3000W surplus clears the single-phase floor but
        // NOT the three-phase one -- the car would draw ~4.2kW and import from the grid, so pause.
        var threePhase = new LiveSolarChargingController(
            new ChargePowerConverter(nominalVoltage: 230, phases: 3),
            minChargingCurrentAmps: 6, maxChargingCurrentAmps: 20, currentStepAmps: 1,
            hysteresisWatts: 200, fullSocPercent: 95, releaseSocPercent: 90);

        var result = threePhase.Decide(Input(96, EvChargerStatus.Charging, 3000, Charging10A, hasControl: true));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
    }

    [Fact]
    public void ThreePhase_AmpsUsePhaseAwareConversion()
    {
        // Three-phase: 6900W surplus -> 6900 / (230*3) = 10A.
        var threePhase = new LiveSolarChargingController(
            new ChargePowerConverter(nominalVoltage: 230, phases: 3),
            minChargingCurrentAmps: 6, maxChargingCurrentAmps: 20, currentStepAmps: 1,
            hysteresisWatts: 200, fullSocPercent: 95, releaseSocPercent: 90);

        var result = threePhase.Decide(Input(96, EvChargerStatus.Charging, 6900, Charging10A, hasControl: true));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(10, result.TargetSettings!.ChargeCurrentAmps);
    }
}
