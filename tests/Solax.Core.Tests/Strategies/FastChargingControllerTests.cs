using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Core.Tests.Strategies;

public class FastChargingControllerTests
{
    // The reference install: 16A ceiling, a car counts as finished after two idle minutes.
    private static readonly FastChargingController Controller = new(
        maxChargingCurrentAmps: 16,
        completionDwell: TimeSpan.FromMinutes(2));

    private static EnergyState State(EvChargerStatus status, double soc = 50, double evPowerWatts = 0) =>
        new(DateTimeOffset.UtcNow, soc, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0, status, evPowerWatts);

    private static ChargingControlInput Input(
        EvChargerMode chargerMode = EvChargerMode.Fast,
        double soc = 50,
        double surplus = 0,
        EvChargerStatus status = EvChargerStatus.Charging,
        bool evDrewPower = true,
        TimeSpan evIdleFor = default) =>
        new(State(status, soc), surplus, new EvChargerSettings(chargerMode, 6), Charging: true,
            EvDrewPower: evDrewPower, EvIdleFor: evIdleFor);

    [Theory]
    [InlineData(EvChargerMode.Green)]
    [InlineData(EvChargerMode.Eco)]
    [InlineData(EvChargerMode.Stop)]
    public void NotFastMode_LeavesTheChargerAlone(EvChargerMode mode)
    {
        var result = Controller.Decide(Input(chargerMode: mode));

        Assert.Equal(ChargingControlAction.None, result.Action);
        Assert.False(result.SessionComplete);
    }

    [Fact]
    public void NotFastMode_DoesNotEndTheSessionEither()
    {
        // The car is long gone, but we were never driving this session -- ending the mode on the
        // strength of a charger we don't control would be presumptuous.
        var result = Controller.Decide(Input(
            chargerMode: EvChargerMode.Green, status: EvChargerStatus.Available, evIdleFor: TimeSpan.FromHours(1)));

        Assert.Equal(ChargingControlAction.None, result.Action);
        Assert.False(result.SessionComplete);
    }

    [Fact]
    public void ChargesAtTheConfiguredMaximum()
    {
        var result = Controller.Decide(Input());

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(16, result.ChargeCurrentAmps);
        Assert.Equal(0, result.LoanPowerWatts);
    }

    [Theory]
    [InlineData(0, 0)]      // flat battery, no sun
    [InlineData(100, 8000)] // full battery, plenty of sun
    [InlineData(20, -3000)] // the house is importing hard
    public void IgnoresSocAndSurplusEntirely(double soc, double surplus)
    {
        var result = Controller.Decide(Input(soc: soc, surplus: surplus));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(16, result.ChargeCurrentAmps);
    }

    [Theory]
    [InlineData(40, 32)] // above what the hardware accepts
    [InlineData(2, 6)]   // below the IEC minimum
    public void TheCeilingIsClampedToWhatTheHardwareAccepts(int configured, int expected)
    {
        var controller = new FastChargingController(configured, TimeSpan.FromMinutes(2));

        Assert.Equal(expected, controller.Decide(Input()).ChargeCurrentAmps);
    }

    [Fact]
    public void AnIdleCarThatHasNeverDrawn_KeepsCharging()
    {
        // Plugged in but not started (Preparing, or waiting on its own timer). Calling this "finished"
        // would switch the mode off seconds after it was selected.
        var result = Controller.Decide(Input(
            status: EvChargerStatus.Preparing, evDrewPower: false, evIdleFor: TimeSpan.FromHours(1)));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.False(result.SessionComplete);
    }

    [Fact]
    public void AnIdleCarBelowTheDwell_KeepsCharging()
    {
        var result = Controller.Decide(Input(evIdleFor: TimeSpan.FromSeconds(119)));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.False(result.SessionComplete);
    }

    [Fact]
    public void AnIdleCarPastTheDwell_EndsTheSession()
    {
        var result = Controller.Decide(Input(
            status: EvChargerStatus.SuspendedEv, evIdleFor: TimeSpan.FromMinutes(2)));

        // Pause, not None: the charger is left idle rather than armed at 16A for whatever plugs in next.
        Assert.Equal(ChargingControlAction.Pause, result.Action);
        Assert.True(result.SessionComplete);
        Assert.Null(result.ChargeCurrentAmps);
    }

    [Fact]
    public void UnpluggingEndsTheSessionWithoutWaiting()
    {
        var result = Controller.Decide(Input(status: EvChargerStatus.Available));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
        Assert.True(result.SessionComplete);
    }

    [Fact]
    public void NoCarAtAll_KeepsChargingUntilOneArrives()
    {
        // The mode selected before the car is plugged in: it waits, it doesn't switch itself off.
        var result = Controller.Decide(Input(status: EvChargerStatus.Available, evDrewPower: false));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.False(result.SessionComplete);
    }
}
