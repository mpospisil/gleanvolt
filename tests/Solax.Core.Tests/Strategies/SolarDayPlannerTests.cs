using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Core.Tests.Strategies;

public class SolarDayPlannerTests
{
    // 6A x 230V x 3 phases -- the three-phase floor the whole shoulder/plateau split turns on.
    private const double MinChargePowerWatts = 4140;
    private const double HouseBaselineWatts = 500;
    private const double CapacityWh = 10_000;

    private static readonly DateTimeOffset FirstPeriodEnd = new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Deadline = new(2026, 7, 27, 19, 0, 0, TimeSpan.Zero);

    // A day shaped like a real one: shoulders that only the battery can use, a plateau in the middle
    // that clears the charger's minimum. Period ends run 09:00, 09:30, ... every 30 minutes.
    private static readonly double[] BellDay = [1000, 1000, 6000, 6000, 6000, 1000, 1000];

    private static SolarForecast Forecast(params double[] watts) =>
        new(
            FirstPeriodEnd.AddHours(-1),
            [.. watts.Select((w, i) => new SolarForecastPeriod(
                FirstPeriodEnd.AddMinutes(30 * i), TimeSpan.FromMinutes(30), w, EstimatedPowerWattsP10: w))]);

    private static EnergyState State(double socPercent, DateTimeOffset? at = null) =>
        new(at ?? FirstPeriodEnd.AddMinutes(-30), socPercent, BatteryPowerWatts: 0, SolarPowerWatts: 0,
            GridPowerWatts: 0, EvChargerStatus.Available, EvChargerPowerWatts: 0);

    private static SolarDayPlannerOptions Options(
        double dailyEvTargetWh = 15_000,
        bool enableLoan = false,
        double maxLoanPowerWatts = 0,
        double minSocFloorPercent = 50) =>
        new(
            BatteryCapacityWh: CapacityWh,
            ChargeEfficiency: 0.95,
            MinChargePowerWatts: MinChargePowerWatts,
            MaxLoanPowerWatts: maxLoanPowerWatts,
            EnableBatteryLoan: enableLoan,
            MinViableWindow: TimeSpan.FromMinutes(30),
            MinBatterySocFloorPercent: minSocFloorPercent,
            DailyEvTargetWh: dailyEvTargetWh);

    private static SolarDayPlan Plan(
        double socPercent,
        SolarForecast? forecast = null,
        SolarDayPlannerOptions? options = null,
        DateTimeOffset? now = null,
        double biasFactor = 1.0,
        double evDeliveredTodayWh = 0,
        DateTimeOffset? deadline = null) =>
        SolarDayPlanner.Plan(
            State(socPercent, now),
            forecast ?? Forecast(BellDay),
            deadline ?? Deadline,
            HouseBaselineWatts,
            biasFactor,
            evDeliveredTodayWh,
            options ?? Options());

    [Fact]
    public void SplitsTheDayIntoShoulderAndPlateauEnergy()
    {
        var plan = Plan(socPercent: 96);

        // Four 1000W periods leave 500W of surplus each for 30 minutes; three 6000W periods leave 5500W.
        Assert.Equal(1000, plan.ShoulderEnergyWh, 1);
        Assert.Equal(8250, plan.PlateauEnergyWh, 1);
    }

    [Fact]
    public void WhenTheShouldersCoverTheBattery_TheWholePlateauGoesToTheCar()
    {
        // 96% of a 10kWh pack needs ~421Wh, comfortably inside the 500Wh of late-afternoon shoulder.
        var plan = Plan(socPercent: 96);

        Assert.Equal(0, plan.PlateauClaimedByBatteryWh, 1);
        Assert.Equal(8250, plan.FeasibleEvEnergyWh, 1);

        // 8.25kWh against a 15kWh target is a tight day, not a shortfall: the car gets everything the
        // plateau has, which is the most this strategy can do for it.
        Assert.Equal(DayOutlook.Tight, plan.Outlook);
    }

    [Fact]
    public void WhenTheShouldersFallShort_TheBatteryClaimsTheLatestPlateauAndTheCarKeepsTheEarliest()
    {
        var plan = Plan(socPercent: 70);

        Assert.True(plan.PlateauClaimedByBatteryWh > 0, "the battery has to reach into the plateau at 70%");

        // Booked backwards from the deadline, so the earliest chargeable period is still the car's.
        var window = Assert.IsType<(DateTimeOffset Start, DateTimeOffset End)>(plan.NextFeasibleWindow);
        Assert.Equal(FirstPeriodEnd.AddMinutes(30), window.Start);
    }

    [Fact]
    public void ADayThatNeverClearsTheChargersMinimum_IsAllShoulderAndOffersNothingToTheCar()
    {
        // Plenty of energy in total (7 x 1kW half-hours), but never at a power the car can accept.
        var plan = Plan(socPercent: 96, Forecast([1000, 1000, 1000, 1000, 1000, 1000, 1000]));

        Assert.Equal(0, plan.PlateauEnergyWh, 1);
        Assert.Equal(0, plan.FeasibleEvEnergyWh, 1);
        Assert.Null(plan.NextFeasibleWindow);
        Assert.Equal(DayOutlook.NoChargeToday, plan.Outlook);
    }

