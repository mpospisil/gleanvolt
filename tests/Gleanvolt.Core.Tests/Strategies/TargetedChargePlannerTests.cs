using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class TargetedChargePlannerTests
{
    // 6A and 16A x 230V x 3 phases: the charger's floor and ceiling, which every decision here turns on.
    private const double MinChargePowerWatts = 4140;
    private const double MaxChargePowerWatts = 11_040;
    private const double HouseBaselineWatts = 500;
    private const double CapacityWh = 10_000;

    private static readonly DateTimeOffset FirstPeriodEnd = new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

    // The battery's own deadline, far enough out that it never truncates the planning window here.
    private static readonly DateTimeOffset BatteryFullBy = new(2026, 7, 28, 19, 0, 0, TimeSpan.Zero);

    // Period ends run 09:00, 09:30, ... every 30 minutes. The plateau in the middle is the only part
    // the car can take; the shoulders are the battery's.
    private static readonly double[] BellDay = [1000, 1000, 6000, 6000, 6000, 1000, 1000];

    private sealed class FlatHouseLoad(double watts) : IHouseLoadProfile
    {
        public double ExpectedWattsAt(DateTimeOffset instant) => watts;
    }

    private static SolarForecast Forecast(params double[] watts) =>
        new(
            FirstPeriodEnd.AddHours(-1),
            [.. watts.Select((w, i) => new SolarForecastPeriod(
                FirstPeriodEnd.AddMinutes(30 * i), TimeSpan.FromMinutes(30), w, EstimatedPowerWattsP10: w))]);

    private static EnergyState State(double socPercent, DateTimeOffset at) =>
        new(at, socPercent, BatteryPowerWatts: 0, SolarPowerWatts: 0,
            GridPowerWatts: 0, EvChargerStatus.Charging, EvChargerPowerWatts: 0);

    private static TargetedChargePlannerOptions Options(
        double minSocFloorPercent = 50,
        TimeSpan? safetyMargin = null,
        TimeSpan? releaseSlack = null) =>
        new(
            BatteryCapacityWh: CapacityWh,
            ChargeEfficiency: 0.95,
            MinChargePowerWatts: MinChargePowerWatts,
            MaxChargePowerWatts: MaxChargePowerWatts,
            MinBatterySocFloorPercent: minSocFloorPercent,
            SafetyMargin: safetyMargin ?? TimeSpan.Zero,
            ReleaseSlack: releaseSlack ?? TimeSpan.Zero);

    private static TargetedChargePlan Plan(
        double requiredWh,
        DateTimeOffset departBy,
        DateTimeOffset now,
        double socPercent = 96,
        SolarForecast? forecast = null,
        double deliveredWh = 0,
        TargetedChargePlannerOptions? options = null,
        DateTimeOffset? batteryFullBy = null,
        double tailWh = 0) =>
        TargetedChargePlanner.Plan(
            State(socPercent, now),
            new TargetedChargeRequest(requiredWh, departBy, ActivatedAt: now)
            {
                Priority = tailWh > 0 ? TargetedChargePriority.JustInTime : TargetedChargePriority.Cheapest,
                TailEnergyWh = tailWh,
            },
            deliveredWh,
            forecast ?? Forecast(BellDay),
            batteryFullBy ?? BatteryFullBy,
            new FlatHouseLoad(HouseBaselineWatts),
            biasFactor: 1.0,
            options ?? Options());

    // --- Regime A: not enough time ---

    [Fact]
    public void WhenTheRequestExceedsWhatTheChargerCanDeliverInTime_ItTakesEverythingAndSaysHowShort()
    {
        // Two hours at 11.04kW is 22.08kWh; 30kWh cannot be done, dark or sunny.
        var now = FirstPeriodEnd.AddHours(-1);
        var plan = Plan(requiredWh: 30_000, departBy: now.AddHours(2), now: now);

        Assert.Equal(TargetedChargeStrategy.Maximum, plan.Strategy);
        Assert.Equal(2 * MaxChargePowerWatts, plan.CeilingEnergyWh, 1);
        Assert.Equal(2 * MaxChargePowerWatts, plan.ExpectedEnergyWh, 1);
        Assert.Equal(30_000 - (2 * MaxChargePowerWatts), plan.ShortfallWh, 1);
    }

    [Fact]
    public void TheReportedFeasibleDeparture_IsTheOneThatWouldHaveCoveredTheRequest()
    {
        var now = FirstPeriodEnd.AddHours(-1);
        var departBy = now.AddHours(2);
        var plan = Plan(requiredWh: 30_000, departBy: departBy, now: now);

        // The shortfall, at full power, added to the departure that was asked for.
        var extra = TimeSpan.FromHours((30_000 - (2 * MaxChargePowerWatts)) / MaxChargePowerWatts);
        Assert.Equal(departBy + extra, plan.FeasibleDeparture);
    }

    [Fact]
    public void RegimeA_ChargesFlatOutFromNowUntilTheDeadline()
    {
        var now = FirstPeriodEnd.AddHours(-1);
        var departBy = now.AddHours(2);
        var plan = Plan(requiredWh: 30_000, departBy: departBy, now: now);

        // Every remaining minute is claimed: the grid block starts now, and where the sun contributes
        // it is a solar block on top of it rather than a gap.
        Assert.Equal(now, plan.GridStart);
        Assert.True(plan.IsInGridBlock(now));
        Assert.True(plan.IsInGridBlock(departBy.AddMinutes(-1)));
        Assert.Equal(plan.ExpectedEnergyWh, plan.SolarEnergyWh + plan.GridEnergyWh, 1);
    }

    // --- Regime B: enough time ---

    [Fact]
    public void WhenTheSunCoversTheNeed_NoGridIsPlannedAtAll()
    {
        // The plateau alone offers 3 x 5.5kW half-hours = 8.25kWh, and 96% SOC leaves the shoulders
        // covering the battery. Asking for 5kWh is comfortably inside that.
        var plan = Plan(requiredWh: 5_000, departBy: FirstPeriodEnd.AddHours(4), now: FirstPeriodEnd.AddMinutes(-30));

        Assert.Equal(TargetedChargeStrategy.Solar, plan.Strategy);
        Assert.Equal(0, plan.GridEnergyWh, 1);
        Assert.Null(plan.GridStart);
        Assert.Equal(5_000, plan.SolarEnergyWh, 1);
        Assert.Equal(0, plan.ShortfallWh, 1);
    }

    [Fact]
    public void TheSolarShareIsTakenEarliestFirst()
    {
        var now = FirstPeriodEnd.AddMinutes(-30);
        var plan = Plan(requiredWh: 5_000, departBy: FirstPeriodEnd.AddHours(4), now: now);

        // 5kWh out of 5.5kW plateau slices: the first chargeable slice starts it, and the block stops
        // part-way through the second rather than waiting for the sunniest moment.
        var solar = plan.Blocks.Where(b => b.Source == TargetedChargeSource.Solar).OrderBy(b => b.Start).ToList();
        Assert.Equal(FirstPeriodEnd.AddMinutes(30), solar[0].Start);
        Assert.True(solar[^1].End < FirstPeriodEnd.AddHours(2), "the last solar block should end inside its slice");
    }

    [Fact]
    public void WithNoSunAnywhereInTheWindow_TheGridBlockStartsAtOnce()
    {
        // Overnight: plugged in at 21:00 for an 07:00 departure, with no sun in the window at all —
        // today's bell curve is hours behind us.
        var now = new DateTimeOffset(2026, 7, 27, 21, 0, 0, TimeSpan.Zero);
        var departBy = new DateTimeOffset(2026, 7, 28, 7, 0, 0, TimeSpan.Zero);
        var plan = Plan(requiredWh: 22_000, departBy: departBy, now: now, forecast: Forecast(BellDay));

        Assert.Equal(TargetedChargeStrategy.SolarPlusGrid, plan.Strategy);
        Assert.Equal(0, plan.SolarEnergyWh, 1);

        // No sun anywhere, so the grid carries all of it -- starting now and spread across the night
        // rather than crammed into two hours at full power. 22kWh over ten hours is a 2.2kW pace, under
        // the charger's floor, so it runs at the floor for part of each slice.
        Assert.Equal(now, plan.GridStart!.Value);
        Assert.True(plan.IsInGridBlock(now));
        Assert.Equal(22_000, plan.GridEnergyWh, 1);

        // Spread, not crammed: flat out this would be over in two hours. At the paced rate it takes
        // more than twice that, which is the whole point -- every extra hour is an hour the sun could
        // have displaced the import, on a window that happened not to have any.
        var duration = plan.Blocks.Max(b => b.End) - plan.Blocks.Min(b => b.Start);
        Assert.True(
            duration > TimeSpan.FromHours(4),
            $"charge ran for {duration.TotalHours:F1}h; flat out would be {22_000 / MaxChargePowerWatts:F1}h");
    }

    [Fact]
    public void WhereTheSunAndTheGridShareASlice_TheirEnergiesDoNotDoubleCount()
    {
        // A day of nothing but plateau, so the grid block is forced to reach back over sunny slices.
        // Six 30-minute slices at 6kW give a 5.5kW surplus each: 16.5kWh of sun over three hours,
        // against a 25kWh ask that needs the grid for the rest.
        var now = FirstPeriodEnd.AddMinutes(-30);
        var departBy = FirstPeriodEnd.AddHours(2.5);
        // A full pack, so nothing of the surplus is reserved and the arithmetic is only about the two
        // sources meeting inside one slice.
        var plan = Plan(
            requiredWh: 25_000,
            departBy: departBy,
            now: now,
            socPercent: 100,
            forecast: Forecast(6000, 6000, 6000, 6000, 6000, 6000));

        Assert.Equal(TargetedChargeStrategy.SolarPlusGrid, plan.Strategy);

        // Where both contribute to one slice they overlap in time, and the grid supplies only what the
        // sun does not: never more than the charger's ceiling less the 5.5kW the roof is giving.
        var gridBlocks = plan.Blocks.Where(b => b.Source == TargetedChargeSource.Grid).ToList();
        Assert.NotEmpty(gridBlocks);
        Assert.All(gridBlocks, b => Assert.True(
            b.PowerWatts <= MaxChargePowerWatts - 5500 + 1, $"grid block drew {b.PowerWatts:F0}W over a 5.5kW slice"));

        // The sun takes as much of the target as it can, and the two shares add up to exactly what was
        // asked for: no double counting.
        Assert.True(plan.SolarEnergyWh >= 16_000, $"solar share was {plan.SolarEnergyWh:F0}Wh of a 16.5kWh plateau");
        Assert.Equal(25_000, plan.SolarEnergyWh + plan.GridEnergyWh, 1);
        Assert.Equal(0, plan.ShortfallWh, 1);
    }

    [Fact]
    public void ThePlateauCarriesMoreOfTheTargetThanTheShoulders()
    {
        // A bell day whose peak is the middle three half-hours. The car cannot charge from the 500W
        // shoulders, so the whole 12kWh falls to the grid — and it is the plateau, not the tail of the
        // window, that gets it: importing there is importing at 11.04kW while the roof supplies part of
        // it, which is the entire reason the placement rule exists.
        var now = FirstPeriodEnd.AddMinutes(-30);
        var departBy = FirstPeriodEnd.AddHours(3);
        var plan = Plan(
            requiredWh: 12_000,
            departBy: departBy,
            now: now,
            socPercent: 100,
            forecast: Forecast(1000, 1000, 4000, 4000, 4000, 1000));

        Assert.Equal(TargetedChargeStrategy.SolarPlusGrid, plan.Strategy);

        // Neither the 500W shoulders nor the 3.5kW plateau clears the charger's floor, so under the old
        // plan every one of these 12kWh was grid. Paced, the sun carries a real share of it.
        Assert.True(plan.SolarEnergyWh > 4_000, $"solar share was {plan.SolarEnergyWh:F0}Wh");
        Assert.Equal(12_000, plan.SolarEnergyWh + plan.GridEnergyWh, 1);

        // The plateau slices contribute more per half-hour than the shoulders, because the charger runs
        // at the same floor through both and the roof covers more of it in the middle of the day.
        var plateau = plan.Blocks
            .Where(b => b.Source == TargetedChargeSource.Solar && b.Start >= FirstPeriodEnd.AddHours(0.5) && b.Start < FirstPeriodEnd.AddHours(2))
            .Sum(b => b.EnergyWh);
        var shoulder = plan.Blocks
            .Where(b => b.Source == TargetedChargeSource.Solar && b.Start < FirstPeriodEnd.AddHours(0.5))
            .Sum(b => b.EnergyWh);

        Assert.True(plateau > shoulder, $"plateau {plateau:F0}Wh should beat shoulder {shoulder:F0}Wh");
    }

    [Fact]
    public void AWindowThatIsSunnyThroughout_TakesTheSunFirstAndBuysOnlyTheRemainder()
    {
        // Nothing but plateau: 5.5kW of surplus every half-hour, 16.5kWh over the window, against a
        // 25kWh ask. The sun is taken in full from the first slice, and the grid only makes up the
        // 8.5kWh it cannot reach -- so the charge starts on sun, not on an import.
        var now = FirstPeriodEnd.AddMinutes(-30);
        var departBy = FirstPeriodEnd.AddHours(2.5);
        var plan = Plan(
            requiredWh: 25_000, departBy: departBy, now: now, socPercent: 100,
            forecast: Forecast(6000, 6000, 6000, 6000, 6000, 6000));

        Assert.Equal(now, plan.Blocks.Where(b => b.Source == TargetedChargeSource.Solar).Min(b => b.Start));
        Assert.True(plan.SolarEnergyWh >= 16_000, $"solar share was {plan.SolarEnergyWh:F0}Wh");

        // And nothing runs past the deadline.
        Assert.All(plan.Blocks, b => Assert.True(b.End <= departBy, $"{b.Source} block ends past the deadline"));
    }

    // --- Degrading, and the edges ---

    [Fact]
    public void WithNoForecastAtAll_ThePlanIsGridOnlyAndStillMeetsTheTarget()
    {
        var now = new DateTimeOffset(2026, 7, 27, 21, 0, 0, TimeSpan.Zero);
        var departBy = new DateTimeOffset(2026, 7, 28, 7, 0, 0, TimeSpan.Zero);

        var plan = TargetedChargePlanner.Plan(
            State(96, now),
            new TargetedChargeRequest(22_000, departBy, now),
            deliveredWh: 0,
            forecast: null,
            BatteryFullBy,
            new FlatHouseLoad(HouseBaselineWatts),
            biasFactor: 1.0,
            Options());

        Assert.False(plan.IsUsable);
        Assert.Equal(TargetedChargeStrategy.SolarPlusGrid, plan.Strategy);
        Assert.Equal(22_000, plan.GridEnergyWh, 1);
        Assert.Equal(0, plan.ShortfallWh, 1);
        Assert.Contains("grid-only", plan.Reason);

        // Planning blind is planning with nothing to wait for, so it starts now.
        Assert.Equal(now, plan.GridStart!.Value);
    }

    [Fact]
    public void TheCeilingCoversHoursTheForecastSaysNothingAbout()
    {
        // The forecast stops at 12:30 but the departure is at 07:00 the next morning: the dark hours
        // in between are chargeable at full power, and a ceiling that ignored them would report a
        // shortfall that does not exist.
        var now = FirstPeriodEnd.AddMinutes(-30);
        var departBy = new DateTimeOffset(2026, 7, 28, 7, 0, 0, TimeSpan.Zero);
        var plan = Plan(requiredWh: 22_000, departBy: departBy, now: now);

        Assert.Equal((departBy - now).TotalHours * MaxChargePowerWatts, plan.CeilingEnergyWh, 1);
        Assert.NotEqual(TargetedChargeStrategy.Maximum, plan.Strategy);
    }

    [Fact]
    public void TheBatteryKeepsItsPriority_APackThatNeedsTheWholeDayLeavesTheCarAlmostNothing()
    {
        // 20% SOC needs ~8.4kWh with losses, which swallows the shoulders and the whole plateau.
        var now = FirstPeriodEnd.AddMinutes(-30);
        var plan = Plan(requiredWh: 10_000, departBy: FirstPeriodEnd.AddHours(3), now: now, socPercent: 20);

        // The pack books the day out from under the car: what is left is crumbs, and the grid carries
        // essentially the whole target.
        Assert.True(plan.SolarEnergyWh < 1_000, $"solar share was {plan.SolarEnergyWh:F0}Wh");
        Assert.Equal(TargetedChargeStrategy.SolarPlusGrid, plan.Strategy);
        Assert.True(plan.GridEnergyWh > 9_000, $"grid share was {plan.GridEnergyWh:F0}Wh");
    }

    [Fact]
    public void ASliceWhoseSurplusMissesTheChargersMinimum_IsNowWorthSomething()
    {
        // 4kW of PV less 500W of house is 3.5kW: real energy, but below the 4.14kW the charger needs.
        var now = FirstPeriodEnd.AddMinutes(-30);
        var plan = Plan(
            requiredWh: 10_000,
            departBy: FirstPeriodEnd.AddHours(3),
            now: now,
            forecast: Forecast(4000, 4000, 4000, 4000, 4000, 4000));

        // The old plan scored this at zero solar: 3.5kW cannot run the charger alone, so the whole
        // 10kWh was bought. Under a pace the charger runs at its floor anyway and the 3.5kW comes
        // straight off the meter -- so most of that surplus now reaches the car.
        Assert.True(plan.SolarEnergyWh > 6_000, $"solar share was {plan.SolarEnergyWh:F0}Wh");
        Assert.True(plan.GridEnergyWh < 4_000, $"grid share was {plan.GridEnergyWh:F0}Wh");
        Assert.Equal(10_000, plan.SolarEnergyWh + plan.GridEnergyWh, 1);

        // And it starts straight away rather than waiting for sun that never clears the floor.
        Assert.Equal(now, plan.GridStart!.Value);
    }

    [Fact]
    public void ADarkWindowIsReportedAsHavingNoSurplusAtAll()
    {
        // The other side of the same distinction: overnight there really is nothing, and the plan must
        // not be made to sound like the weak-sun case by the fix for it.
        var now = new DateTimeOffset(2026, 7, 27, 21, 0, 0, TimeSpan.Zero);
        var plan = Plan(requiredWh: 22_000, departBy: new DateTimeOffset(2026, 7, 28, 7, 0, 0, TimeSpan.Zero), now: now);

        Assert.Equal(0, plan.ForecastSurplusWh, 1);
        Assert.False(plan.HasUnusableSurplus);
    }

    [Fact]
    public void TheForecastSurplusIsWhatIsLeftAfterTheHouseAndThePack()
    {
        // A full pack books nothing, so the whole plateau surplus is on offer to the car -- and here it
        // does clear the floor, so the two figures agree.
        var now = FirstPeriodEnd.AddMinutes(-30);
        var plan = Plan(
            requiredWh: 25_000, departBy: FirstPeriodEnd.AddHours(2.5), now: now, socPercent: 100,
            forecast: Forecast(6000, 6000, 6000, 6000, 6000, 6000));

        // Six 30-minute slices at 5.5kW of surplus.
        Assert.Equal(6 * 2_750, plan.ForecastSurplusWh, 1);
        Assert.False(plan.HasUnusableSurplus);
    }

    [Fact]
    public void OnceTheTargetHasBeenDelivered_ThePlanIsComplete()
    {
        var now = FirstPeriodEnd.AddMinutes(-30);
        var plan = Plan(requiredWh: 10_000, departBy: FirstPeriodEnd.AddHours(3), now: now, deliveredWh: 10_000);

        Assert.Equal(TargetedChargeStrategy.Complete, plan.Strategy);
        Assert.True(plan.IsComplete);
        Assert.Equal(0, plan.RemainingEnergyWh, 1);
        Assert.Empty(plan.Blocks);
    }

    [Fact]
    public void DeliveredEnergyShrinksWhatIsStillPlannedFor()
    {
        var now = new DateTimeOffset(2026, 7, 27, 21, 0, 0, TimeSpan.Zero);
        var departBy = new DateTimeOffset(2026, 7, 28, 7, 0, 0, TimeSpan.Zero);
        var plan = Plan(requiredWh: 22_000, departBy: departBy, now: now, deliveredWh: 8_000);

        Assert.Equal(14_000, plan.RemainingEnergyWh, 1);
        Assert.Equal(14_000, plan.GridEnergyWh, 1);

        // Less to deliver means a slower pace over the same window, not an earlier finish: the point of
        // pacing is to leave the sun as long as possible to displace the import.
        Assert.Equal(now, plan.GridStart!.Value);
        Assert.True(plan.RequiredPaceWatts < 22_000 / (departBy - now).TotalHours);
    }

    [Fact]
    public void ADepartureInThePastLeavesNothingToPlanWith()
    {
        var now = FirstPeriodEnd.AddHours(2);
        var plan = Plan(requiredWh: 10_000, departBy: now.AddMinutes(-30), now: now);

        Assert.Equal(TargetedChargeStrategy.Maximum, plan.Strategy);
        Assert.Equal(0, plan.CeilingEnergyWh, 1);
        Assert.Equal(0, plan.ExpectedEnergyWh, 1);
        Assert.Equal(10_000, plan.ShortfallWh, 1);
        Assert.Empty(plan.Blocks);
    }

    [Fact]
    public void TheSafetyMarginPullsTheFinishLineInFromTheDeparture()
    {
        var now = new DateTimeOffset(2026, 7, 27, 21, 0, 0, TimeSpan.Zero);
        var departBy = new DateTimeOffset(2026, 7, 28, 7, 0, 0, TimeSpan.Zero);
        var plan = Plan(
            requiredWh: 22_000,
            departBy: departBy,
            now: now,
            options: Options(safetyMargin: TimeSpan.FromMinutes(15)));

        Assert.Equal(departBy.AddMinutes(-15), plan.Deadline);

        // The margin shortens the window the blocks are placed in, so nothing runs into it — the whole
        // point being that "ready at 07:00" is not "still charging at 07:00".
        Assert.All(plan.Blocks, b => Assert.True(b.End <= departBy.AddMinutes(-15), "a block runs into the safety margin"));
    }

    [Fact]
    public void ADepartureOnTomorrowMorningUsesTomorrowsPeriodsToo()
    {
        // Plugged in at 21:00 with the forecast running into tomorrow morning: the sun after midnight
        // is planned for, and it displaces part of the grid block.
        var now = new DateTimeOffset(2026, 7, 27, 21, 0, 0, TimeSpan.Zero);
        var departBy = new DateTimeOffset(2026, 7, 28, 11, 0, 0, TimeSpan.Zero);

        var tomorrow = new SolarForecast(
            now,
            [.. Enumerable.Range(0, 4).Select(i => new SolarForecastPeriod(
                new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero).AddMinutes(30 * i),
                TimeSpan.FromMinutes(30),
                6000,
                EstimatedPowerWattsP10: 6000))]);

        var plan = Plan(requiredWh: 22_000, departBy: departBy, now: now, forecast: tomorrow);

        Assert.True(plan.SolarEnergyWh > 0, "tomorrow morning's sun should be planned for");
        Assert.Equal(22_000, plan.SolarEnergyWh + plan.GridEnergyWh, 1);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 8, 30, 0, TimeSpan.Zero), plan.SolarStart);
    }

    [Fact]
    public void TheSocFloorIsTheOwnersClampWhenTheForecastTrajectorySitsBelowIt()
    {
        var now = FirstPeriodEnd.AddMinutes(-30);
        var plan = Plan(requiredWh: 5_000, departBy: FirstPeriodEnd.AddHours(4), now: now, options: Options(minSocFloorPercent: 60));

        Assert.Equal(60, plan.SocFloorPercent, 1);
    }

    // --- Just in time (#101): the last stretch lands at the deadline, not whenever the sun offers it ---

    [Fact]
    public void JustInTime_HoldsTheTailBackToTheLatestItCanStartAndStillFinish()
    {
        // 10 hours of window, 4kWh of tail. At 11.04kW the tail needs ~22 minutes, so the release is
        // ~22 minutes before the deadline and everything else is charged before it.
        var now = FirstPeriodEnd.AddHours(-1);
        var departBy = now.AddHours(10);
        var plan = Plan(requiredWh: 12_000, departBy: departBy, now: now, tailWh: 4_000);

        Assert.Equal(4_000, plan.TailEnergyWh, 1);
        Assert.NotNull(plan.HoldUntil);

        var expected = departBy - TimeSpan.FromHours(4_000 / MaxChargePowerWatts);
        Assert.Equal(expected, plan.HoldUntil!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void JustInTime_LeavesTheEnergyBelowTheRestPointToChargeNormallyOnTheSun()
    {
        // The point of splitting at a rest point rather than delaying the lot: the free part still
        // takes the plateau, so a sunny window is not forfeited to protect the top of the pack.
        var now = FirstPeriodEnd.AddHours(-1);
        var plan = Plan(requiredWh: 12_000, departBy: now.AddHours(10), now: now, tailWh: 4_000);

        Assert.True(plan.SolarEnergyWh > 0, "the free part should still be charging on forecast surplus");

        // And it is charging before the release, not after it.
        Assert.Contains(plan.Blocks, b => b.Source == TargetedChargeSource.Solar && b.Start < plan.HoldUntil);
    }

    [Fact]
    public void JustInTime_SchedulesTheTailAfterTheReleaseAndNotBefore()
    {
        var now = FirstPeriodEnd.AddHours(-1);
        var plan = Plan(requiredWh: 12_000, departBy: now.AddHours(10), now: now, tailWh: 4_000);

        var release = plan.HoldUntil!.Value;

        // Everything scheduled at or after the release adds up to the tail: the two windows are
        // planned separately and nothing leaks across the boundary.
        var afterRelease = plan.Blocks.Where(b => b.End > release).Sum(b => b.EnergyWh);
        Assert.InRange(afterRelease, 3_950, 4_050);
    }

    [Fact]
    public void JustInTime_IsAbandonedWhenTheRestOfTheChargeWouldNotFitBeforeTheRelease()
    {
        // The promise outranks the preference.
        //
        // Note what makes this bite: the slack. Without it the free window shrinks by exactly the time
        // the tail needs, so the split can never make an otherwise-feasible request infeasible. The
        // release slack is what actually costs the free part room -- half an hour of it is 5.5kWh at
        // full power -- and with a 20kWh request in a 22.08kWh window there is not that much to spare.
        var now = FirstPeriodEnd.AddHours(-1);
        var plan = Plan(
            requiredWh: 20_000, departBy: now.AddHours(2), now: now, tailWh: 4_000,
            options: Options(releaseSlack: TimeSpan.FromMinutes(30)));

        Assert.Null(plan.HoldUntil);
        Assert.Equal(0, plan.TailEnergyWh);

        // And giving the hold up actually bought something: the same request held would have arrived
        // further short than the unheld one does.
        var cheapest = Plan(requiredWh: 20_000, departBy: now.AddHours(2), now: now);
        Assert.True(
            plan.ShortfallWh <= cheapest.ShortfallWh + 1,
            $"abandoning the hold should not cost delivery: {plan.ShortfallWh} vs {cheapest.ShortfallWh}");
    }

    [Fact]
    public void JustInTime_WithNoSlackNeverMakesAFeasibleRequestInfeasible()
    {
        // The complement of the case above, and the reason it needs a slack to demonstrate: the free
        // window loses exactly the time the tail gains, so a request that fits at all still fits split.
        var now = FirstPeriodEnd.AddHours(-1);
        var plan = Plan(requiredWh: 22_000, departBy: now.AddHours(2), now: now, tailWh: 4_000);

        Assert.NotNull(plan.HoldUntil);

        // Not "no shortfall" -- a request this close to the ceiling leaves a little on the table under
        // either priority, because the paced pass cannot run every slice flat out. The claim is only
        // that splitting it costs nothing extra.
        var cheapest = Plan(requiredWh: 22_000, departBy: now.AddHours(2), now: now);
        Assert.True(
            plan.ShortfallWh <= cheapest.ShortfallWh + 1,
            $"splitting a feasible request should not cost delivery: {plan.ShortfallWh} vs {cheapest.ShortfallWh}");
    }

    [Fact]
    public void JustInTime_IsAbandonedOnceTheReleasePointHasPassed()
    {
        // 4kWh of tail needs ~22 minutes at full power; with 20 minutes left the release is behind us,
        // so there is nothing to wait in and the plan is today's single pass.
        var now = FirstPeriodEnd.AddHours(-1);
        var plan = Plan(requiredWh: 4_000, departBy: now.AddMinutes(20), now: now, tailWh: 4_000);

        Assert.Null(plan.HoldUntil);
        Assert.Equal(0, plan.TailEnergyWh);
    }

    [Fact]
    public void JustInTime_ReleaseSlackBringsTheTailForward()
    {
        var now = FirstPeriodEnd.AddHours(-1);
        var departBy = now.AddHours(10);

        var tight = Plan(requiredWh: 12_000, departBy: departBy, now: now, tailWh: 4_000);
        var slack = Plan(
            requiredWh: 12_000, departBy: departBy, now: now, tailWh: 4_000,
            options: Options(releaseSlack: TimeSpan.FromMinutes(30)));

        Assert.Equal(
            tight.HoldUntil!.Value.AddMinutes(-30), slack.HoldUntil!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void JustInTime_TailNeverExceedsWhatIsStillOwed()
    {
        // 9kWh already delivered against a 12kWh request leaves 3kWh, which is less than the 4kWh tail.
        // The tail is what is left, not a figure from a request that is mostly in the car already.
        var now = FirstPeriodEnd.AddHours(-1);
        var plan = Plan(
            requiredWh: 12_000, departBy: now.AddHours(10), now: now, deliveredWh: 9_000, tailWh: 4_000);

        Assert.Equal(3_000, plan.RemainingEnergyWh, 1);
        Assert.Equal(3_000, plan.TailEnergyWh, 1);

        // And with nothing below the rest point left, the plan is holding right now.
        Assert.True(plan.IsHoldingAt(now));
    }

    [Fact]
    public void JustInTime_IsNotHoldingWhileThereIsStillEnergyBelowTheRestPointToDeliver()
    {
        // A hold being planned is not a hold being in force. Until the free part is delivered the car
        // charges exactly as Cheapest would, and the controller must not sit idle.
        var now = FirstPeriodEnd.AddHours(-1);
        var plan = Plan(requiredWh: 12_000, departBy: now.AddHours(10), now: now, tailWh: 4_000);

        Assert.NotNull(plan.HoldUntil);
        Assert.False(plan.IsHoldingAt(now));
    }

    [Fact]
    public void Cheapest_IsUnchangedByAnyOfThis()
    {
        // The default path must be byte-for-byte what it was: same strategy, same figures, no hold.
        var now = FirstPeriodEnd.AddHours(-1);
        var plan = Plan(requiredWh: 12_000, departBy: now.AddHours(10), now: now);

        Assert.Null(plan.HoldUntil);
        Assert.Equal(0, plan.TailEnergyWh);
        Assert.False(plan.IsHoldingAt(now));
    }

    [Fact]
    public void JustInTime_SaysWhatItIsHoldingAndUntilWhen()
    {
        var now = FirstPeriodEnd.AddHours(-1);
        var plan = Plan(requiredWh: 12_000, departBy: now.AddHours(10), now: now, tailWh: 4_000);

        Assert.Contains("Holding the last", plan.Reason);
        Assert.Contains("4.0kWh", plan.Reason);
    }
}
