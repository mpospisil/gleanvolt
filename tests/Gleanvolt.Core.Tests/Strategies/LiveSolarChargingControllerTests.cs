using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

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

    private static EnergyState State(double socPercent) =>
        new(DateTimeOffset.UtcNow, socPercent, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0, EvChargerStatus.Charging, EvChargerPowerWatts: 0);

    private static ChargingControlInput Input(double soc, double surplus, EvChargerMode chargerMode, bool charging) =>
        new(State(soc), surplus, new EvChargerSettings(chargerMode, 6), charging);

    [Theory]
    [InlineData(EvChargerMode.Green)]
    [InlineData(EvChargerMode.Eco)]
    [InlineData(EvChargerMode.Stop)]
    public void NotFastMode_LeavesTheChargerAlone(EvChargerMode mode)
    {
        // Plenty of sun and a full battery, but the charger isn't in Fast: we don't control it.
        var result = Controller.Decide(Input(96, 5000, mode, charging: false));

        Assert.Equal(ChargingControlAction.None, result.Action);
    }

    [Fact]
    public void Fast_BatteryFull_SurplusAboveResumeThreshold_StartsCharging()
    {
        // Not charging, 96% >= 95%, surplus 4000W clears the resume threshold -> charge at
        // floor(4000/230) = 17A.
        var result = Controller.Decide(Input(96, 4000, EvChargerMode.Fast, charging: false));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(17, result.ChargeCurrentAmps);
    }

    [Fact]
    public void Fast_NotCharging_SurplusBetweenMinAndResume_Pauses()
    {
        // 1450W is above the min (1380) but below the resume threshold (1580); not charging -> stay paused.
        var result = Controller.Decide(Input(96, 1450, EvChargerMode.Fast, charging: false));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
    }

    [Fact]
    public void Fast_BatteryBelowFull_NotCharging_Pauses()
    {
        // 94% < 95% engage threshold: don't start, and (being in Fast) actively hold it paused.
        var result = Controller.Decide(Input(94, 5000, EvChargerMode.Fast, charging: false));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
    }

    [Fact]
    public void Fast_BatteryBelowRelease_WhileCharging_Pauses()
    {
        // Dropped to 89% (< 90% release) while charging: disengage and pause.
        var result = Controller.Decide(Input(89, 5000, EvChargerMode.Fast, charging: true));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
    }

    [Fact]
    public void Fast_BatteryBetweenReleaseAndFull_WhileCharging_KeepsCharging()
    {
        // 92% is below 95% engage but above 90% release: already charging, so hysteresis keeps going.
        var result = Controller.Decide(Input(92, 4000, EvChargerMode.Fast, charging: true));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(17, result.ChargeCurrentAmps);
    }

    // The 6A hard cutoff (evaluated on the smoothed surplus): at or above the floor keep charging,
    // below it pause -- never sit at the minimum drawing from the grid.
    [Theory]
    [InlineData(1380, ChargingControlAction.Charge)]
    [InlineData(1379, ChargingControlAction.Pause)]
    [InlineData(920, ChargingControlAction.Pause)]
    public void Fast_WhileCharging_SurplusAtTheSixAmpFloor(double surplusWatts, ChargingControlAction expected)
    {
        var result = Controller.Decide(Input(96, surplusWatts, EvChargerMode.Fast, charging: true));

        Assert.Equal(expected, result.Action);
    }

    [Fact]
    public void Fast_SurplusExceedsConfiguredMax_ClampsToMax()
    {
        var result = Controller.Decide(Input(96, 30000, EvChargerMode.Fast, charging: true));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(20, result.ChargeCurrentAmps);
    }

    [Fact]
    public void ConfiguredMaxAboveHardwareLimit_ClampsTo32A()
    {
        var overConfigured = new LiveSolarChargingController(
            new ChargePowerConverter(nominalVoltage: 230, phases: 1),
            minChargingCurrentAmps: 6, maxChargingCurrentAmps: 40, currentStepAmps: 1,
            hysteresisWatts: 200, fullSocPercent: 95, releaseSocPercent: 90);

        var result = overConfigured.Decide(Input(96, 30000, EvChargerMode.Fast, charging: true));

        Assert.Equal(EvChargerLimits.MaxCurrentAmps, result.ChargeCurrentAmps);
    }

    [Fact]
    public void ThreePhase_SurplusBelowThreePhaseFloor_Pauses()
    {
        // Three-phase: min = 6 * 230 * 3 = 4140W. 3000W clears the single-phase floor but not this one.
        var threePhase = new LiveSolarChargingController(
            new ChargePowerConverter(nominalVoltage: 230, phases: 3),
            minChargingCurrentAmps: 6, maxChargingCurrentAmps: 20, currentStepAmps: 1,
            hysteresisWatts: 200, fullSocPercent: 95, releaseSocPercent: 90);

        var result = threePhase.Decide(Input(96, 3000, EvChargerMode.Fast, charging: true));

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

        var result = threePhase.Decide(Input(96, 6900, EvChargerMode.Fast, charging: true));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(10, result.ChargeCurrentAmps);
    }

    [Fact]
    public void CloudCycle_ChargesThenPausesThenResumes()
    {
        // Charging on 4000W.
        var charging = Controller.Decide(Input(96, 4000, EvChargerMode.Fast, charging: true));
        Assert.Equal(ChargingControlAction.Charge, charging.Action);

        // Cloud drops it below the floor -> pause.
        var paused = Controller.Decide(Input(96, 1000, EvChargerMode.Fast, charging: true));
        Assert.Equal(ChargingControlAction.Pause, paused.Action);

        // Cloud clears; now not charging, surplus clears the resume threshold -> charge again.
        var resumed = Controller.Decide(Input(96, 4000, EvChargerMode.Fast, charging: false));
        Assert.Equal(ChargingControlAction.Charge, resumed.Action);
        Assert.Equal(17, resumed.ChargeCurrentAmps);
    }
}
