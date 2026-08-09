using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Core.Tests.Strategies;

public class ChargingSessionTrackerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(30);

    private static ChargeControlStatus Status(
        DateTimeOffset at,
        ChargeControlMode mode = ChargeControlMode.Solar,
        ChargeControlState state = ChargeControlState.Charging,
        bool carConnected = true,
        double evWatts = 4000,
        double solarWatts = 6000,
        double gridWatts = 0,
        double batteryWatts = 0,
        double soc = 80,
        int? targetAmps = 6,
        int? activeAmps = 6,
        bool holdActive = false,
        double loanWatts = 0,
        SolarDayPlan? plan = null,
        bool sessionCompleted = false) => new(
        Mode: mode,
        DryRun: false,
        HoldingControl: state is ChargeControlState.Charging or ChargeControlState.Paused,
        State: state,
        SurplusWatts: 4200,
        TargetCurrentAmps: targetAmps,
        ActiveCurrentAmps: activeAmps,
        BatterySocPercent: soc,
        ChargerStatus: carConnected ? EvChargerStatus.Charging : EvChargerStatus.Available,
        CarConnected: carConnected,
        SolarPowerWatts: solarWatts,
        EvChargerPowerWatts: evWatts,
        EvChargingCurrentAmps: 6,
        BatteryPowerWatts: batteryWatts,
        GridPowerWatts: gridWatts,
        BatteryHoldEnabled: true,
        BatteryHoldRequested: false,
        BatteryHoldActive: holdActive,
        BatteryHoldTargetWatts: holdActive ? 1500 : null,
        Plan: plan,
        LoanPowerWatts: loanWatts,
        SessionEnergyWh: 0,
        LoanedTodayWh: 0,
        TomorrowForecastWh: null,
        Timestamp: at,
        SessionCompleted: sessionCompleted);

    private static ChargingSessionTracker NewTracker(bool recordUncontrolled = false) =>
        new(Cadence, recordUncontrolled, TimeProvider.System);

    [Fact]
    public void NothingIsRecordedWhileNoModeIsDrivingTheCharger()
    {
        var tracker = NewTracker();

        var update = tracker.Observe(Status(Noon, mode: ChargeControlMode.Off, state: ChargeControlState.Disabled));

        Assert.True(update.IsEmpty);
        Assert.Null(tracker.OpenSession);
    }

    [Fact]
    public void NothingIsRecordedWhileNoCarIsPluggedIn()
    {
        var tracker = NewTracker();

        var update = tracker.Observe(Status(Noon, carConnected: false, evWatts: 0));

        Assert.True(update.IsEmpty);
    }

    [Fact]
    public void AControllingModeOnAConnectedCarOpensASession()
    {
        var tracker = NewTracker();

        var update = tracker.Observe(Status(Noon, mode: ChargeControlMode.Forecasted));

        Assert.NotNull(update.Started);
        Assert.Equal(ChargeControlMode.Forecasted, update.Started!.StartMode);
        Assert.Equal(Noon, update.Started.StartedAt);
        Assert.True(update.Started.Controlled);
        Assert.True(update.Started.IsOpen);
        Assert.NotNull(update.Sample);
        Assert.Contains(update.Events, e => e.Kind == ChargingSessionEventKind.SessionStarted);
    }

    [Fact]
    public void SamplesAreStoredOnTheCadenceRatherThanOnEveryPoll()
    {
        var tracker = NewTracker();
        tracker.Observe(Status(Noon));

        // Five-second polls, as configured in production, across one 30-second cadence window.
        var stored = 0;
        for (var i = 1; i <= 6; i++)
        {
            var update = tracker.Observe(Status(Noon.AddSeconds(5 * i)));
            if (update.Sample is not null)
            {
                stored++;
            }
        }

        Assert.Equal(1, stored);
    }

    [Fact]
    public void AChangeForcesASampleEvenInsideTheCadenceWindow()
    {
        var tracker = NewTracker();
        tracker.Observe(Status(Noon, targetAmps: 6));

        // Five seconds later -- far inside the 30-second window, but the setpoint moved.
        var update = tracker.Observe(Status(Noon.AddSeconds(5), targetAmps: 10));

        Assert.NotNull(update.Sample);
        Assert.Equal(10, update.Sample!.TargetCurrentAmps);
        Assert.Contains(update.Events, e => e.Kind == ChargingSessionEventKind.TargetCurrentChanged);
    }

    [Fact]
    public void SwitchingModeMidSessionRecordsAnEventRatherThanSplittingTheSession()
    {
        var tracker = NewTracker();
        var opened = tracker.Observe(Status(Noon, mode: ChargeControlMode.Forecasted)).Started!;

        var update = tracker.Observe(Status(Noon.AddMinutes(5), mode: ChargeControlMode.FastNoBattery));

        Assert.Null(update.Started);
        Assert.Null(update.Ended);
        Assert.Equal(opened.Id, tracker.OpenSession!.Id);
        Assert.Contains(update.Events, e => e.Kind == ChargingSessionEventKind.ModeChanged);
    }

    [Fact]
    public void TheModeInForceAtTheEndIsTheOneRecordedAsTheEndMode()
    {
        var tracker = NewTracker();
        tracker.Observe(Status(Noon, mode: ChargeControlMode.Forecasted));
        tracker.Observe(Status(Noon.AddMinutes(1), mode: ChargeControlMode.FastNoBattery));

        var ended = tracker.Observe(Status(Noon.AddMinutes(2), mode: ChargeControlMode.FastNoBattery, carConnected: false, evWatts: 0)).Ended;

        Assert.NotNull(ended);
        Assert.Equal(ChargeControlMode.Forecasted, ended!.StartMode);
        Assert.Equal(ChargeControlMode.FastNoBattery, ended.EndMode);
    }

    [Fact]
    public void UnpluggingTheCarEndsTheSession()
    {
        var tracker = NewTracker();
        tracker.Observe(Status(Noon));

        var update = tracker.Observe(Status(Noon.AddMinutes(2), carConnected: false, evWatts: 0));

        Assert.NotNull(update.Ended);
        Assert.Equal(ChargingSessionEndReason.CarUnplugged, update.Ended!.EndReason);
        Assert.Null(tracker.OpenSession);
    }

    [Fact]
    public void ReturningToOffEndsTheSession()
    {
        var tracker = NewTracker();
        tracker.Observe(Status(Noon));

        var update = tracker.Observe(Status(Noon.AddMinutes(2), mode: ChargeControlMode.Off, state: ChargeControlState.Disabled, evWatts: 0));

        Assert.Equal(ChargingSessionEndReason.ModeOff, update.Ended!.EndReason);
    }

    [Fact]
    public void AFinishedCarIsDistinguishedFromSomebodySwitchingTheModeOff()
    {
        // The poll loop has already returned the mode to Off by the time this status is published, so
        // the flag is the only thing left that says the car filled up rather than being interrupted.
        var tracker = NewTracker();
        tracker.Observe(Status(Noon, mode: ChargeControlMode.FastNoBattery));

        var update = tracker.Observe(Status(
            Noon.AddMinutes(2),
            mode: ChargeControlMode.Off,
            state: ChargeControlState.Disabled,
            evWatts: 0,
            sessionCompleted: true));

        Assert.Equal(ChargingSessionEndReason.SessionComplete, update.Ended!.EndReason);
    }

    [Fact]
    public void ShutdownClosesTheOpenSessionAsStopped()
    {
        var tracker = NewTracker();
        tracker.Observe(Status(Noon));

        var update = tracker.Close(ChargingSessionEndReason.ServiceStopped, Noon.AddMinutes(10));

        Assert.Equal(ChargingSessionEndReason.ServiceStopped, update.Ended!.EndReason);
        Assert.Equal(Noon.AddMinutes(10), update.Ended.EndedAt);
        Assert.Null(tracker.OpenSession);
    }

    [Fact]
    public void ClosingWithNothingOpenIsANoOp() =>
        Assert.True(NewTracker().Close(ChargingSessionEndReason.ServiceStopped, Noon).IsEmpty);

    [Fact]
    public void EnergyIsIntegratedFromEveryPollNotOnlyFromTheStoredSamples()
    {
        var tracker = NewTracker();

        // An hour at a steady 11 kW, observed once a minute. Only every other observation becomes a
        // stored sample at the 30-second cadence, but all of them feed the totals.
        for (var minute = 0; minute <= 60; minute++)
        {
            tracker.Observe(Status(Noon.AddMinutes(minute), evWatts: 11_000, solarWatts: 12_000));
        }

        var ended = tracker.Close(ChargingSessionEndReason.ServiceStopped, Noon.AddMinutes(60)).Ended!;

        Assert.Equal(11_000, ended.EnergyDeliveredWh, 0);
        Assert.Equal(11_000, ended.PeakChargingPowerWatts, 0);
    }

    [Fact]
    public void TheSessionTotalsSplitBySourceAndReconcileToTheEnergyDelivered()
    {
        var tracker = NewTracker();

        // 5 kW of spare sun against an 8 kW charge, with the pack lending 2 kW: 5 solar, 2 battery,
        // 1 grid, held for an hour.
        for (var minute = 0; minute <= 60; minute++)
        {
            tracker.Observe(Status(
                Noon.AddMinutes(minute),
                evWatts: 8000,
                solarWatts: 6000,
                batteryWatts: -2000,
                gridWatts: 1000));
        }

        var ended = tracker.Close(ChargingSessionEndReason.ServiceStopped, Noon.AddMinutes(60)).Ended!;

        Assert.Equal(5000, ended.FromSolarWh, 0);
        Assert.Equal(2000, ended.FromBatteryWh, 0);
        Assert.Equal(1000, ended.FromGridWh, 0);
        Assert.Equal(ended.EnergyDeliveredWh, ended.FromSolarWh + ended.FromGridWh + ended.FromBatteryWh, 0);
        Assert.Equal(0.625, ended.SolarFraction!.Value, 3);
    }

    [Fact]
    public void TheCommandedLoanIsTotalledSeparatelyFromTheMeasuredBatteryShare()
    {
        var tracker = NewTracker();

        // The forecast mode commanded a 2 kW loan; the pack actually delivered 1 kW of the charge.
        for (var minute = 0; minute <= 60; minute++)
        {
            tracker.Observe(Status(
                Noon.AddMinutes(minute),
                evWatts: 8000,
                solarWatts: 6000,
                batteryWatts: -1000,
                gridWatts: 2000,
                loanWatts: 2000));
        }

        var ended = tracker.Close(ChargingSessionEndReason.ServiceStopped, Noon.AddMinutes(60)).Ended!;

        Assert.Equal(2000, ended.LoanedWh, 0);
        Assert.Equal(1000, ended.FromBatteryWh, 0);
    }

    [Fact]
    public void TheSamplesKeepTheCommandedCurrentApartFromWhatTheCarActuallyTook()
    {
        var tracker = NewTracker();

        // The setpoint says 16 A but the car has tapered to roughly 6 A worth of power: the gap that
        // separates "we never asked for more" from "the car wouldn't take more".
        var update = tracker.Observe(Status(Noon, targetAmps: 16, activeAmps: 16, evWatts: 4140));

        var sample = update.Sample!;
        Assert.Equal(16, sample.TargetCurrentAmps);
        Assert.Equal(16, sample.ActiveCurrentAmps);
        Assert.Equal(4140, sample.EvChargerPowerWatts, 0);
    }

    [Fact]
    public void UncontrolledChargingIsIgnoredByDefaultAndRecordedWhenAskedFor()
    {
        var plugged = Status(Noon, mode: ChargeControlMode.Off, state: ChargeControlState.Disabled);

        Assert.True(NewTracker().Observe(plugged).IsEmpty);

        var opened = NewTracker(recordUncontrolled: true).Observe(plugged).Started;
        Assert.NotNull(opened);
        Assert.False(opened!.Controlled);
    }

    [Fact]
    public void ASessionThatBeganUncontrolledBecomesControlledWhenAModeTakesOver()
    {
        var tracker = NewTracker(recordUncontrolled: true);
        tracker.Observe(Status(Noon, mode: ChargeControlMode.Off, state: ChargeControlState.Disabled));

        tracker.Observe(Status(Noon.AddMinutes(1), mode: ChargeControlMode.Solar));

        Assert.True(tracker.OpenSession!.Controlled);
    }

    [Fact]
    public void ArmingAndReleasingTheBatteryHoldIsRecordedAsEvents()
    {
        var tracker = NewTracker();
        tracker.Observe(Status(Noon, holdActive: false));

        var armed = tracker.Observe(Status(Noon.AddMinutes(1), holdActive: true));
        var released = tracker.Observe(Status(Noon.AddMinutes(2), holdActive: false));

        Assert.Contains(armed.Events, e => e.Kind == ChargingSessionEventKind.BatteryHoldArmed);
        Assert.Contains(released.Events, e => e.Kind == ChargingSessionEventKind.BatteryHoldReleased);
    }

    [Fact]
    public void APlanThatFallsOutOfTrustMidSessionIsRecorded()
    {
        var usable = Plan(isUsable: true);
        var tracker = NewTracker();
        tracker.Observe(Status(Noon, mode: ChargeControlMode.Forecasted, plan: usable));

        var update = tracker.Observe(Status(
            Noon.AddMinutes(1),
            mode: ChargeControlMode.Forecasted,
            plan: SolarDayPlan.Unavailable(Noon.AddHours(7), "Forecast accuracy has left the trust band.")));

        Assert.Contains(update.Events, e => e.Kind == ChargingSessionEventKind.PlanUnusable);
    }

    [Fact]
    public void TheOpeningPlanIsKeptOnTheHeaderSoTheOutcomeCanBeJudgedAgainstIt()
    {
        var plan = Plan(isUsable: true);
        var tracker = NewTracker();

        var started = tracker.Observe(
            Status(Noon, mode: ChargeControlMode.Forecasted, plan: plan),
            forecastPowerWatts: 6100,
            forecastRemainingTodayWh: 18_000).Started!;

        Assert.Equal(plan, started.StartPlan);
        Assert.Equal(18_000, started.ForecastRemainingAtStartWh);
    }

    [Fact]
    public void TheForecastAtEachInstantIsStoredAlongsideWhatActuallyHappened()
    {
        var tracker = NewTracker();

        var sample = tracker.Observe(Status(Noon, solarWatts: 5400), forecastPowerWatts: 6100).Sample!;

        Assert.Equal(6100, sample.ForecastPowerWatts);
        Assert.Equal(5400, sample.SolarPowerWatts, 0);
    }

    private static SolarDayPlan Plan(bool isUsable) => new(
        RemainingPvWh: 18_000,
        ShoulderEnergyWh: 4000,
        PlateauEnergyWh: 14_000,
        PlateauClaimedByBatteryWh: 0,
        ExpectedHouseWh: 3000,
        BatteryToFullWh: 2000,
        EvBudgetWh: 13_000,
        FeasibleEvEnergyWh: 12_000,
        NextFeasibleWindow: (Noon, Noon.AddHours(4)),
        RequiredSocFloorPercent: 55,
        ShortfallWh: 0,
        EvExpectedTodayWh: 12_000,
        EvTargetWh: 15_000,
        Outlook: DayOutlook.Surplus,
        BiasFactor: 1.0,
        Deadline: Noon.AddHours(7),
        ForecastAsOf: Noon.AddHours(-1),
        IsUsable: isUsable,
        Reason: "Plenty of sun left today.");
}
