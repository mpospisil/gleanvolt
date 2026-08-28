using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

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
        TimeSpan evIdleFor = default,
        FastChargeProgress? fastCharge = null) =>
        new(State(status, soc), surplus, new EvChargerSettings(chargerMode, 6), Charging: true,
            EvDrewPower: evDrewPower, EvIdleFor: evIdleFor, FastCharge: fastCharge);

    /// <summary>A limit of <paramref name="requiredWh"/> with <paramref name="deliveredWh"/> against it.</summary>
    private static FastChargeProgress Progress(double requiredWh, double deliveredWh) =>
        new(new FastChargeLimit(requiredWh, DateTimeOffset.UnixEpoch), deliveredWh);

    /// <summary>A deferred charge, planned against a fixed clock so the branch under test is the only variable.</summary>
    private static FastChargeProgress Deferred(
        DateTimeOffset now,
        DateTimeOffset departBy,
        double requiredWh = 30_000,
        double deliveredWh = 0)
    {
        var limit = new FastChargeLimit(requiredWh, now, DepartBy: departBy);

        return new FastChargeProgress(
            limit,
            deliveredWh,
            FastChargePlanner.Plan(limit, deliveredWh, null, 11_040, TimeSpan.FromMinutes(15), now));
    }

    private static ChargingControlInput At(
        DateTimeOffset now,
        FastChargeProgress progress,
        bool evDrewPower = true,
        bool charging = true,
        TimeSpan evIdleFor = default) =>
        new(
            new EnergyState(now, 50, 0, 0, 0, EvChargerStatus.Charging, 0),
            0,
            new EvChargerSettings(EvChargerMode.Fast, 6),
            Charging: charging,
            EvDrewPower: evDrewPower,
            EvIdleFor: evIdleFor,
            FastCharge: progress);

    [Fact]
    public void TheMomentAWaitedStartArrives_TheCarIsNotMistakenForAFinishedOne()
    {
        // Observed on 2026-08-28: 5kWh asked for at 07:06 to be ready by 08:15, planned to start at
        // 07:47. The car drew for a few seconds at activation, the plan then held it at the pause
        // current for 41 minutes, and on the poll that ended the wait the mode reported "car stopped
        // drawing for 41 min (charge limit reached) at 0.0kWh of the 5.0kWh asked for" and returned to
        // Off -- ending itself at the exact second it was due to begin. Nothing was delivered.
        //
        // The idle clock ran through a pause we commanded, so it must not be read as the car's verdict:
        // Charging is false for every one of those polls, because pausing is what set it false.
        // Departing 08:30 gives ready-by 08:15, and 5kWh at the wallbox needs about half an hour, so
        // the plan defers the start to roughly 07:47 -- the figures the controller logged on the day.
        var planned = new DateTimeOffset(2026, 8, 28, 7, 6, 30, TimeSpan.FromHours(2));
        var deferred = Deferred(planned, departBy: planned.AddMinutes(84), requiredWh: 5000);

        // Evaluated once the appointment has arrived, which is the poll that used to end the mode.
        var appointment = deferred.Plan!.StartNoLaterThan.AddSeconds(30);
        var waited = appointment - planned;

        var result = Controller.Decide(
            At(appointment, deferred, charging: false, evIdleFor: waited));

        Assert.False(result.SessionComplete);
        Assert.Equal(ChargingControlAction.Charge, result.Action);
    }

    [Fact]
    public void ACarThatStopsWhileWeAreOfferingPower_StillEndsTheCharge()
    {
        // The guard must not cost the mode its actual purpose: while we are charging, a car idle past
        // the dwell has reached its own limit and the session is over.
        var result = Controller.Decide(Input(
            evDrewPower: true,
            evIdleFor: TimeSpan.FromMinutes(3),
            fastCharge: Progress(requiredWh: 30_000, deliveredWh: 12_000)));

        Assert.True(result.SessionComplete);
    }

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
    public void ADroppedChargerReading_DoesNotEndTheSession()
    {
        // Unknown is a charger that didn't answer, not a car that left. Ending the mode on one costs
        // the owner the fast charge they asked for, and nothing brings it back by itself.
        var result = Controller.Decide(Input(status: EvChargerStatus.Unknown));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.False(result.SessionComplete);
    }

    [Fact]
    public void NoCarAtAll_KeepsChargingUntilOneArrives()
    {
        // The mode selected before the car is plugged in: it waits, it doesn't switch itself off.
        var result = Controller.Decide(Input(status: EvChargerStatus.Available, evDrewPower: false));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.False(result.SessionComplete);
    }

    // -- The amount (#119).

    [Fact]
    public void WithoutALimitItChargesOnAndSaysNothingAboutAnAmount()
    {
        var result = Controller.Decide(Input());

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.False(result.SessionComplete);
        Assert.DoesNotContain("delivered", result.Reason);
    }

    [Fact]
    public void ShortOfTheLimitItChargesOnAndReportsHowFarAlongItIs()
    {
        var result = Controller.Decide(Input(fastCharge: Progress(20_000, 8_000)));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(16, result.ChargeCurrentAmps);
        Assert.False(result.SessionComplete);
        Assert.Contains("8.0kWh of 20.0kWh delivered", result.Reason);
        Assert.Contains("12.0kWh to go", result.Reason);
    }

    [Fact]
    public void TheLimitBeingMetEndsTheSession()
    {
        var result = Controller.Decide(Input(fastCharge: Progress(20_000, 20_100)));

        Assert.True(result.SessionComplete);
        Assert.Equal(ChargingControlAction.Pause, result.Action);
        Assert.Contains("Fast target reached", result.Reason);
        Assert.Contains("returning to Off", result.Reason);
    }

    [Fact]
    public void TheLimitIsCheckedBeforeTheIdleDwell()
    {
        // A car that stops drawing at the very moment it reaches the number: both branches would fire,
        // and only one of them is true. Downstream they are indistinguishable once the mode reads Off.
        var result = Controller.Decide(Input(
            status: EvChargerStatus.SuspendedEv,
            evIdleFor: TimeSpan.FromMinutes(30),
            fastCharge: Progress(20_000, 20_000)));

        Assert.True(result.SessionComplete);
        Assert.Contains("Fast target reached", result.Reason);
        Assert.DoesNotContain("charge limit reached", result.Reason);
    }

    [Fact]
    public void ACarThatStopsShortSaysHowFarShortItGot()
    {
        var result = Controller.Decide(Input(
            evIdleFor: TimeSpan.FromMinutes(3),
            fastCharge: Progress(20_000, 12_400)));

        Assert.True(result.SessionComplete);
        Assert.Contains("charge limit reached", result.Reason);
        Assert.Contains("12.4kWh of the 20.0kWh asked for", result.Reason);
    }

    [Fact]
    public void ALimitDoesNotOverrideTheNotFastPrecondition()
    {
        // A charger taken out of Fast at the wallbox is not ours to end, met limit or not.
        var result = Controller.Decide(Input(
            chargerMode: EvChargerMode.Green, fastCharge: Progress(20_000, 25_000)));

        Assert.Equal(ChargingControlAction.None, result.Action);
        Assert.False(result.SessionComplete);
    }

    // -- The departure (#122). It changes when the charge runs, never how.

    [Fact]
    public void BeforeTheStartTimeItWaitsInsteadOfCharging()
    {
        var now = new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero);
        var result = Controller.Decide(At(now, Deferred(now, now.AddHours(9))));

        Assert.Equal(ChargingControlAction.Pause, result.Action);
        Assert.False(result.SessionComplete);
        Assert.Contains("Waiting until", result.Reason);
    }

    [Fact]
    public void WaitingIsCheckedBeforeTheIdleDwell()
    {
        // The bug this ordering exists to prevent: a car that drew power earlier in the session and is
        // now being held back draws nothing, so the completion dwell would fire and end the very charge
        // the plan exists to schedule.
        var now = new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero);

        var result = Controller.Decide(At(now, Deferred(now, now.AddHours(9))) with
        {
            State = new EnergyState(now, 50, 0, 0, 0, EvChargerStatus.SuspendedEv, 0),
            EvIdleFor = TimeSpan.FromHours(3),
        });

        Assert.Equal(ChargingControlAction.Pause, result.Action);
        Assert.False(result.SessionComplete);
        Assert.Contains("Waiting until", result.Reason);
    }

    [Fact]
    public void AtTheStartTimeItChargesAtTheMaximumLikeAnyOtherFastCharge()
    {
        var now = new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero);
        var result = Controller.Decide(At(now, Deferred(now, now.AddHours(2))));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Equal(16, result.ChargeCurrentAmps);
    }

    [Fact]
    public void TooLittleTimeChargesNowAndSaysHowFarShortItWillFall()
    {
        var now = new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero);
        var result = Controller.Decide(At(now, Deferred(now, now.AddHours(2))));

        Assert.Equal(ChargingControlAction.Charge, result.Action);
        Assert.Contains("Not enough time", result.Reason);
        Assert.Contains("kWh of it will not fit", result.Reason);
    }

    [Fact]
    public void ThePassedDepartureEndsTheMode()
    {
        var planned = new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero);
        var progress = Deferred(planned, planned.AddHours(9), deliveredWh: 28_000);

        var result = Controller.Decide(At(planned.AddHours(10), progress));

        Assert.True(result.SessionComplete);
        Assert.Contains("has passed", result.Reason);
        Assert.Contains("28.0kWh of 30.0kWh", result.Reason);
    }

    [Fact]
    public void AMetTargetBeatsAPassedDeparture()
    {
        // A charge that lands exactly on the deadline succeeded; it did not run out of time.
        var planned = new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero);
        var progress = Deferred(planned, planned.AddHours(9), deliveredWh: 30_000);

        var result = Controller.Decide(At(planned.AddHours(10), progress));

        Assert.True(result.SessionComplete);
        Assert.Contains("Fast target reached", result.Reason);
        Assert.DoesNotContain("has passed", result.Reason);
    }

    [Fact]
    public void ADeferredChargeStillHonoursTheNotFastPrecondition()
    {
        var now = new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero);

        var result = Controller.Decide(At(now, Deferred(now, now.AddHours(9))) with
        {
            CurrentSettings = new EvChargerSettings(EvChargerMode.Green, 6),
        });

        Assert.Equal(ChargingControlAction.None, result.Action);
    }
}
