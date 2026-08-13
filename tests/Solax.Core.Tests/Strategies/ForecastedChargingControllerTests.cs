using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Core.Tests.Strategies;

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
        double maxLoanPowerWatts = 2500,
        double minBridgeSurplusWatts = 2000,
        double maxDailyLoanWh = 4000,
        double sessionTargetWh = 0,
        TimeSpan? minRunTime = null,
        TimeSpan? minPauseTime = null) =>
        new(
            MinChargingCurrentAmps: 6,
            MaxChargingCurrentAmps: 16,
            CurrentStepAmps: 1,
            ResumeHysteresisWatts: 200,
            EnableBatteryLoan: enableLoan,
            MaxLoanPowerWatts: maxLoanPowerWatts,
            MinBridgeSurplusWatts: minBridgeSurplusWatts,
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
        DayOutlook outlook = DayOutlook.Surplus,
        bool usable = true) =>
        new(
            RemainingPvWh: 20_000,
            ShoulderEnergyWh: 2000,
            PlateauEnergyWh: 10_000,
            PlateauClaimedByBatteryWh: 0,
            ExpectedHouseWh: 2000,
            BatteryToFullWh: 1000,
            EvBudgetWh: feasibleEvWh,
            FeasibleEvEnergyWh: feasibleEvWh,
            NextFeasibleWindow: (Now, Now.AddHours(3)),
            RequiredSocFloorPercent: socFloor,
            ShortfallWh: shortfallWh,
            EvExpectedTodayWh: feasibleEvWh,
            EvTargetWh: 15_000,
            Outlook: outlook,
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
        DateTimeOffset? now = null) =>
        new(
            new EnergyState(now ?? Now, socPercent, BatteryPowerWatts: 0, SolarPowerWatts: 6000,
                GridPowerWatts: 0, EvChargerStatus.Charging, EvChargerPowerWatts: 0),
            surplusWatts,
            new EvChargerSettings(mode, 6),
            charging,
            plan ?? Plan(),
            timeInState,
            sessionEnergyWh,
            loanedTodayWh);

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
    public void NoChargeToday_Pauses()
    {
        var decision = Controller().Decide(Input(8000, plan: Plan(feasibleEvWh: 0, outlook: DayOutlook.NoChargeToday)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("No chargeable window", decision.Reason);
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

    [Fact]
    public void NoLoanOnAShortfallDay_BecauseThereIsNothingToRepayItWith()
    {
        var decision = Controller().Decide(Input(
            3000, charging: true, timeInState: TimeSpan.FromMinutes(20),
            plan: Plan(shortfallWh: 5000, outlook: DayOutlook.Shortfall)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal(0, decision.LoanPowerWatts);
    }

    [Fact]
    public void NoLoanOnceTheDailyLoanBudgetIsSpent()
    {
        var decision = Controller().Decide(Input(3000, charging: true, timeInState: TimeSpan.FromMinutes(20), loanedTodayWh: 4000));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal(0, decision.LoanPowerWatts);
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

        var decision = controller.Decide(Input(8000, charging: false, timeInState: TimeSpan.FromMinutes(5)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Contains("minimum before restarting", decision.Reason);
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
