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

    public FastChargeProviderTests() =>
        _provider = new FastChargeProvider(_selector, NullLogger<FastChargeProvider>.Instance);

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
}
