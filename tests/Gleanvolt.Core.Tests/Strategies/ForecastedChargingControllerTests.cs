using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class ForecastedChargingControllerTests
{
    // Three-phase reference setup: 1A = 690W, so the 6A floor is 4140W.
    private const double WattsPerAmp = 690;
    private const double MinChargePowerWatts = 6 * WattsPerAmp;

    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Deadline = new(2026, 7, 27, 19, 0, 0, TimeSpan.Zero);

    private readonly StubController _fallback = new();

    private static ForecastedChargingOptions Options(
        bool enableLoan = true,
        double maxLoanPowerWatts = 3800,
        double minBridgeSurplusWatts = 2000,
        double spillBridgeSurplusWatts = 400,
        double maxDailyLoanWh = 4000,
        double sessionTargetWh = 0,
        double floorResumeMarginPercent = 0,
        double floorGuardReserveWatts = 0,
        TimeSpan? minRunTime = null,
        TimeSpan? minPauseTime = null) =>
        new(
            MinChargingCurrentAmps: 6,
            MaxChargingCurrentAmps: 16,
            CurrentStepAmps: 1,
            ResumeHysteresisWatts: 200,
            FloorResumeMarginPercent: floorResumeMarginPercent,
            FloorGuardReserveWatts: floorGuardReserveWatts,
            EnableBatteryLoan: enableLoan,
            MaxLoanPowerWatts: maxLoanPowerWatts,
            MinBridgeSurplusWatts: minBridgeSurplusWatts,
            SpillBridgeSurplusWatts: spillBridgeSurplusWatts,
            MaxDailyLoanWh: maxDailyLoanWh,
            LoanSocMarginPercent: 2,
            MinRunTime: minRunTime ?? TimeSpan.FromMinutes(10),
            MinPauseTime: minPauseTime ?? TimeSpan.Zero,
            FinalGuardBefore: TimeSpan.FromHours(1),
            SessionEnergyTargetWh: sessionTargetWh);

    private ForecastedChargingController Controller(ForecastedChargingOptions? options = null) =>
        new(new ChargePowerConverter(230, 3), _fallback, options ?? Options());

    private static SolarDayPlan Plan(
        double feasibleEvWh = 8000,
        double socFloor = 50,
        double shortfallWh = 0,
        bool usable = true,
        bool hasWindow = true,
        double? trajectoryFloor = null,
        double spillWh = 0,
        double loanableWh = 2000) =>
        new(
            RemainingPvWh: 20_000,
            ShoulderEnergyWh: 2000,
            PlateauEnergyWh: 10_000,
            PlateauClaimedByBatteryWh: 0,
            ExpectedHouseWh: 2000,
            BatteryToFullWh: 1000,
            EvBudgetWh: feasibleEvWh,
            FeasibleEvEnergyWh: feasibleEvWh,
            NextFeasibleWindow: hasWindow ? (Now, Now.AddHours(3)) : null,
            RequiredSocFloorPercent: socFloor,
            // Defaults to the floor in force, i.e. the forecast's own trajectory is what binds: a
            // breach is then the serious kind. Tests about the configured clamp pass a lower one.
            TrajectorySocFloorPercent: trajectoryFloor ?? socFloor,
            // Zero by default: the pack can absorb everything the day still makes, which is the case the
            // loan's original "is this surplus real?" floor was written for. The spill cases pass their own.
            SpillWh: spillWh,
            LoanableWh: loanableWh,
            ShortfallWh: shortfallWh,
            BiasFactor: 1,
            Deadline: Deadline,
            ForecastAsOf: Now.AddHours(-1),
            IsUsable: usable,
            Reason: "test plan",
            Timeline: []);

    private static ChargingControlInput Input(
        double surplusWatts,
        double socPercent = 80,
        SolarDayPlan? plan = null,
        bool charging = false,
        EvChargerMode mode = EvChargerMode.Fast,
        TimeSpan timeInState = default,
        double sessionEnergyWh = 0,
        double loanedTodayWh = 0,
        double? loanOutstandingWh = null,
        DateTimeOffset? now = null,
        bool chargedThisMode = false) =>
        new(
            new EnergyState(now ?? Now, socPercent, BatteryPowerWatts: 0, SolarPowerWatts: 6000,
                GridPowerWatts: 0, EvChargerStatus.Charging, EvChargerPowerWatts: 0),
            surplusWatts,
            new EvChargerSettings(mode, 6),
            charging,
            plan ?? Plan(),
            TargetedPlan: null,
            TimeInCurrentState: timeInState,
            SessionEnergyWh: sessionEnergyWh,
            LoanedTodayWh: loanedTodayWh,
            ChargedThisMode: chargedThisMode,
            // Nothing recovered unless a test says so, which is the pessimistic reading of what was lent.
            LoanOutstandingWh: loanOutstandingWh ?? loanedTodayWh);

    [Fact]
    public void OutsideFastMode_ItLeavesTheChargerAlone()
    {
        var decision = Controller().Decide(Input(8000, mode: EvChargerMode.Green));

        Assert.Equal(ChargingControlAction.None, decision.Action);
    }

    [Fact]
    public void WithoutAUsablePlan_ItDelegatesToTheLiveSolarFallback()
    {
        _fallback.NextDecision = new(ChargingControlAction.Charge, 12, "fallback says charge");

        var decision = Controller().Decide(Input(8000, plan: Plan(usable: false)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(12, decision.ChargeCurrentAmps);
        Assert.Contains("fallback says charge", decision.Reason);
        Assert.Contains("live-solar fallback", decision.Reason);
    }

    [Fact]
    public void AmpleSurplusChargesAtTheQuantisedCurrentWithNoLoan()
    {
        var decision = Controller().Decide(Input(8000));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(11, decision.ChargeCurrentAmps); // 8000 / 690 = 11.6 -> 11A
        Assert.Equal(0, decision.LoanPowerWatts);
    }

    [Fact]
    public void TheTargetIsClampedToTheConfiguredMaximum()
    {
        var decision = Controller().Decide(Input(20_000));

        Assert.Equal(16, decision.ChargeCurrentAmps);
    }

    [Fact]
    public void BelowTheSocFloor_ItPausesSoTheBatteryCanCatchUp()
    {
        var decision = Controller().Decide(Input(8000, socPercent: 45, plan: Plan(socFloor: 50)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("floor", decision.Reason);
    }

    [Fact]
    public void RestartingRequiresTheSocMarginAboveTheFloor()
    {
        // The morning case the mode was flapping on: SOC one percent over the floor. Enough to keep a
        // running session alive, nowhere near enough to bring a paused one back.
        var options = Options(enableLoan: false, floorResumeMarginPercent: 5);

        var starting = Controller(options).Decide(
            Input(8000, socPercent: 51, charging: false, timeInState: TimeSpan.FromHours(1), plan: Plan(socFloor: 50)));
        var continuing = Controller(options).Decide(
            Input(8000, socPercent: 51, charging: true, timeInState: TimeSpan.FromHours(1), plan: Plan(socFloor: 50)));

        Assert.Equal(ChargingControlAction.Pause, starting.Action);
        Assert.Contains("has not recovered", starting.Reason);
        Assert.Equal(ChargingControlAction.Charge, continuing.Action);
    }

    [Fact]
    public void OnceTheSocMarginIsMet_ChargingRestarts()
    {
        var decision = Controller(Options(enableLoan: false, floorResumeMarginPercent: 5)).Decide(
            Input(8000, socPercent: 55, charging: false, timeInState: TimeSpan.FromHours(1), plan: Plan(socFloor: 50)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
    }

    [Fact]
    public void TheResumeMarginNeverDemandsMoreThanAFullBattery()
    {
        // Late in the day the floor is already at 100%; adding a margin on top would make the gate
        // unsatisfiable in a way the final guard doesn't intend.
        var decision = Controller(Options(enableLoan: false, floorResumeMarginPercent: 5)).Decide(
            Input(8000, socPercent: 100, charging: false, timeInState: TimeSpan.FromHours(1), plan: Plan(socFloor: 100)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
    }

    [Fact]
    public void DippingThroughTheConfiguredClampHoldsTheSessionAtTheMinimum()
    {
        // A sunny morning: the floor in force is the owner's 50% clamp, while the forecast's own
        // trajectory sits at 20%. A one-percent dip under the clamp is not worth ending a session for.
        var decision = Controller(Options(enableLoan: false)).Decide(
            Input(8000, socPercent: 49, charging: true, timeInState: TimeSpan.FromMinutes(2),
                plan: Plan(socFloor: 50, trajectoryFloor: 20)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(6, decision.ChargeCurrentAmps);
        Assert.Contains("minimum run time", decision.Reason);
    }

    [Fact]
    public void DippingThroughTheForecastTrajectoryStopsTheSessionAtOnce()
    {
        // Same dip, but now the trajectory is what binds: the evening 100% is genuinely at risk, so the
        // dwell timer does not get to keep the car running.
        var decision = Controller(Options(enableLoan: false)).Decide(
            Input(8000, socPercent: 49, charging: true, timeInState: TimeSpan.FromMinutes(2),
                plan: Plan(socFloor: 50, trajectoryFloor: 50)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("floor", decision.Reason);
    }

    [Fact]
    public void PastTheMinimumRunTime_AClampDipStillPauses()
    {
        var decision = Controller(Options(enableLoan: false)).Decide(
            Input(8000, socPercent: 49, charging: true, timeInState: TimeSpan.FromHours(1),
                plan: Plan(socFloor: 50, trajectoryFloor: 20)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
    }

    [Fact]
    public void InsideTheGuardBand_PartOfTheSurplusIsHeldBackForTheBattery()
    {
        // SOC one percent over the floor, so inside the 5% band: 8000W of surplus reaches the car as
        // 7250W (10A) instead of 8000W (11A), and the 750W difference walks the pack back up.
        var options = Options(enableLoan: false, floorResumeMarginPercent: 5, floorGuardReserveWatts: 750);
        var input = Input(8000, socPercent: 51, charging: true, timeInState: TimeSpan.FromHours(1), plan: Plan(socFloor: 50));

        var guarded = Controller(options).Decide(input);
        var unguarded = Controller(Options(enableLoan: false, floorResumeMarginPercent: 5)).Decide(input);

        Assert.Equal(ChargingControlAction.Charge, guarded.Action);
        Assert.Equal(10, guarded.ChargeCurrentAmps);
        Assert.Equal(11, unguarded.ChargeCurrentAmps);
        Assert.Contains("reserved for the battery", guarded.Reason);
    }

    [Fact]
    public void AboveTheGuardBand_TheCarGetsTheWholeSurplus()
    {
        var decision = Controller(Options(enableLoan: false, floorResumeMarginPercent: 5, floorGuardReserveWatts: 750)).Decide(
            Input(8000, socPercent: 56, charging: true, timeInState: TimeSpan.FromHours(1), plan: Plan(socFloor: 50)));

        Assert.Equal(11, decision.ChargeCurrentAmps);
        Assert.DoesNotContain("reserved", decision.Reason);
    }

    [Fact]
    public void InsideTheGuardBand_AMarginalSurplusGoesToTheBatteryInstead()
    {
        // 4600W clears the 4140W floor on its own, but not once the battery's 750W is taken out. The
        // session stops, the pack gets the whole surplus, and the resume margin keeps the car off until
        // it has climbed clear -- one long cycle instead of a dozen short ones.
        var decision = Controller(Options(enableLoan: false, floorResumeMarginPercent: 5, floorGuardReserveWatts: 750)).Decide(
            Input(4600, socPercent: 51, charging: true, timeInState: TimeSpan.FromHours(1), plan: Plan(socFloor: 50)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("reserved for the battery", decision.Reason);
    }

    [Fact]
    public void InsideTheGuardBand_TheBatteryLendsNothing()
    {
        // A loan and a reserve at the same SOC would cancel out: the pack would be discharging to the
        // car and being topped up from the same surplus, paying a round trip for no net movement.
        var decision = Controller(Options(floorResumeMarginPercent: 5, floorGuardReserveWatts: 0)).Decide(
            Input(3000, socPercent: 53, charging: true, timeInState: TimeSpan.FromHours(1), plan: Plan(socFloor: 50)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal(0, decision.LoanPowerWatts);
    }

    [Fact]
    public void InsideTheFinalGuardWindow_ItPausesWhileTheBatteryIsNotFull()
    {
        var decision = Controller().Decide(Input(8000, socPercent: 90, now: Deadline.AddMinutes(-30)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("Final guard", decision.Reason);
    }

    [Fact]
    public void InsideTheFinalGuardWindow_AFullBatteryStillLetsTheCarCharge()
    {
        var decision = Controller().Decide(Input(8000, socPercent: 100, now: Deadline.AddMinutes(-30)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
    }

    [Fact]
    public void ReachingTheSessionTargetEndsTheSession()
    {
        var controller = Controller(Options(sessionTargetWh: 10_000));

        var decision = controller.Decide(Input(8000, sessionEnergyWh: 10_500));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("Session target reached", decision.Reason);
    }

    [Fact]
    public void ADayWithNoChargeableWindowPauses()
    {
        var decision = Controller().Decide(Input(8000, plan: Plan(feasibleEvWh: 0, hasWindow: false)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("No deliverable budget", decision.Reason);
    }

    // Issue #217: this used to be a hard stop that ended the session outright. The hard stops are
    // promises -- the battery's evening guarantee, the final guard, the session ceiling -- and a day
    // the sun never gets going on is weather, not a promise, so it waits out the dwell timer like a
    // passing cloud instead.
    [Fact]
    public void ADayWithNoChargeableWindowHoldsAtMinimumInsideTheMinimumRunTime()
    {
        var decision = Controller().Decide(Input(
            8000, plan: Plan(feasibleEvWh: 0, hasWindow: false), charging: true, timeInState: TimeSpan.FromMinutes(2)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(6, decision.ChargeCurrentAmps);
        Assert.Contains("minimum run time", decision.Reason);
    }

    [Fact]
    public void TheLoanBridgesARealSurplusUpToTheSixAmpFloor()
    {
        // 3000W is a genuine surplus that would otherwise charge nothing at all on three phases.
        var decision = Controller().Decide(Input(3000, charging: true));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(6, decision.ChargeCurrentAmps);
        Assert.Equal(MinChargePowerWatts - 3000, decision.LoanPowerWatts, 1);
    }

    [Fact]
    public void TheLoanNeverFundsASessionOnItsOwn()
    {
        // Below MinBridgeSurplusWatts the sun isn't really contributing; lending here would just be a
        // battery-to-car transfer, paying a round trip and a cycle for nothing.
        var decision = Controller().Decide(Input(1200, charging: true, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal(0, decision.LoanPowerWatts);
    }

    // Issue #223 removed the shortfall veto: what stops lending on a day that cannot refill the pack is
    // the floor, and it stops the whole session rather than merely the loan. A shortfall *is* a
    // trajectory floor above the current SOC -- that is what the two words mean -- so this is the only
    // shape a real shortfall day has, and the veto it replaced was mostly refusing the midday spill on
    // afternoons whose evening load outran the sun.
    [Fact]
    public void ADayThatCannotRefillThePackIsStoppedByTheFloor_NotByTheLoanRule()
    {
        var decision = Controller().Decide(Input(
            3000, socPercent: 70, charging: true, timeInState: TimeSpan.FromMinutes(20),
            plan: Plan(shortfallWh: 5000, socFloor: 80, trajectoryFloor: 80)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal(0, decision.LoanPowerWatts);
        Assert.Contains("floor the forecast requires", decision.Reason);
    }

    [Fact]
    public void NoLoanOnceTheOutstandingLendingReachesTheDailyCap()
    {
        var decision = Controller().Decide(Input(3000, charging: true, timeInState: TimeSpan.FromMinutes(20), loanedTodayWh: 4000));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal(0, decision.LoanPowerWatts);
    }

    // The cap counts what the pack is *still* down, not what it lent this morning: a pack that lent 6kWh
    // and has been charged back to where it started is physically able to lend again, and used to spend
    // the rest of the day refused (issue #223).
    [Fact]
    public void LendingResumesOnceThePackHasRecoveredWhatItLent()
    {
        var decision = Controller().Decide(Input(
            3000, charging: true, timeInState: TimeSpan.FromMinutes(20),
            loanedTodayWh: 6000, loanOutstandingWh: 0));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(MinChargePowerWatts - 3000, decision.LoanPowerWatts, 1);
    }

    // The bug the issue was opened for, measured: the loan reached exactly 4140W while a restart asked
    // for 4140 + 200, so the loan could sustain a charge and could never start one. The whole of its
    // operating range was closed to a paused session, whatever the state of the pack.
    [Fact]
    public void APausedSessionRestartsOnALoan_TheBridgeReachesTheStartThreshold()
    {
        var controller = Controller();
        var startThresholdWatts = MinChargePowerWatts + 200;

        var decision = controller.Decide(Input(
            2500, socPercent: 100, charging: false, timeInState: TimeSpan.FromHours(1),
            plan: Plan(spillWh: 2500), chargedThisMode: true));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(6, decision.ChargeCurrentAmps);
        Assert.Equal(startThresholdWatts - 2500, decision.LoanPowerWatts, 1);
    }

    // The substance of the request behind the issue: a full pack, a thin afternoon, and a surplus being
    // exported because it cannot reach the charger's floor on its own. The plan offers no window and no
    // budget -- both statements about the forecast -- and on a pack with no room left the live surplus is
    // entitled to answer for itself.
    [Fact]
    public void OnASpillDay_AThinSurplusIsBridgedEvenWithNoPlannedWindow()
    {
        var decision = Controller().Decide(Input(
            1600, socPercent: 100, charging: false, timeInState: TimeSpan.FromHours(1),
            plan: Plan(feasibleEvWh: 0, hasWindow: false, spillWh: 2500), chargedThisMode: true));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(6, decision.ChargeCurrentAmps);
        Assert.Equal(MinChargePowerWatts + 200 - 1600, decision.LoanPowerWatts, 1);
    }

    // ...and the other half of that rule, which is the behaviour that was right before: with room in the
    // pack the energy lent is energy it would have kept, so the round trip has to be earned by a surplus
    // that is really there.
    [Fact]
    public void WithRoomInThePack_AThinSurplusIsStillRefused()
    {
        var decision = Controller().Decide(Input(
            1600, socPercent: 80, charging: true, timeInState: TimeSpan.FromMinutes(20),
            plan: Plan(spillWh: 0)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal(0, decision.LoanPowerWatts);
    }

    [Fact]
    public void WithRoomInThePack_ADayWithNoDeliverableBudgetStillStopsTheCar()
    {
        var decision = Controller().Decide(Input(
            3000, socPercent: 80, charging: true, timeInState: TimeSpan.FromMinutes(20),
            plan: Plan(feasibleEvWh: 0, hasWindow: false, spillWh: 0)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("No deliverable budget", decision.Reason);
    }

    [Fact]
    public void NoLoanWhileTheSocSitsOnTheFloor()
    {
        // At 51% against a 50% floor with a 2% margin, the pack has nothing to spare.
        var decision = Controller().Decide(Input(
            3000, socPercent: 51, charging: true, timeInState: TimeSpan.FromMinutes(20), plan: Plan(socFloor: 50)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal(0, decision.LoanPowerWatts);
    }

    [Fact]
    public void TheLoanIsCappedAtTheConfiguredPower()
    {
        var controller = Controller(Options(maxLoanPowerWatts: 500, minBridgeSurplusWatts: 2000));

        // A 2.5kW surplus needs 1.64kW to reach the floor but may only borrow 500W, so it stays paused.
        var decision = controller.Decide(Input(2500, charging: true, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
    }

    [Fact]
    public void WithinTheMinimumRunTime_ASurplusDipHoldsTheSessionAtTheMinimumCurrent()
    {
        var decision = Controller(Options(enableLoan: false)).Decide(
            Input(1000, charging: true, timeInState: TimeSpan.FromMinutes(3)));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(6, decision.ChargeCurrentAmps);
        Assert.Contains("minimum run time", decision.Reason);
    }

    [Fact]
    public void OnceTheMinimumRunTimeHasPassed_TheSameDipPauses()
    {
        var decision = Controller(Options(enableLoan: false)).Decide(
            Input(1000, charging: true, timeInState: TimeSpan.FromMinutes(20)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
    }

    [Fact]
    public void WithinTheMinimumPauseTime_ItDoesNotRestart()
    {
        var controller = Controller(Options(minPauseTime: TimeSpan.FromMinutes(15)));

        var decision = controller.Decide(Input(8000, charging: false, timeInState: TimeSpan.FromMinutes(5), chargedThisMode: true));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("minimum before restarting", decision.Reason);
    }

    [Fact]
    public void JustSelected_ItDoesNotWaitToRestartAChargeItNeverRan()
    {
        // The dwell used to count from the moment the mode was selected, so every start lost up to 15
        // minutes of sun -- whether or not the charger had already started the car at plug-in.
        var controller = Controller(Options(minPauseTime: TimeSpan.FromMinutes(15)));

        var decision = controller.Decide(Input(8000, charging: false, timeInState: TimeSpan.Zero, chargedThisMode: false));

        Assert.DoesNotContain("minimum before restarting", decision.Reason);
        Assert.Equal(ChargingControlAction.Charge, decision.Action);
    }

    [Fact]
    public void StartingRequiresTheHysteresisMarginAboveTheFloor()
    {
        // Exactly at the 6A floor: enough to keep going, not enough to start.
        var starting = Controller(Options(enableLoan: false)).Decide(Input(MinChargePowerWatts, charging: false));
        var continuing = Controller(Options(enableLoan: false)).Decide(Input(MinChargePowerWatts, charging: true, timeInState: TimeSpan.FromHours(1)));

        Assert.Equal(ChargingControlAction.Pause, starting.Action);
        Assert.Equal(ChargingControlAction.Charge, continuing.Action);
    }

    [Fact]
    public void AnExhaustedBudgetPausesEvenWithSunOnTheRoof()
    {
        var decision = Controller().Decide(Input(8000, plan: Plan(feasibleEvWh: 0), timeInState: TimeSpan.FromHours(1)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("No deliverable budget", decision.Reason);
    }

    private sealed class StubController : IChargingController
    {
        public ChargingControlDecision NextDecision { get; set; } = new(ChargingControlAction.Pause, null, "stub");

        public ChargingControlDecision Decide(ChargingControlInput input) => NextDecision;
    }
}
