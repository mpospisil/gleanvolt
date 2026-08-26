using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Fast;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The fast mode's meter (#119). Small, but it owns the one number the mode ends on, and two of its
/// rules are the kind that only bite in production: delivery is counted from activation rather than
/// from the plug-in, and re-pressing the button starts a new count.
/// </summary>
public class FastChargeProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly FastChargeSelector _selector = new(NullLogger<FastChargeSelector>.Instance);
    private readonly FastChargeProvider _provider;

    public FastChargeProviderTests() => _provider = FastCharge.Provider(_selector);

    private static EnergyState Drawing(DateTimeOffset at, double watts) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: watts,
            EvChargerStatus.Charging, EvChargerPowerWatts: watts);

    [Fact]
    public void WithNoLimitThereIsNoProgress()
    {
        // Full: the ordinary case, and not a failure to report.
        Assert.Null(_provider.Update(Drawing(Now, 11_000)));
    }

    [Fact]
    public void MetersWhatTheChargerDelivers()
    {
        _selector.Set(new FastChargeLimit(20_000, Now), "test");

        _provider.Update(Drawing(Now, 12_000));
        var progress = _provider.Update(Drawing(Now.AddMinutes(5), 12_000));

        Assert.NotNull(progress);
        Assert.Equal(1_000, progress!.DeliveredWh, 0);   // 12kW for five minutes
        Assert.Equal(19_000, progress.RemainingWh, 0);
        Assert.False(progress.IsMet);
    }

    [Fact]
    public void CountsFromActivation_NotFromWhenTheCarWasPluggedIn()
    {
        // Energy the car took under some earlier mode is not part of this promise.
        _provider.Update(Drawing(Now, 12_000));
        _provider.Update(Drawing(Now.AddMinutes(5), 12_000));

        _selector.Set(new FastChargeLimit(20_000, Now.AddMinutes(5)), "test");

        _provider.Update(Drawing(Now.AddMinutes(5), 12_000));
        var progress = _provider.Update(Drawing(Now.AddMinutes(10), 12_000));

        Assert.Equal(1_000, progress!.DeliveredWh, 0);
    }

    [Fact]
    public void PressingTheButtonAgainStartsANewCount()
    {
        // Keyed on the activation instant rather than reference equality, so re-activating the same
        // figures still resets -- which is what pressing it again means.
        _selector.Set(new FastChargeLimit(20_000, Now), "test");
        _provider.Update(Drawing(Now, 12_000));
        _provider.Update(Drawing(Now.AddMinutes(5), 12_000));

        _selector.Set(new FastChargeLimit(20_000, Now.AddMinutes(5)), "test");
        var progress = _provider.Update(Drawing(Now.AddMinutes(5), 12_000));

        Assert.Equal(0, progress!.DeliveredWh, 0);
    }

    [Fact]
    public void ReportsTheLimitAsMetOnceItHasBeenDelivered()
    {
        _selector.Set(new FastChargeLimit(1_000, Now), "test");

        _provider.Update(Drawing(Now, 12_000));
        var progress = _provider.Update(Drawing(Now.AddMinutes(5), 12_000));

        Assert.True(progress!.IsMet);
        Assert.Equal(0, progress.RemainingWh);
        Assert.Equal(1, progress.Fraction);
    }

    [Fact]
    public void ClearingTheLimitReturnsToChargingUntilTheCarStops()
    {
        _selector.Set(new FastChargeLimit(20_000, Now), "test");
        _provider.Update(Drawing(Now, 12_000));

        _selector.Clear("test");

        Assert.Null(_provider.Update(Drawing(Now.AddMinutes(5), 12_000)));
    }

    [Fact]
    public void ADroppedPollIsNotIntegratedAcross()
    {
        // The integrator's own rule, asserted here because it is this meter that would otherwise
        // invent kilowatt-hours the car never took and end the mode early.
        _selector.Set(new FastChargeLimit(20_000, Now), "test");

        _provider.Update(Drawing(Now, 12_000));
        var progress = _provider.Update(Drawing(Now.AddHours(3), 12_000));

        Assert.Equal(0, progress!.DeliveredWh, 0);
    }

    // -- The deferred charge's schedule (#122).

    [Fact]
    public void WithNoDepartureThereIsNoPlan()
    {
        _selector.Set(new FastChargeLimit(30_000, Now), "test");

        Assert.Null(_provider.Update(Drawing(Now, 11_000))!.Plan);
    }

    [Fact]
    public void PlansFromTheInstallationsMaximumBeforeTheCarHasDrawnAnything()
    {
        _selector.Set(new FastChargeLimit(30_000, Now, DepartBy: Now.AddHours(9)), "test");

        var plan = _provider.Update(Drawing(Now, 0))!.Plan!;

        // 16A x 230V x 3 phases.
        Assert.Equal(11_040, plan.ChargePowerWatts);
        Assert.False(plan.PowerObserved);
        Assert.True(plan.IsWaitingAt(Now));
    }

    [Fact]
    public void UsesWhatTheCarActuallyTakesOnceItTakesSomething()
    {
        _selector.Set(new FastChargeLimit(30_000, Now, DepartBy: Now.AddHours(9)), "test");

        _provider.Update(Drawing(Now, 0));
        var plan = _provider.Update(Drawing(Now.AddMinutes(1), 7_400))!.Plan!;

        Assert.Equal(7_400, plan.ChargePowerWatts);
        Assert.True(plan.PowerObserved);
    }

    [Fact]
    public void RemembersWhatTheCarTakesAcrossThePausesInBetween()
    {
        // The rule that makes a deferred charge work at all: while it is held back the car draws
        // nothing, so a power read fresh each cycle would forget, every poll, that this car only does
        // 7.4kW -- and fall back to the installation's 11kW. That is how a deferred charge starts an
        // hour too late.
        _selector.Set(new FastChargeLimit(30_000, Now, DepartBy: Now.AddHours(9)), "test");

        _provider.Update(Drawing(Now, 7_400));
        var plan = _provider.Update(Drawing(Now.AddMinutes(1), 0))!.Plan!;

        Assert.Equal(7_400, plan.ChargePowerWatts);
        Assert.True(plan.PowerObserved);
    }

    [Fact]
    public void ATrickleIsNotAMeasurementOfWhatTheCarCanTake()
    {
        // Our own pause current, or standby: it describes the setpoint, not the car.
        _selector.Set(new FastChargeLimit(30_000, Now, DepartBy: Now.AddHours(9)), "test");

        var plan = _provider.Update(Drawing(Now, 150))!.Plan!;

        Assert.Equal(11_040, plan.ChargePowerWatts);
        Assert.False(plan.PowerObserved);
    }

    [Fact]
    public void ANewLimitForgetsTheOldCarsPower()
    {
        _selector.Set(new FastChargeLimit(30_000, Now, DepartBy: Now.AddHours(9)), "test");
        _provider.Update(Drawing(Now, 7_400));

        _selector.Set(new FastChargeLimit(30_000, Now.AddMinutes(5), DepartBy: Now.AddHours(9)), "test");
        var plan = _provider.Update(Drawing(Now.AddMinutes(5), 0))!.Plan!;

        Assert.False(plan.PowerObserved);
    }

    [Fact]
    public void TheStartMovesLaterAsEnergyGoesIn()
    {
        _selector.Set(new FastChargeLimit(30_000, Now, DepartBy: Now.AddHours(9)), "test");

        var first = _provider.Update(Drawing(Now, 11_000))!.Plan!.StartNoLaterThan;
        var later = _provider.Update(Drawing(Now.AddMinutes(5), 11_000))!.Plan!.StartNoLaterThan;

        Assert.True(later > first);
    }
}
