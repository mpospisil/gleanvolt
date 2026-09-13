using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class SolarGridChargingControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    // 230V x 3 phases, 6-16A, 1A steps, 200W resume hysteresis, 2kW minimum surplus.
    // One amp is 690W, so the floor is 6A = 4140W and a 2kW surplus bridges 2140W from the grid.
    private static readonly SolarGridChargingController Controller = new(
        new ChargePowerConverter(nominalVoltage: 230, phases: 3),
        new SolarGridChargingOptions(
            MinChargingCurrentAmps: 6,
            MaxChargingCurrentAmps: 16,
            CurrentStepAmps: 1,
            ResumeHysteresisWatts: 200,
            MinRunTime: TimeSpan.FromMinutes(10),
            MinPauseTime: TimeSpan.FromMinutes(15),
            CompletionDwell: TimeSpan.FromMinutes(2),
            MinSurplusWatts: 2000));

    private static readonly SolarGridOutlook SunNow =
        new(2000, Now, ForecastUsable: true, NextSunAt: Now, SunUntil: Now.AddHours(4), "sunny");

    private static readonly SolarGridOutlook NoSunLeft =
        new(2000, Now, ForecastUsable: true, NextSunAt: null, SunUntil: null, "no sun left");

    private static ChargingControlInput Input(
        double surplus,
        bool charging = false,
        SolarGridOutlook? outlook = null,
        EvChargerMode chargerMode = EvChargerMode.Fast,
        TimeSpan timeInState = default,
        bool evDrewPower = false,
        TimeSpan evIdleFor = default,
        EvChargerStatus status = EvChargerStatus.Charging,
        bool stoodDown = false,
        bool chargedThisMode = false) =>
        new(
            new EnergyState(Now, 60, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0, status, EvChargerPowerWatts: 0),
            surplus,
            new EvChargerSettings(chargerMode, 6),
            charging,
            TimeInCurrentState: timeInState,
            EvDrewPower: evDrewPower,
            EvIdleFor: evIdleFor,
            ChargerStoodDown: stoodDown,
            SolarGrid: outlook ?? SunNow,
            ChargedThisMode: chargedThisMode);

    [Theory]
    [InlineData(EvChargerMode.Green)]
    [InlineData(EvChargerMode.Eco)]
    [InlineData(EvChargerMode.Stop)]
    public void NotFast_LeavesTheChargerAlone(EvChargerMode mode)
    {
        var result = Controller.Decide(Input(6000, chargerMode: mode));

        Assert.Equal(ChargingControlAction.None, result.Action);
    }

    [Fact]
    public void ASurplusOverTheMinimumButUnderTheFloor_IsBridgedFromTheGridToTheFloor()
    {
        // 3000W clears the 2200W start threshold but not the 4140W floor: charge at 6A, grid pays 1140W.
        var result = Controller.Decide(Input(3000));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(6, result.ChargeCurrentAmps);
        Assert.Equal(1140, result.GridBridgeWatts, precision: 0);
        Assert.Contains("from the grid", result.Reason);
    }

    [Fact]
    public void ASurplusOverTheFloor_FollowsTheSunWithNothingFromTheGrid()
    {
        // floor(6000 / 690) = 8A = 5520W, all of it covered by the sun.
        var result = Controller.Decide(Input(6000));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(8, result.ChargeCurrentAmps);
        Assert.Equal(0, result.GridBridgeWatts);
    }

    [Fact]
    public void ALargeSurplus_IsClampedToTheMaximum()
    {
        var result = Controller.Decide(Input(20_000));

        Assert.Equal(16, result.ChargeCurrentAmps);
        Assert.Equal(0, result.GridBridgeWatts);
    }

    [Fact]
    public void APausedCar_WaitsForTheHysteresisMarginAboveTheMinimum()
    {
        // 2100W is over the 2000W minimum but under the 2200W start threshold.
        var result = Controller.Decide(Input(2100));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
        Assert.Contains("start threshold", result.Reason);
    }

    [Fact]
    public void ARunningCar_ContinuesDownToTheMinimumItself()
    {
        var result = Controller.Decide(Input(2050, charging: true, timeInState: TimeSpan.FromHours(1)));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(6, result.ChargeCurrentAmps);
        Assert.Equal(2090, result.GridBridgeWatts, precision: 0);
    }

    [Fact]
    public void ADipInsideTheMinimumRunTime_HoldsTheFloor_AndReportsTheWholeGapAsABridge()
    {
        // Held rather than stopped -- and the gap must be reported, or the hold would not arm and the
        // pack would pay for the minutes at 6A.
        var result = Controller.Decide(Input(1500, charging: true, timeInState: TimeSpan.FromMinutes(5)));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(6, result.ChargeCurrentAmps);
        Assert.Equal(2640, result.GridBridgeWatts, precision: 0);
        Assert.Contains("minimum run time", result.Reason);
    }

    [Fact]
    public void ADipAfterTheMinimumRunTime_WithSunStillForecast_Pauses()
    {
        var result = Controller.Decide(Input(1500, charging: true, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
        Assert.False(result.SessionComplete);
        Assert.Contains("waiting for this dip to pass", result.Reason);
    }

    [Fact]
    public void NoSunLeftInTheForecast_AndNoneLive_EndsTheMode()
    {
        var result = Controller.Decide(Input(500, outlook: NoSunLeft));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
        Assert.True(result.SessionComplete);
        Assert.Contains("returning to Off", result.Reason);
    }

    [Fact]
    public void TheForecastNeverTurnsAwayRealSun()
    {
        // The forecast wrote the rest of the day off, but the roof disagrees: the live gate decides.
        var result = Controller.Decide(Input(5000, outlook: NoSunLeft));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.False(result.SessionComplete);
    }

    [Fact]
    public void TheEndWaitsForTheMinimumRunTime()
    {
        // A running charge still gets its minutes at the floor before the forecast may end it.
        var result = Controller.Decide(Input(500, charging: true, outlook: NoSunLeft, timeInState: TimeSpan.FromMinutes(3)));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.False(result.SessionComplete);
    }

    [Fact]
    public void SunForecastHoursAway_StandsTheChargerDown()
    {
        var later = SunNow with { NextSunAt = Now.AddHours(2), SunUntil = Now.AddHours(6) };

        var result = Controller.Decide(Input(0, outlook: later));

        Assert.Equal(ChargingControlAction.StandDown, result.Action);
        Assert.False(result.SessionComplete);
    }

    [Fact]
    public void SunForecastMinutesAway_OnlyPauses()
    {
        var soon = SunNow with { NextSunAt = Now.AddMinutes(5) };

        var result = Controller.Decide(Input(0, outlook: soon));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
    }

    [Fact]
    public void AStoodDownChargerIsRearmedByRealSun()
    {
        // The charger reads Stop because we put it there; sun arriving is a charge, which the
        // coordinator turns back into Fast.
        var result = Controller.Decide(Input(5000, chargerMode: EvChargerMode.Stop, stoodDown: true));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
    }

    [Fact]
    public void NoUsableForecast_WaitsOnLiveSurplus_AndNeverEndsOnIt()
    {
        var unusable = SolarGridOutlook.Unavailable(2000, Now, "no forecast fetched yet");

        var result = Controller.Decide(Input(0, outlook: unusable));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
        Assert.False(result.SessionComplete);
        Assert.Contains("No usable forecast", result.Reason);
    }

    [Fact]
    public void TheRestartDwellAppliesOnceThisModeHasCharged()
    {
        var result = Controller.Decide(Input(5000, timeInState: TimeSpan.FromMinutes(5), evDrewPower: true, chargedThisMode: true));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
        Assert.Contains("minimum before restarting", result.Reason);
    }

    [Fact]
    public void ACarTheChargerStartedBeforeTheModeWasPicked_IsNotMadeToWaitForARestart()
    {
        // 2026-09-13: the car had drawn power -- the charger started it at plug-in -- but not under this
        // mode, and the first decision stopped it for 15 minutes with the surplus over the threshold.
        var result = Controller.Decide(Input(2300, evDrewPower: true, chargedThisMode: false));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(6, result.ChargeCurrentAmps);
    }

    [Fact]
    public void TheRestartDwellDoesNotDelayTheFirstCharge()
    {
        // Nothing has been drawn yet, so there is nothing to restart: a dwell here would count from the
        // button press and waste the sun that is there now.
        var result = Controller.Decide(Input(5000, timeInState: TimeSpan.FromMinutes(1), evDrewPower: false));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
    }

    [Fact]
    public void ACarThatStopsDrawingWhileAskedToCharge_EndsTheMode()
    {
        var result = Controller.Decide(Input(
            5000, charging: true, timeInState: TimeSpan.FromHours(1), evDrewPower: true, evIdleFor: TimeSpan.FromMinutes(3)));

        Assert.True(result.SessionComplete);
        Assert.Contains("stopped drawing", result.Reason);
    }

    [Fact]
    public void AnUnpluggedCar_EndsTheMode()
    {
        var result = Controller.Decide(Input(
            5000, charging: true, timeInState: TimeSpan.FromHours(1), evDrewPower: true, status: EvChargerStatus.Available));

        Assert.True(result.SessionComplete);
        Assert.Contains("unplugged", result.Reason);
    }

    [Fact]
    public void TheOutlooksMinimumIsTheOneInForce()
    {
        // The owner raised the minimum to 3kW at runtime; the configured 2kW must not let 2.5kW start.
        var raised = SunNow with { MinSurplusWatts = 3000 };

        var result = Controller.Decide(Input(2500, outlook: raised));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
        Assert.Contains("3200W", result.Reason);
    }

    [Fact]
    public void AMinimumAtTheFloor_NeverTakesAnythingFromTheGrid()
    {
        var atFloor = SunNow with { MinSurplusWatts = 4140 };

        var result = Controller.Decide(Input(4200, charging: true, outlook: atFloor, timeInState: TimeSpan.FromHours(1)));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(6, result.ChargeCurrentAmps);
        Assert.Equal(0, result.GridBridgeWatts);
    }
}
