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
        double gridEnergyWh = 14_000,
        double paceWatts = 3_000,
        DateTimeOffset? gridStart = null,
        DateTimeOffset? now = null,
        double tailWh = 0,
        DateTimeOffset? holdUntil = null) =>
        new(
            Strategy: strategy,
            Now: now ?? Now,
            DepartBy: DepartBy,
            Deadline: DepartBy.AddMinutes(-15),
            RequiredEnergyWh: requiredWh,
            DeliveredEnergyWh: deliveredWh,
            RemainingEnergyWh: Math.Max(0, requiredWh - deliveredWh),
            SolarEnergyWh: 8_000,
            ForecastSurplusWh: 8_000,
            RequiredPaceWatts: paceWatts,
            GridEnergyWh: gridEnergyWh,
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
            TailEnergyWh: tailWh,
            HoldUntil: holdUntil,
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
    public void WithNoSun_ItHoldsThePaceAndTheGridFundsAllOfIt()
    {
        // The behaviour this replaced ran flat out at 16A until the target was met, which on a real day
        // bought 9 of 13kWh from the grid in 87 minutes. The pace runs at the rate the deadline needs
        // and no faster, leaving the rest of the window for the sun to displace.
        var plan = Plan(paceWatts: 6_000);

        var decision = Controller().Decide(Input(plan));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(8, decision.ChargeCurrentAmps);      // 6000W / 690W per amp, floored
        Assert.NotEqual(16, decision.ChargeCurrentAmps);
        Assert.Equal(0, decision.LoanPowerWatts);

        // Every watt of it is imported, so the hold must arm.
        Assert.True(decision.GridBridgeWatts > 5_000);
    }

    [Fact]
    public void ThePaceRisesToMeetTheDeadlineAndIsCappedByTheCharger()
    {
        // A pace beyond the charger's ceiling simply pins it: the "not enough time" case reaches the
        // same place by a different route.
        var plan = Plan(paceWatts: 20_000);

        var decision = Controller().Decide(Input(plan));

        Assert.Equal(16, decision.ChargeCurrentAmps);
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
        var plan = Plan(paceWatts: 0);

        // 5.5kW over three phases at 230V is ~7.97A, floored to 7A. Long enough paused to be allowed
        // to restart: the dwell timers apply to the solar side exactly as in the forecast-driven mode.
        var decision = Controller().Decide(Input(plan, surplusWatts: 5500, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(7, decision.ChargeCurrentAmps);
    }

    [Fact]
    public void BelowTheFloor_ThePaceStillRunsAndTheGridFundsAllOfIt()
    {
        // 8kW of surplus and the car takes none of it: below the SOC floor the sun belongs to the pack.
        // The promise is still kept, at the charger's floor, entirely on imported energy -- and all of
        // it counts as bridged, so the hold arms and the pack is not quietly raided for it.
        var plan = Plan(socFloorPercent: 70, paceWatts: 1_000);

        var decision = Controller().Decide(Input(plan, surplusWatts: 8_000, socPercent: 65, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(6, decision.ChargeCurrentAmps);
        Assert.Equal(4_140, decision.GridBridgeWatts, 0);
    }

    [Fact]
    public void TheSocFloorDoesNotHoldBackThePace()
    {
        // The pack's priority is about the *sun*, not about the promise. Below the floor the car still
        // gets the pace -- funded by the grid, with the hold armed -- because a low battery is no reason
        // to miss the departure.
        var plan = Plan(socFloorPercent: 70, paceWatts: 6_000);

        var decision = Controller().Decide(Input(plan, socPercent: 30, surplusWatts: 9_000));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);

        // The pace and only the pace: 6000W quantises to 8A. The 9kW of surplus is the pack's.
        Assert.Equal(8, decision.ChargeCurrentAmps);
        Assert.Contains("floor", decision.Reason);

        // All of it is imported, so the pack must be held out of it.
        Assert.True(decision.GridBridgeWatts > 0);
    }

    // --- The grid bridge, and sun outside the plan's blocks ---

    [Fact]
    public void ASurplusUnderTheChargersFloor_IsBridgedFromTheGridRatherThanExported()
    {
        // 3.5kW is real energy and charges nothing at all: the 6A floor on three phases is 4.14kW. The
        // plan already owes 14kWh to the grid, so buying the 640W gap now is strictly cheaper than
        // exporting the 3.5kW and buying the whole 4.14kW back after dark.
        var plan = Plan(paceWatts: 0);

        var decision = Controller().Decide(Input(plan, surplusWatts: 3_500, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(6, decision.ChargeCurrentAmps);
        Assert.Equal(4_140 - 3_500, decision.GridBridgeWatts, 1);
        Assert.Contains("Grid bridge", decision.Reason);
    }

    [Fact]
    public void TheBridgeIsRefusedWhenTheSunCoversTheWholeTarget()
    {
        // Nothing is owed to the grid, so there is no committed import to bring forward — buying now
        // would spend money on energy the day was about to hand over free.
        var plan = Plan(strategy: TargetedChargeStrategy.Solar, gridEnergyWh: 0, paceWatts: 0);

        var decision = Controller().Decide(Input(plan, surplusWatts: 3_500, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal(0, decision.GridBridgeWatts);
    }

    [Fact]
    public void TheBridgeIsRefusedBelowThePlansSocFloor()
    {
        // The pack's priority is not suspended for this. Diverting the surplus to the car would delay
        // the battery's own recovery, and the planned grid block still covers the target.
        var plan = Plan(socFloorPercent: 70, paceWatts: 0);

        var decision = Controller().Decide(Input(
            plan, surplusWatts: 3_500, socPercent: 65, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal(0, decision.GridBridgeWatts);
    }

    [Fact]
    public void TheBridgeIsRefusedOnASurplusTooSmallToBeReal()
    {
        // Below the bridge threshold the sun is barely contributing, and this would just be an
        // unplanned grid block in the wrong place.
        var decision = Controller().Decide(Input(Plan(paceWatts: 0), surplusWatts: 900, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal(0, decision.GridBridgeWatts);
    }

    [Fact]
    public void TheBridgeIsABridgeAndNotABooster()
    {
        // Once the sun clears the floor on its own there is nothing to bridge: the car runs on surplus
        // and the grid contributes nothing, however much is still owed to it.
        var decision = Controller().Decide(Input(Plan(paceWatts: 0), surplusWatts: 8_000, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(11, decision.ChargeCurrentAmps);      // 8000W / 690W per amp, floored
        Assert.Equal(0, decision.GridBridgeWatts);
    }

    [Fact]
    public void SunAboveThePaceSetsTheRate()
    {
        // Nothing owed to the grid, so the pace is zero and the surplus alone decides the current.
        var plan = Plan(paceWatts: 0);

        var decision = Controller().Decide(Input(plan, surplusWatts: 8_000, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(11, decision.ChargeCurrentAmps);
        Assert.Contains("the sun sets the rate", decision.Reason);
        Assert.Equal(0, decision.GridBridgeWatts);
    }

    [Fact]
    public void AFreshTargetDoesNotWaitOutTheRestartDwell()
    {
        // Observed live on 2026-08-23: a target activated under 5kW of surplus sat at 0A logging
        // "Paused 10min of the 15min minimum before restarting". Activating sets the coordinator's
        // state-changed clock, the controller answers "pause", and the dwell then counts its full 15
        // minutes from the button press -- a quarter of an hour of free sun lost to a restart timer
        // with nothing to restart.
        var plan = Plan(deliveredWh: 0, paceWatts: 1_000);

        var decision = Controller().Decide(Input(plan, surplusWatts: 5_000, timeInState: TimeSpan.Zero));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.DoesNotContain("before restarting", decision.Reason);
    }

    [Fact]
    public void OnceSomethingHasBeenDelivered_TheRestartDwellAppliesAgain()
    {
        // The flapping it guards against is real: past the first watt, a surplus wobbling around the
        // threshold must not cycle the contactor every poll.
        var plan = Plan(deliveredWh: 4_000, paceWatts: 1_000);

        var decision = Controller().Decide(Input(plan, surplusWatts: 5_000, timeInState: TimeSpan.FromMinutes(3)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("before restarting", decision.Reason);
    }

    [Fact]
    public void WithNothingOwedAndNoSun_ItWaitsAndSaysSo()
    {
        // Pace zero: the forecast covers the whole target on its own, so there is genuinely nothing to
        // do but wait. The sentence has to read as the plan working, not the plan failing.
        var plan = Plan(paceWatts: 0);

        var decision = Controller().Decide(Input(plan));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("Waiting for sun", decision.Reason);
        Assert.Contains($"{DepartBy.AddMinutes(-15).LocalDateTime:HH:mm}", decision.Reason);
        Assert.False(decision.SessionComplete);
    }

    [Fact]
    public void WithASubFloorPace_ItHoldsTheFloorRatherThanDeferringToTheDeadline()
    {
        // The regression this guards: a 1kW pace under a 4.14kW floor used to wait until the pace
        // climbed past the floor, which crams the whole remainder into the last minutes of the window.
        // The pace is energy the sun is not forecast to cover, so deferring it buys nothing at all.
        var decision = Controller().Decide(Input(Plan(paceWatts: 1_000)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(6, decision.ChargeCurrentAmps);
        Assert.Contains("rather than defer", decision.Reason);
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
    public void PastTheDeparture_TheModeEndsItselfEvenWithTheChargerStopped()
    {
        // Measured live on 2026-08-24: the car was unplugged at 19:52 against a 20:00 departure, the
        // charger fell back to Stop, and the "use-mode is not Fast" precondition then short-circuited
        // every poll before the departure check. The mode sat in Targeted for hours with a live request
        // armed against a departure long past.
        var decision = Controller().Decide(Input(
            Plan(now: DepartBy.AddMinutes(5)),
            mode: EvChargerMode.Stop,
            now: DepartBy.AddMinutes(5)));

        Assert.True(decision.SessionComplete);
        Assert.Contains("has passed", decision.Reason);
    }

    [Fact]
    public void WhenTheCarStopsAtItsOwnLimit_TheModeEndsItself()
    {
        // Pins the intended behaviour. Note it already passed before this branch: given a growing
        // EvIdleFor the controller ends the mode correctly, so the live failures on 2026-08-23 (14 min)
        // and 2026-08-24 (27 min) are NOT here -- something upstream is not feeding EvIdleFor,
        // Charging or EvDrewPower as this test supplies them. Finding that needs an end-to-end
        // reproduction through the coordinator, and is deliberately not attempted in this branch.
        var decision = Controller().Decide(Input(
            Plan(),
            charging: true,
            evDrewPower: true,
            evIdleFor: TimeSpan.FromMinutes(5),
            status: EvChargerStatus.Finishing));

        Assert.True(decision.SessionComplete);
        Assert.Contains("its own limit", decision.Reason);
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
            Plan(paceWatts: 0), charging: false, evDrewPower: true, evIdleFor: TimeSpan.FromHours(3)));

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

    // --- Just in time (#101): the deliberately idle charger ---

    [Fact]
    public void WhileHolding_ItRefusesEvenAGenerousSurplus()
    {
        // The one place this mode turns real sun down. Everything below the rest point is delivered
        // (need == tail), so charging on a bright afternoon would put the car at its target hours early
        // -- which is exactly what the priority exists to prevent.
        var plan = Plan(requiredWh: 5_000, deliveredWh: 0, tailWh: 5_000, holdUntil: Now.AddHours(6), paceWatts: 0);

        var decision = Controller().Decide(Input(plan, surplusWatts: 9_000, socPercent: 95));

        // Stood down rather than paused: this hold is six hours, and the wallbox abandons a Fast-at-0A
        // session in minutes.
        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.Contains("Holding the last", decision.Reason);
    }

    [Fact]
    public void WhileHolding_ItSaysTheIdleChargerIsThePlanWorking()
    {
        // The sentence is the feature. A charger sitting still at 23:00 with a 07:00 departure is the
        // single state most likely to be read as a fault.
        var plan = Plan(requiredWh: 5_000, tailWh: 5_000, holdUntil: Now.AddHours(6), paceWatts: 0);

        var decision = Controller().Decide(Input(plan, surplusWatts: 0));

        Assert.Contains("this is the plan working", decision.Reason);
        Assert.DoesNotContain("Waiting for sun", decision.Reason);
    }

    [Fact]
    public void BeforeTheFreePartIsDelivered_APlannedHoldDoesNotStopItCharging()
    {
        // A hold planned is not a hold in force: 12kWh still needed against a 4kWh tail means there is
        // 8kWh below the rest point to get on with, and the sun is the car's as usual.
        var plan = Plan(requiredWh: 12_000, tailWh: 4_000, holdUntil: Now.AddHours(6), paceWatts: 0);

        // Past the restart dwell, so what is being tested is the hold and not that.
        var decision = Controller().Decide(
            Input(plan, surplusWatts: 9_000, socPercent: 95, timeInState: TimeSpan.FromHours(1)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
    }

    [Fact]
    public void OnceTheReleaseHasPassed_TheTailChargesLikeAnythingElse()
    {
        // Past the release the planner stops setting HoldUntil at all, so this is only a guard that
        // nothing in the controller keeps holding on its own.
        var plan = Plan(requiredWh: 5_000, tailWh: 5_000, holdUntil: Now.AddHours(-1), paceWatts: 8_000);

        var decision = Controller().Decide(Input(plan, surplusWatts: 0, timeInState: TimeSpan.FromHours(1)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
    }

    [Fact]
    public void WithNoHoldPlanned_NothingAboutTheDefaultPathChanges()
    {
        var plan = Plan(paceWatts: 6_000);

        var decision = Controller().Decide(Input(plan, surplusWatts: 0, timeInState: TimeSpan.FromHours(1)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.DoesNotContain("Holding", decision.Reason);
    }
}
