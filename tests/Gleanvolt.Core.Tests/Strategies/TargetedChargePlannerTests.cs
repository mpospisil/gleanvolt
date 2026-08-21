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
        TimeSpan? safetyMargin = null) =>
        new(
            BatteryCapacityWh: CapacityWh,
            ChargeEfficiency: 0.95,
            MinChargePowerWatts: MinChargePowerWatts,
            MaxChargePowerWatts: MaxChargePowerWatts,
            MinBatterySocFloorPercent: minSocFloorPercent,
            SafetyMargin: safetyMargin ?? TimeSpan.Zero);

    private static TargetedChargePlan Plan(
        double requiredWh,
        DateTimeOffset departBy,
        DateTimeOffset now,
        double socPercent = 96,
        SolarForecast? forecast = null,
        double deliveredWh = 0,
        TargetedChargePlannerOptions? options = null,
        DateTimeOffset? batteryFullBy = null) =>
        TargetedChargePlanner.Plan(
            State(socPercent, now),
            new TargetedChargeRequest(requiredWh, departBy, ActivatedAt: now),
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

        // Every slice ties at zero surplus, so the tie-break decides: 22kWh at 11.04kW takes just under
        // two hours, and those two hours start now. Waiting until 05:00 would buy exactly the same
        // energy, exposed to exactly as much sun — none — with eight hours less room to recover in.
        Assert.Equal(now, plan.GridStart!.Value);
        Assert.True(plan.IsInGridBlock(now));
        Assert.Equal(now + TimeSpan.FromHours(22_000 / MaxChargePowerWatts), plan.Blocks.Max(b => b.End));
    }

    [Fact]
    public void AGridBlockOverlappingASunnySlice_CountsOnlyThePowerTheSunLeavesFree()
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

        // The sun gives 5.5kW of the charger's 11.04kW, so each overlapped slice can only absorb the
        // 5.54kW difference — the block reaches much further back than deficit / P_max would suggest.
        var gridBlocks = plan.Blocks.Where(b => b.Source == TargetedChargeSource.Grid).ToList();
        Assert.All(gridBlocks, b => Assert.Equal(MaxChargePowerWatts - 5500, b.PowerWatts, 1));

        // And the two shares still add up to exactly what was asked for: no double counting.
        Assert.Equal(25_000, plan.SolarEnergyWh + plan.GridEnergyWh, 1);
        Assert.Equal(0, plan.ShortfallWh, 1);
    }

    [Fact]
    public void TheGridBlockGoesWhereTheForecastSurplusIsBiggest()
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

        // 3.5kW of surplus is under the charger's 4.14kW minimum, so none of it is a solar block.
        Assert.Equal(0, plan.SolarEnergyWh, 1);
        Assert.Equal(12_000, plan.GridEnergyWh, 1);

        // The plateau runs 09:30-11:00 and the import starts at the top of it, not at 08:30 and not at
        // the deadline.
        Assert.Equal(FirstPeriodEnd.AddHours(0.5), plan.GridStart!.Value);
        Assert.True(plan.IsInGridBlock(FirstPeriodEnd.AddHours(1)));
        Assert.False(plan.IsInGridBlock(now));
    }

    [Fact]
    public void AWindowThatIsSunnyThroughout_IsFilledFromTheFrontOfIt()
    {
        // Nothing but plateau, so every slice offers the same surplus and the tie-break takes over:
        // earliest first, which is also the earliest the car can start banking the target.
        var now = FirstPeriodEnd.AddMinutes(-30);
        var departBy = FirstPeriodEnd.AddHours(2.5);
        var plan = Plan(
            requiredWh: 25_000, departBy: departBy, now: now, socPercent: 100,
            forecast: Forecast(6000, 6000, 6000, 6000, 6000, 6000));

        Assert.Equal(now, plan.GridStart!.Value);
        Assert.True(plan.IsInGridBlock(now));

        // And the grid blocks stay inside the window they were placed in.
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
    public void TheBatteryKeepsItsPriority_APackThatNeedsTheWholeDayLeavesTheCarNothing()
    {
        // 20% SOC needs ~8.4kWh with losses, which swallows the shoulders and the whole plateau.
        var now = FirstPeriodEnd.AddMinutes(-30);
        var plan = Plan(requiredWh: 10_000, departBy: FirstPeriodEnd.AddHours(3), now: now, socPercent: 20);

        Assert.Equal(0, plan.SolarEnergyWh, 1);
        Assert.Equal(TargetedChargeStrategy.SolarPlusGrid, plan.Strategy);
        Assert.DoesNotContain(plan.Blocks, b => b.Source == TargetedChargeSource.Solar);
    }

    [Fact]
    public void ASliceWhoseLeftoverSurplusMissesTheChargersMinimum_IsWorthNothingToTheCar()
    {
        // 4kW of PV less 500W of house is 3.5kW: real energy, but below the 4.14kW the charger needs.
        var now = FirstPeriodEnd.AddMinutes(-30);
        var plan = Plan(
            requiredWh: 10_000,
            departBy: FirstPeriodEnd.AddHours(3),
            now: now,
            forecast: Forecast(4000, 4000, 4000, 4000, 4000, 4000));

        Assert.Equal(0, plan.SolarEnergyWh, 1);
        Assert.Equal(10_000, plan.GridEnergyWh, 1);

        // Worth nothing to the car *on its own* — but the grid block is placed right on top of it, so
        // the 3.5kW still comes off the meter while the charger runs at its maximum.
        Assert.Equal(now, plan.GridStart!.Value);
        Assert.True(plan.IsInGridBlock(now));
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

        // Less to deliver means a shorter block from the same start — which is exactly how a car that
        // limits itself to less than we asked for gets a longer one again on the next poll.
        Assert.Equal(now, plan.GridStart!.Value);
        Assert.Equal(now + TimeSpan.FromHours(14_000 / MaxChargePowerWatts), plan.Blocks.Max(b => b.End));
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
}