    [Fact]
    public void EnablingTheLoanWidensFeasibilityToPeriodsWithinBridgingReach()
    {
        // 3.5kW of surplus is below the 4.14kW floor, so unreachable on sun alone -- but within reach
        // of a 2.5kW loan.
        var forecast = Forecast([4000, 4000, 4000, 4000]);

        var withoutLoan = Plan(socPercent: 96, forecast);
        var withLoan = Plan(socPercent: 96, forecast, Options(enableLoan: true, maxLoanPowerWatts: 2500));

        Assert.Null(withoutLoan.NextFeasibleWindow);
        Assert.NotNull(withLoan.NextFeasibleWindow);
        Assert.True(withLoan.FeasibleEvEnergyWh > 0);
    }

    [Fact]
    public void TheSocFloorSitsAtTheHardMinimumWhileThereIsPlentyOfDayLeft()
    {
        // The bell day still has 9.25kWh of surplus to come against a 10kWh pack: the trajectory would
        // allow draining it almost completely, and only the hard floor stops that.
        var plan = Plan(socPercent: 60);

        Assert.Equal(50, plan.RequiredSocFloorPercent, 1);
    }

    [Fact]
    public void TheSocFloorTracksWhatTheRemainingForecastCanActuallyRefill()
    {
        // A thin day: four half-hours at 2kW of surplus is 4kWh, worth 38% of a 10kWh pack after charge
        // losses, so the trajectory allows SOC down to 62% and no further.
        var plan = Plan(socPercent: 90, Forecast([2500, 2500, 2500, 2500]));

        Assert.Equal(62, plan.RequiredSocFloorPercent, 0);
    }

    [Fact]
    public void TheSocFloorRisesToHundredAsTheDayRunsOut()
    {
        // Late afternoon, one weak period left: nothing can refill the battery, so nothing may be spent.
        var lateAfternoon = new DateTimeOffset(2026, 7, 27, 18, 40, 0, TimeSpan.Zero);
        var forecast = new SolarForecast(
            lateAfternoon.AddHours(-2),
            [new SolarForecastPeriod(new DateTimeOffset(2026, 7, 27, 19, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(30), 200, 200)]);

        var plan = Plan(socPercent: 85, forecast, now: lateAfternoon);

        Assert.Equal(100, plan.RequiredSocFloorPercent, 1);
    }

    [Fact]
    public void AMissingForecastYieldsAnUnusablePlanRatherThanAnOptimisticOne()
    {
        var plan = SolarDayPlanner.Plan(State(50), forecast: null, Deadline, HouseBaselineWatts, 1.0, 0, Options());

        Assert.False(plan.IsUsable);
        Assert.Equal(DayOutlook.Unknown, plan.Outlook);
        Assert.Equal(0, plan.FeasibleEvEnergyWh);
        Assert.Equal(100, plan.RequiredSocFloorPercent);
    }

    [Fact]
    public void TheBiasFactorScalesTheRemainingForecast()
    {
        var full = Plan(socPercent: 96);
        var halved = Plan(socPercent: 96, biasFactor: 0.5);

        Assert.True(halved.RemainingPvWh < full.RemainingPvWh);
        Assert.Equal(full.RemainingPvWh * 0.5, halved.RemainingPvWh, 1);
    }

    [Fact]
    public void AShortfallIsReportedWhenTheDayCannotCoverHouseBatteryAndCar()
    {
        // A thin day against a 15kWh EV target: the car cannot have what the sun won't make.
        var plan = Plan(socPercent: 40, Forecast([1000, 5000, 5000, 1000]));

        Assert.True(plan.HasShortfall);
        Assert.True(plan.EvExpectedTodayWh < plan.EvTargetWh);
        Assert.NotEqual(DayOutlook.Surplus, plan.Outlook);
    }

    [Fact]
    public void MeetingTheDailyTargetAlreadyReadsAsSurplus()
    {
        var plan = Plan(socPercent: 96, evDeliveredTodayWh: 15_000);

        Assert.Equal(DayOutlook.Surplus, plan.Outlook);
        Assert.False(plan.HasShortfall);
    }

    [Fact]
    public void PastTheDeadline_PlanningExtendsToTheEndOfProductionSoLateSunStillReachesTheCar()
    {
        // Summer evening: the deadline has passed, the battery is full, and there is still sun.
        var evening = new DateTimeOffset(2026, 7, 27, 19, 30, 0, TimeSpan.Zero);
        var forecast = new SolarForecast(
            evening.AddHours(-3),
            [
                new SolarForecastPeriod(new DateTimeOffset(2026, 7, 27, 20, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(30), 6000, 6000),
                new SolarForecastPeriod(new DateTimeOffset(2026, 7, 27, 20, 30, 0, TimeSpan.Zero), TimeSpan.FromMinutes(30), 6000, 6000),
            ]);

        var plan = Plan(socPercent: 100, forecast, now: evening);

        Assert.NotNull(plan.NextFeasibleWindow);
        Assert.True(plan.FeasibleEvEnergyWh > 0);
    }

    [Fact]
    public void AWindowShorterThanTheMinimumViableStretchIsNotOffered()
    {
        // A single 30-minute plateau against a 60-minute minimum: not worth starting a session for.
        var options = Options() with { MinViableWindow = TimeSpan.FromMinutes(60) };
        var plan = Plan(socPercent: 96, Forecast([1000, 6000, 1000, 1000]), options);

        Assert.Null(plan.NextFeasibleWindow);
        Assert.Equal(DayOutlook.NoChargeToday, plan.Outlook);
    }
}
