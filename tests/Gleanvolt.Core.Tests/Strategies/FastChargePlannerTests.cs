using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// Scheduling a deferred fast charge (#122). One division and a subtraction — so what is worth testing
/// is not the arithmetic but the edges around it: the power it divides by, what happens when there is
/// not enough time, and the fact that it never quietly turns a late plan into a punctual one.
/// </summary>
public class FastChargePlannerTests
{
    // 22:00 the night before, which is the hour this feature exists for.
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SevenAm = new(2026, 8, 11, 7, 0, 0, TimeSpan.Zero);

    /// <summary>The reference install: 16 A on three phases at 230 V.</summary>
    private const double MaxPower = 11_040;

    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(15);

    private static FastChargePlan? Plan(
        double requiredWh = 30_000,
        double deliveredWh = 0,
        double? observedPowerWatts = null,
        DateTimeOffset? departBy = null,
        DateTimeOffset? now = null,
        double maxPowerWatts = MaxPower) =>
        FastChargePlanner.Plan(
            new FastChargeLimit(requiredWh, Now, DepartBy: departBy ?? SevenAm),
            deliveredWh,
            observedPowerWatts,
            maxPowerWatts,
            Margin,
            now ?? Now);

    [Fact]
    public void WithNoDepartureThereIsNothingToPlan()
    {
        Assert.Null(FastChargePlanner.Plan(
            new FastChargeLimit(30_000, Now), 0, null, MaxPower, Margin, Now));
    }

    [Fact]
    public void StartsAsLateAsItCanAndStillBeReadyInTime()
    {
        var plan = Plan();

        // 30kWh at 11.04kW is 2h43m; ready by 06:45; so start at about 04:02.
        Assert.NotNull(plan);
        Assert.Equal(new DateTimeOffset(2026, 8, 11, 6, 45, 0, TimeSpan.Zero), plan!.ReadyBy);
        Assert.Equal(2.717, plan.Duration.TotalHours, 2);
        Assert.Equal(new DateTimeOffset(2026, 8, 11, 4, 2, 0, TimeSpan.Zero), plan.StartNoLaterThan, TimeSpan.FromMinutes(1));
        Assert.True(plan.IsWaitingAt(Now));
        Assert.True(plan.IsFeasible);
    }

    [Fact]
    public void TheStartMovesLaterAsEnergyIsDelivered()
    {
        var fresh = Plan()!.StartNoLaterThan;
        var half = Plan(deliveredWh: 15_000)!.StartNoLaterThan;

        Assert.True(half > fresh);
    }

    [Fact]
    public void TheStartMovesEarlierWhenTheCarTurnsOutToBeSlowerThanTheCharger()
    {
        // The failure this feature has no slack for: an 11kW wallbox in front of a 7.4kW on-board
        // charger is otherwise a plan an hour short of the time it needs.
        var assumed = Plan()!.StartNoLaterThan;
        var measured = Plan(observedPowerWatts: 7_400)!.StartNoLaterThan;

        Assert.True(measured < assumed);
        Assert.Equal(4.054, Plan(observedPowerWatts: 7_400)!.Duration.TotalHours, 2);
    }

    [Fact]
    public void TheCarsOwnPowerIsUsedWhenItIsKnown_AndSaidToBeMeasured()
    {
        var measured = Plan(observedPowerWatts: 7_400)!;
        var assumed = Plan()!;

        Assert.Equal(7_400, measured.ChargePowerWatts);
        Assert.True(measured.PowerObserved);

        Assert.Equal(MaxPower, assumed.ChargePowerWatts);
        Assert.False(assumed.PowerObserved);
    }

    [Fact]
    public void AnObservedPowerAboveTheInstallationsIsNotBelieved()
    {
        // A spurious reading must not be able to promise a charge faster than the wallbox can run.
        var plan = Plan(observedPowerWatts: 40_000)!;

        Assert.Equal(MaxPower, plan.ChargePowerWatts);
    }

    [Fact]
    public void NotEnoughTimeIsReportedRatherThanHidden()
    {
        // Asked at 05:00 for 30kWh by 07:00: 1h45m of usable window against 2h43m of charging.
        var plan = Plan(now: new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero))!;

        Assert.False(plan.IsFeasible);
        Assert.Equal(10.68, plan.ShortfallWh / 1000, 2);

        // And the start time is left in the past rather than clamped forward: "you should have started
        // an hour ago" is true, and a plan clamped to now would look punctual.
        Assert.True(plan.StartNoLaterThan < new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero));
        Assert.False(plan.IsWaitingAt(new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void PastTheStartTimeItIsNoLongerWaiting()
    {
        var plan = Plan()!;

        Assert.True(plan.IsWaitingAt(plan.StartNoLaterThan.AddSeconds(-1)));
        Assert.False(plan.IsWaitingAt(plan.StartNoLaterThan));
        Assert.False(plan.IsWaitingAt(plan.StartNoLaterThan.AddHours(1)));
    }

    [Fact]
    public void TheDepartureIsTheDepartureAndTheMarginIsBeforeIt()
    {
        var plan = Plan()!;

        Assert.Equal(SevenAm, plan.DepartBy);
        Assert.False(plan.HasDepartedAt(SevenAm.AddSeconds(-1)));
        Assert.True(plan.HasDepartedAt(SevenAm));
    }

    [Fact]
    public void ADeliveredAmountPastTheRequestLeavesNothingToSchedule()
    {
        var plan = Plan(deliveredWh: 35_000)!;

        Assert.Equal(0, plan.RemainingEnergyWh);
        Assert.Equal(TimeSpan.Zero, plan.Duration);
        Assert.Equal(plan.ReadyBy, plan.StartNoLaterThan);
    }

    [Fact]
    public void AMisconfiguredZeroPowerChargesNowRatherThanNever()
    {
        // Dividing by something near zero defers the charge to the end of time: no error, no charge,
        // and a flat car in the morning. No plan means charge immediately, which is the safe direction.
        Assert.Null(Plan(maxPowerWatts: 0));
        Assert.Null(Plan(maxPowerWatts: double.NaN));
    }
}
