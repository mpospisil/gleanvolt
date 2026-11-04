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
        releaseSocPercent: 90,
        completionDwell: TimeSpan.FromMinutes(2));

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
    public void Fast_NotCharging_SurplusBetweenMinAndResume_Waits()
    {
        // 1450W is above the min (1380) but below the resume threshold (1580); not charging -> stay paused.
        var result = Controller.Decide(Input(96, 1450, EvChargerMode.Fast, charging: false));

        Assert.Equal(ChargingControlAction.StandDown, result.Action);
    }

    [Fact]
    public void Fast_BatteryBelowFull_NotCharging_Waits()
    {
        // 94% < 95% engage threshold: don't start, and (being in Fast) actively hold it paused.
        var result = Controller.Decide(Input(94, 5000, EvChargerMode.Fast, charging: false));

        Assert.Equal(ChargingControlAction.StandDown, result.Action);
    }

    [Fact]
    public void Fast_BatteryBelowRelease_WhileCharging_Waits()
    {
        // Dropped to 89% (< 90% release) while charging: disengage and pause.
        var result = Controller.Decide(Input(89, 5000, EvChargerMode.Fast, charging: true));

        Assert.Equal(ChargingControlAction.StandDown, result.Action);
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
    // below it wait (stood down: with no forecast nobody knows for how long) -- never sit at the minimum
    // drawing from the grid.
    [Theory]
    [InlineData(1380, ChargingControlAction.Charge)]
    [InlineData(1379, ChargingControlAction.StandDown)]
    [InlineData(920, ChargingControlAction.StandDown)]
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
            hysteresisWatts: 200, fullSocPercent: 95, releaseSocPercent: 90, completionDwell: TimeSpan.FromMinutes(2));

        var result = overConfigured.Decide(Input(96, 30000, EvChargerMode.Fast, charging: true));

        Assert.Equal(EvChargerLimits.MaxCurrentAmps, result.ChargeCurrentAmps);
    }

    [Fact]
    public void ThreePhase_SurplusBelowThreePhaseFloor_Waits()
    {
        // Three-phase: min = 6 * 230 * 3 = 4140W. 3000W clears the single-phase floor but not this one.
        var threePhase = new LiveSolarChargingController(
            new ChargePowerConverter(nominalVoltage: 230, phases: 3),
            minChargingCurrentAmps: 6, maxChargingCurrentAmps: 20, currentStepAmps: 1,
            hysteresisWatts: 200, fullSocPercent: 95, releaseSocPercent: 90, completionDwell: TimeSpan.FromMinutes(2));

        var result = threePhase.Decide(Input(96, 3000, EvChargerMode.Fast, charging: true));

        Assert.Equal(ChargingControlAction.StandDown, result.Action);
    }

    [Fact]
    public void ThreePhase_AmpsUsePhaseAwareConversion()
    {
        // Three-phase: 6900W surplus -> 6900 / (230*3) = 10A.
        var threePhase = new LiveSolarChargingController(
            new ChargePowerConverter(nominalVoltage: 230, phases: 3),
            minChargingCurrentAmps: 6, maxChargingCurrentAmps: 20, currentStepAmps: 1,
            hysteresisWatts: 200, fullSocPercent: 95, releaseSocPercent: 90, completionDwell: TimeSpan.FromMinutes(2));

        var result = threePhase.Decide(Input(96, 6900, EvChargerMode.Fast, charging: true));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(10, result.ChargeCurrentAmps);
    }

    [Fact]
    public void CloudCycle_ChargesThenWaitsThenResumes()
    {
        // Charging on 4000W.
        var charging = Controller.Decide(Input(96, 4000, EvChargerMode.Fast, charging: true));
        Assert.Equal(ChargingControlAction.Charge, charging.Action);

        // Cloud drops it below the floor -> wait, stood down: with no forecast nobody knows for how long.
        var paused = Controller.Decide(Input(96, 1000, EvChargerMode.Fast, charging: true));
        Assert.Equal(ChargingControlAction.StandDown, paused.Action);

        // Cloud clears; now not charging, surplus clears the resume threshold -> charge again.
        var resumed = Controller.Decide(Input(96, 4000, EvChargerMode.Fast, charging: false));
        Assert.Equal(ChargingControlAction.Charge, resumed.Action);
        Assert.Equal(17, resumed.ChargeCurrentAmps);
    }

    // -- An aging pack that stops short of the gate: in the band, taking nothing, sun to spare.

    private static readonly DateTimeOffset Noon = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private static LiveSolarChargingController NewController() => new(
        new ChargePowerConverter(nominalVoltage: 230, phases: 1),
        minChargingCurrentAmps: 6, maxChargingCurrentAmps: 20, currentStepAmps: 1,
        hysteresisWatts: 200, fullSocPercent: 95, releaseSocPercent: 90, completionDwell: TimeSpan.FromMinutes(2),
        stallDwell: TimeSpan.FromMinutes(10), stallWatts: 100);

    private static ChargingControlInput At(TimeSpan sinceNoon, double soc, double batteryWatts, double surplus) =>
        new(new EnergyState(Noon + sinceNoon, soc, batteryWatts, SolarPowerWatts: 6000, GridPowerWatts: -4000, EvChargerStatus.Charging, EvChargerPowerWatts: 0),
            surplus, new EvChargerSettings(EvChargerMode.Fast, 6), Charging: false);

    // Polls every 10 s from noon for the given span; returns the last decision.
    private static ChargingControlDecision Run(
        LiveSolarChargingController controller, TimeSpan span, double soc, double batteryWatts, double surplus, TimeSpan? from = null)
    {
        ChargingControlDecision last = null!;
        for (var t = from ?? TimeSpan.Zero; t <= (from ?? TimeSpan.Zero) + span; t += TimeSpan.FromSeconds(10))
        {
            last = controller.Decide(At(t, soc, batteryWatts, surplus));
        }

        return last;
    }

    [Fact]
    public void StalledInTheBand_ForTheDwell_CountsAsFull()
    {
        var result = Run(NewController(), TimeSpan.FromMinutes(10), soc: 93, batteryWatts: 0, surplus: 4000);

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(17, result.ChargeCurrentAmps);
        Assert.Contains("stopped taking charge", result.Reason);
    }

    [Fact]
    public void StalledInTheBand_ShortOfTheDwell_StillWaits()
    {
        var result = Run(NewController(), TimeSpan.FromMinutes(9), soc: 93, batteryWatts: 0, surplus: 4000);

        Assert.Equal(ChargingControlAction.StandDown, result.Action);
    }

    [Fact]
    public void BatteryStillCharging_InTheBand_Waits()
    {
        // A healthy pack at 93% is still taking the sun: it will reach 95% on its own.
        var result = Run(NewController(), TimeSpan.FromMinutes(30), soc: 93, batteryWatts: 2500, surplus: 4000);

        Assert.Equal(ChargingControlAction.StandDown, result.Action);
    }

    [Fact]
    public void BelowTheBand_NeverCountsAsFull()
    {
        // 85% taking nothing is not a stalled-full pack: it is a battery the inverter is not charging.
        var result = Run(NewController(), TimeSpan.FromMinutes(30), soc: 85, batteryWatts: 0, surplus: 4000);

        Assert.Equal(ChargingControlAction.StandDown, result.Action);
    }

    [Fact]
    public void NoSunToStartTheCar_IsNotAStall()
    {
        // Battery at 0 W because there is nothing to charge it with -- the car could not start either.
        var result = Run(NewController(), TimeSpan.FromMinutes(30), soc: 93, batteryWatts: 0, surplus: 1000);

        Assert.Equal(ChargingControlAction.StandDown, result.Action);
    }

    [Fact]
    public void BatteryTakingChargeAgain_RestartsTheDwell()
    {
        var controller = NewController();
        Run(controller, TimeSpan.FromMinutes(8), soc: 93, batteryWatts: 0, surplus: 4000);
        controller.Decide(At(TimeSpan.FromMinutes(8.5), soc: 93, batteryWatts: 1500, surplus: 4000));

        var result = Run(controller, TimeSpan.FromMinutes(8), soc: 93, batteryWatts: 0, surplus: 4000, from: TimeSpan.FromMinutes(9));

        Assert.Equal(ChargingControlAction.StandDown, result.Action);
    }

    [Fact]
    public void AGapBetweenPolls_RestartsTheDwell()
    {
        // Eight minutes stalled, then the mode was not driving for an hour: the old run says nothing now.
        var controller = NewController();
        Run(controller, TimeSpan.FromMinutes(8), soc: 93, batteryWatts: 0, surplus: 4000);

        var result = Run(controller, TimeSpan.FromMinutes(5), soc: 93, batteryWatts: 0, surplus: 4000, from: TimeSpan.FromMinutes(68));

        Assert.Equal(ChargingControlAction.StandDown, result.Action);
    }
}
