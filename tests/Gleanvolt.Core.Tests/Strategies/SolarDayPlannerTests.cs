using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class SolarDayPlannerTests
{
    // 6A x 230V x 3 phases -- the three-phase floor the whole shoulder/plateau split turns on.
    private const double MinChargePowerWatts = 4140;
    private const double HouseBaselineWatts = 500;

    // A flat profile keeps these tests about the planner; the shape itself is HouseLoadProfile's job.
    private sealed class FlatHouseLoad(double watts) : IHouseLoadProfile
    {
        public double ExpectedWattsAt(DateTimeOffset instant) => watts;
    }
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
        bool enableLoan = false,
        double maxLoanPowerWatts = 0,
        double minSocFloorPercent = 50,
        double minBridgeSurplusWatts = 2000,
        double spillBridgeSurplusWatts = 400) =>
        new(
            BatteryCapacityWh: CapacityWh,
            ChargeEfficiency: 0.95,
            MinChargePowerWatts: MinChargePowerWatts,
            MaxLoanPowerWatts: maxLoanPowerWatts,
            EnableBatteryLoan: enableLoan,
            MinBridgeSurplusWatts: minBridgeSurplusWatts,
            SpillBridgeSurplusWatts: spillBridgeSurplusWatts,
            MinViableWindow: TimeSpan.FromMinutes(30),
            MinBatterySocFloorPercent: minSocFloorPercent);

    private static SolarDayPlan Plan(
        double socPercent,
        SolarForecast? forecast = null,
        SolarDayPlannerOptions? options = null,
        DateTimeOffset? now = null,
        double biasFactor = 1.0,
        DateTimeOffset? deadline = null) =>
        SolarDayPlanner.Plan(
            State(socPercent, now),
            forecast ?? Forecast(BellDay),
            deadline ?? Deadline,
            new FlatHouseLoad(HouseBaselineWatts),
            biasFactor,
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
        Assert.Contains("No chargeable window", plan.Reason);
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

    // Issue #223. The spill is the term that says whether a battery loan costs the house anything: what
    // the pack has no room for goes to the car or out of the meter, and nowhere else.
    [Fact]
    public void TheSpillIsTheSurplusThePackHasNoRoomFor()
    {
        // A full pack can absorb nothing, so every remaining watt of surplus is spill.
        var full = Plan(socPercent: 100);

        Assert.Equal(full.RemainingPvWh - full.ExpectedHouseWh, full.SpillWh, 1);
        Assert.True(full.WillSpill);
    }

    [Fact]
    public void ThereIsNoSpillWhileThePackCanStillTakeEverythingTheDayMakes()
    {
        // 9.25kWh of surplus to come against a pack 10kWh short of full, losses included: it all has a home.
        var empty = Plan(socPercent: 0);

        Assert.Equal(0, empty.SpillWh, 1);
        Assert.False(empty.WillSpill);
    }

    [Fact]
    public void TheLoanableEnergyIsTheRoomBetweenTheSocAndTheFloor()
    {
        // The bell day leaves the clamp binding at 50%, so a pack at 80% may lend 30% of 10kWh.
        var plan = Plan(socPercent: 80);

        Assert.Equal(50, plan.RequiredSocFloorPercent, 1);
        Assert.Equal(3000, plan.LoanableWh, 1);
    }

    [Fact]
    public void TheLoanHeadroomIsTheFreePartOfTheLoanableRoom()
    {
        // Lending that costs the house nothing: the room above the floor, capped by what the pack could
        // not have kept anyway. The cap is a statement rather than a constraint in practice -- the floor
        // is derived from the same remaining surplus the spill is, so it never permits lending more than
        // the day was going to give away -- and zero spill is what makes it zero, which is the reading
        // that matters on the dashboard.
        var spilling = Plan(socPercent: 100);
        var absorbing = Plan(socPercent: 55, forecast: Forecast([1000, 1000]));

        Assert.True(spilling.LoanHeadroomWh > 0);
        Assert.Equal(spilling.LoanableWh, spilling.LoanHeadroomWh, 1);
        Assert.Equal(0, absorbing.LoanHeadroomWh, 1);
    }

    // The disagreement the shared helper exists to end: the plan used to count a period feasible from
    // MinChargePowerWatts - MaxLoanPowerWatts while the controller refused to lend below
    // MinBridgeSurplusWatts, so every slice between the two was drawn as chargeable and silently was not.
    [Fact]
    public void TheFeasibilityThresholdIsNeverBelowTheSurplusFloorALoanIsGrantedFrom()
    {
        var options = Options(enableLoan: true, maxLoanPowerWatts: 3800);

        Assert.Equal(2000, SolarDayPlanner.MinSolarPowerWatts(options, spilling: false), 1);
        Assert.Equal(400, SolarDayPlanner.MinSolarPowerWatts(options, spilling: true), 1);
    }

    [Fact]
    public void WithoutTheLoanTheThresholdIsTheChargersOwnFloor()
    {
        Assert.Equal(
            MinChargePowerWatts,
            SolarDayPlanner.MinSolarPowerWatts(Options(enableLoan: false), spilling: true),
            1);
    }

    [Fact]
    public void AFullPackGetsAWindowOnADayNoPeriodOfWhichClearsTheBridgeFloor()
    {
        // 1.5kW of surplus all afternoon: under the charger's 4.14kW floor, under the 2kW a loan is
        // normally granted from, and every watt of it export while the pack is full. That is the day the
        // request behind #223 described, and it has a window now.
        var forecast = Forecast([2000, 2000, 2000, 2000]);
        var options = Options(enableLoan: true, maxLoanPowerWatts: 3800);

        var full = Plan(socPercent: 100, forecast, options);
        var roomToSpare = Plan(socPercent: 40, forecast, options);

        Assert.NotNull(full.NextFeasibleWindow);
        Assert.True(full.FeasibleEvEnergyWh > 0);

        // The same weather with room in the pack stays the battery's: nothing spills, so the thin-surplus
        // rules never open, and the shoulder belongs to the pack as it always did.
        Assert.Null(roomToSpare.NextFeasibleWindow);
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
    public void TheTrajectoryFloorIsReportedSeparatelyFromTheClampAboveIt()
    {
        // Same bell day. The floor in force is the 50% clamp, but the forecast on its own would allow
        // the pack down to ~12% -- the distinction the controller uses to decide how hard to stop.
        var plan = Plan(socPercent: 60);

        Assert.Equal(50, plan.RequiredSocFloorPercent, 1);
        Assert.Equal(12, plan.TrajectorySocFloorPercent, 0);
        Assert.True(plan.FloorIsClamped);
    }

    [Fact]
    public void WhereTheTrajectoryBindsTheTwoFloorsAgree()
    {
        // A thin day: the trajectory is already above the clamp, so the clamp adds nothing and a breach
        // of the floor is a breach of the forecast's own requirement.
        var plan = Plan(socPercent: 90, Forecast([2500, 2500, 2500, 2500]));

        Assert.Equal(plan.RequiredSocFloorPercent, plan.TrajectorySocFloorPercent, 3);
        Assert.False(plan.FloorIsClamped);
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
        var plan = SolarDayPlanner.Plan(State(50), forecast: null, Deadline, new FlatHouseLoad(HouseBaselineWatts), 1.0, Options());

        Assert.False(plan.IsUsable);
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

    // Issue #217: the shortfall lost its EV term with the daily target. It measures the day against
    // the house plus a full battery -- the evening 100% -- and nothing else. The car is not in the
    // sum at all, because the car only ever gets what is left over once both are served.
    [Fact]
    public void AShortfallIsReportedWhenTheDayCannotCoverTheHouseAndFillTheBattery()
    {
        var plan = Plan(socPercent: 40, Forecast([1000, 5000, 5000, 1000]));

        Assert.True(plan.HasShortfall);
        Assert.Equal(plan.ExpectedHouseWh + plan.BatteryToFullWh - plan.RemainingPvWh, plan.ShortfallWh, 1);
    }

    [Fact]
    public void NoShortfallWhenTheDayCoversTheHouseAndTheBatteryToFull()
    {
        // A nearly full battery on a bell day: the sun has both covered with room to spare, so the
        // evening 100% is not at risk however little the car ends up getting.
        var plan = Plan(socPercent: 96);

        Assert.False(plan.HasShortfall);
        Assert.Equal(0, plan.ShortfallWh);
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
    }

    // A profile with a different load per hour, to show the plan uses the load expected *then*.
    private sealed class ShapedHouseLoad(Func<int, double> wattsForHour) : IHouseLoadProfile
    {
        public double ExpectedWattsAt(DateTimeOffset instant) => wattsForHour(instant.UtcDateTime.Hour);
    }

    [Fact]
    public void TheHouseLoadIsTakenPerPeriod_NotAsOneFigureForTheWholeDay()
    {
        // The morning is quiet and the afternoon is busy. A single trailing average sampled in the
        // afternoon would price the whole remaining day at the afternoon's load and wipe out the
        // budget; the profile prices each period at its own hour.
        var forecast = Forecast([6000, 6000, 6000, 6000, 6000, 6000]);
        var shaped = new ShapedHouseLoad(hour => hour >= 11 ? 5000 : 500);

        var withShape = SolarDayPlanner.Plan(
            State(96), forecast, Deadline, shaped, 1.0, Options());
        var withAfternoonEverywhere = SolarDayPlanner.Plan(
            State(96), forecast, Deadline, new FlatHouseLoad(5000), 1.0, Options());

        Assert.True(
            withShape.FeasibleEvEnergyWh > withAfternoonEverywhere.FeasibleEvEnergyWh,
            "pricing the quiet morning at the busy afternoon's load throws away the car's window");
        Assert.NotNull(withShape.NextFeasibleWindow);
    }

    // The web UI's plan timeline (issue #50) is built from SolarDayPlan.Timeline rather than a
    // second pass over the forecast, so its correctness is the planner's to guarantee.
    [Fact]
    public void TheTimelineHasOnePointPerForecastPeriod()
    {
        var plan = Plan(socPercent: 96);

        Assert.Equal(BellDay.Length, plan.Timeline.Count);
    }

    [Fact]
    public void TheTimelineIsChronological()
    {
        var plan = Plan(socPercent: 96);

        Assert.Equal(plan.Timeline.OrderBy(p => p.Start), plan.Timeline);
    }

    [Fact]
    public void TheTimelineCarriesEachPeriodsSurplusAndWhetherItClearsThePlateau()
    {
        var plan = Plan(socPercent: 96);

        // Same 1000W/6000W bell shape as SplitsTheDayIntoShoulderAndPlateauEnergy, read back per point
        // instead of summed: 500W shoulder surplus, 5500W plateau surplus, split at the 4140W floor.
        Assert.All(plan.Timeline.Take(2), p => Assert.False(p.IsPlateau));
        Assert.All(plan.Timeline.Take(2), p => Assert.Equal(500, p.SurplusWatts, 1));

        Assert.All(plan.Timeline.Skip(2).Take(3), p => Assert.True(p.IsPlateau));
        Assert.All(plan.Timeline.Skip(2).Take(3), p => Assert.Equal(5500, p.SurplusWatts, 1));
    }

    [Fact]
    public void TheFirstTimelinePointsFloorMatchesThePlansOwnFloor()
    {
        // Plan.RequiredSocFloorPercent is "the floor if now were now"; Timeline[0].RequiredSocFloorPercent
        // is "the floor if now were the first slice's start" -- the same quantity, since nothing lies
        // between them when the plan is built exactly at a period boundary.
        var plan = Plan(socPercent: 70);

        Assert.Equal(plan.RequiredSocFloorPercent, plan.Timeline[0].RequiredSocFloorPercent, 3);
    }

    [Fact]
    public void TheRequiredFloorRisesTowardsTheEndOfTheDayAlongTheTimeline()
    {
        // Less surplus lies ahead of a later point, so the pack has less room to recover from --
        // exactly what squeezes the car out of the late afternoon, now visible period by period rather
        // than only as the single current-moment figure.
        var plan = Plan(socPercent: 70);

        Assert.True(
            plan.Timeline[^1].RequiredSocFloorPercent >= plan.Timeline[0].RequiredSocFloorPercent,
            "the floor at the last period should be no lower than at the first");
    }

    [Fact]
    public void TheTimelineIsEmptyWhenThePlanIsUnusable()
    {
        var plan = SolarDayPlan.Unavailable(Deadline, "no forecast fetched yet");

        Assert.Empty(plan.Timeline);
    }
}

