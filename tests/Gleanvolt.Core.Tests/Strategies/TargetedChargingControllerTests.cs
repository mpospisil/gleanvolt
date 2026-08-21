using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class TargetedChargingControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DepartBy = new(2026, 8, 9, 7, 0, 0, TimeSpan.Zero);

    private static TargetedChargingController Controller() =>
        new(
            new ChargePowerConverter(230, 3),
            new TargetedChargingOptions(
                MinChargingCurrentAmps: 6,
                MaxChargingCurrentAmps: 16,
                CurrentStepAmps: 1,
                ResumeHysteresisWatts: 200,
                MinRunTime: TimeSpan.FromMinutes(10),
                MinPauseTime: TimeSpan.FromMinutes(15),
                CompletionDwell: TimeSpan.FromMinutes(2)));

    /// <summary>A plan with the blocks stated outright, so each test says exactly what it is about.</summary>
    private static TargetedChargePlan Plan(
        TargetedChargeStrategy strategy = TargetedChargeStrategy.SolarPlusGrid,
        IReadOnlyList<TargetedChargeBlock>? blocks = null,
        double requiredWh = 22_000,
        double deliveredWh = 0,
        double socFloorPercent = 50,
        DateTimeOffset? gridStart = null,
        DateTimeOffset? now = null) =>
        new(
            Strategy: strategy,
            Now: now ?? Now,
            DepartBy: DepartBy,
            Deadline: DepartBy.AddMinutes(-15),
            RequiredEnergyWh: requiredWh,
            DeliveredEnergyWh: deliveredWh,
            RemainingEnergyWh: Math.Max(0, requiredWh - deliveredWh),
            SolarEnergyWh: 8_000,
            GridEnergyWh: 14_000,
            CeilingEnergyWh: 90_000,
            ExpectedEnergyWh: 22_000,
            ShortfallWh: 0,
            GridStart: gridStart ?? DepartBy.AddHours(-2),
            FeasibleDeparture: null,
            SocFloorPercent: socFloorPercent,
            BatteryToFullWh: 0,
            Blocks: blocks ?? [],
            ForecastAsOf: Now,
            IsUsable: true,
            Reason: "test");

    private static TargetedChargeBlock Block(TargetedChargeSource source, DateTimeOffset start, DateTimeOffset end) =>
        new(start, end, source, PowerWatts: 5000, EnergyWh: 5000 * (end - start).TotalHours);

    private static ChargingControlInput Input(
        TargetedChargePlan? plan,
        double surplusWatts = 0,
        double socPercent = 80,
        bool charging = false,
        EvChargerMode mode = EvChargerMode.Fast,
        TimeSpan timeInState = default,
        bool evDrewPower = false,
        TimeSpan evIdleFor = default,
        EvChargerStatus status = EvChargerStatus.Charging,
        DateTimeOffset? now = null) =>
        new(
            new EnergyState(now ?? Now, socPercent, BatteryPowerWatts: 0, SolarPowerWatts: 0,
                GridPowerWatts: 0, status, EvChargerPowerWatts: 0),
            surplusWatts,
            new EvChargerSettings(mode, 6),
            charging,
            Plan: null,
            TargetedPlan: plan,
            TimeInCurrentState: timeInState,
            EvDrewPower: evDrewPower,
            EvIdleFor: evIdleFor);

    [Fact]
    public void OutsideFastMode_ItLeavesTheChargerAlone()
    {
        var decision = Controller().Decide(Input(Plan(), mode: EvChargerMode.Green));

        Assert.Equal(ChargingControlAction.None, decision.Action);
    }

    [Fact]
    public void InsideTheGridBlock_ItChargesAtTheMaximumCurrent()
    {
        var plan = Plan(blocks: [Block(TargetedChargeSource.Grid, Now.AddMinutes(-5), Now.AddHours(2))]);

        var decision = Controller().Decide(Input(plan));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(16, decision.ChargeCurrentAmps);
        Assert.Equal(0, decision.LoanPowerWatts);
    }

    [Fact]
    public void WhenThereIsNotEnoughTime_ItChargesAtTheMaximumWhateverTheBlocksSay()
    {
        var decision = Controller().Decide(Input(Plan(strategy: TargetedChargeStrategy.Maximum)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(16, decision.ChargeCurrentAmps);
    }

    [Fact]
    public void InsideASolarBlock_ItChargesAtTheCurrentTheSurplusSupports()
    {
        var plan = Plan(blocks: [Block(TargetedChargeSource.Solar, Now.AddMinutes(-5), Now.AddHours(2))]);

        // 5.5kW over three phases at 230V is ~7.97A, floored to 7A. Long enough paused to be allowed
        // to restart: the dwell timers apply to the solar side exactly as in the forecast-driven mode.
        var decision = Controller().Decide(Input(plan, surplusWatts: 5500, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(7, decision.ChargeCurrentAmps);
    }

    [Fact]
    public void InsideASolarBlock_ThePlansSocFloorStillGates()
    {
        var plan = Plan(
            blocks: [Block(TargetedChargeSource.Solar, Now.AddMinutes(-5), Now.AddHours(2))],
            socFloorPercent: 70);

        var decision = Controller().Decide(Input(plan, surplusWatts: 8000, socPercent: 65, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("floor", decision.Reason);
    }

    [Fact]
    public void TheSocFloorDoesNotHoldBackTheGridBlock()
    {
        // The pack's priority is about the *sun*. Inside the grid block the car is on imported energy
        // and the discharge hold is armed, so a low battery is no reason to miss the departure.
        var plan = Plan(
            blocks: [Block(TargetedChargeSource.Grid, Now.AddMinutes(-5), Now.AddHours(2))],
            socFloorPercent: 70);

        var decision = Controller().Decide(Input(plan, socPercent: 30));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(16, decision.ChargeCurrentAmps);
    }

    [Fact]
    public void BetweenTheBlocks_ItPausesAndSaysWhenTheGridWouldStart()
    {
        var plan = Plan(
            blocks: [Block(TargetedChargeSource.Grid, DepartBy.AddHours(-2), DepartBy.AddMinutes(-15))],
            gridStart: DepartBy.AddHours(-2));

        var decision = Controller().Decide(Input(plan));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("Waiting for sun", decision.Reason);
        Assert.Contains($"{DepartBy.AddHours(-2).LocalDateTime:HH:mm}", decision.Reason);
        Assert.False(decision.SessionComplete);
    }

    [Fact]
    public void ADroppedChargerReading_DoesNotEndTheSession()
    {
        // Regression (2026-08-21, live): the charger stopped answering for a single poll, the status
        // came back Unknown, and `!IsCarConnected()` read that as an unplugged car. The mode ended
        // itself mid-charge and the request went with it -- nothing was left to restart the plan.
        var plan = Plan(blocks: [Block(TargetedChargeSource.Grid, Now.AddMinutes(-5), Now.AddHours(2))]);

        var decision = Controller().Decide(Input(
            plan, charging: true, evDrewPower: true, status: EvChargerStatus.Unknown));

        Assert.False(decision.SessionComplete);
        Assert.Equal(ChargingControlAction.Charge, decision.Action);
    }

    [Fact]
    public void AnActualUnplugStillEndsTheSession()
    {
        var plan = Plan(blocks: [Block(TargetedChargeSource.Grid, Now.AddMinutes(-5), Now.AddHours(2))]);

        var decision = Controller().Decide(Input(
            plan, charging: true, evDrewPower: true, status: EvChargerStatus.Available));

        Assert.True(decision.SessionComplete);
        Assert.Equal(ChargingControlAction.Pause, decision.Action);
    }

    [Fact]
    public void WhenTheTargetIsReached_TheSessionIsComplete()
    {
        var decision = Controller().Decide(Input(Plan(strategy: TargetedChargeStrategy.Complete, deliveredWh: 22_000)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.True(decision.SessionComplete);
        Assert.Contains("Target reached", decision.Reason);
    }

    [Fact]
    public void PastTheDeparture_TheSessionIsComplete()
    {
        var decision = Controller().Decide(Input(Plan(), now: DepartBy.AddMinutes(1)));

        Assert.True(decision.SessionComplete);
        Assert.Contains("has passed", decision.Reason);
    }

    [Fact]
    public void WhenTheCarStopsShortOfTheTarget_TheSessionIsComplete()
    {
        var plan = Plan(
            blocks: [Block(TargetedChargeSource.Grid, Now.AddMinutes(-5), Now.AddHours(2))],
            deliveredWh: 9_000);

        var decision = Controller().Decide(Input(
            plan, charging: true, evDrewPower: true, evIdleFor: TimeSpan.FromMinutes(3)));

        Assert.True(decision.SessionComplete);
        Assert.Contains("short of the target", decision.Reason);
    }

    [Fact]
    public void ACarIdleWhileWeArePausedIsNotACarThatHasStopped()
    {
        // The gap between blocks is us holding the charger at the pause current. Reading it as "the car
        // has finished" would end the mode every time it waited for the sun.
        var decision = Controller().Decide(Input(
            Plan(), charging: false, evDrewPower: true, evIdleFor: TimeSpan.FromHours(3)));

        Assert.False(decision.SessionComplete);
        Assert.Equal(ChargingControlAction.Pause, decision.Action);
    }

    [Fact]
    public void WithNoRequestAtAll_ItPausesRatherThanEndingTheMode()
    {
        var decision = Controller().Decide(Input(plan: null));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.False(decision.SessionComplete);
        Assert.Contains("No target set", decision.Reason);
    }

    [Fact]
    public void ASolarDipIsHeldAtTheMinimumCurrentUntilTheRunTimerExpires()
    {
        var plan = Plan(blocks: [Block(TargetedChargeSource.Solar, Now.AddMinutes(-5), Now.AddHours(2))]);

        var decision = Controller().Decide(Input(
            plan, surplusWatts: 1000, charging: true, timeInState: TimeSpan.FromMinutes(2)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(6, decision.ChargeCurrentAmps);
    }
}
