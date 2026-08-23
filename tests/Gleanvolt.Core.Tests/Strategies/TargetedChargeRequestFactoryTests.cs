using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// Composing a targeted request (#103). This was the web form's own <c>TryCompose</c> until the API
/// grew a second door onto it, and the whole reason it moved here is that the two must reject the same
/// things for the same reasons — a quote that is not composed exactly like the promise is not a quote.
/// </summary>
public class TargetedChargeRequestFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 22, 0, 0, TimeSpan.FromHours(1));

    private static readonly TargetedChargeRequestLimits Limits = new(
        MaxHorizon: TimeSpan.FromHours(36),
        BatteryCapacityKWh: 77,
        ChargeEfficiency: 0.9,
        DefaultRestSocPercent: 80);

    private static TargetedChargeRequestFactory.Result Create(
        double? energyWh = null,
        double? targetSoc = null,
        TargetedChargePriority priority = TargetedChargePriority.Cheapest,
        double? restSoc = null,
        double? vehicleSoc = null,
        TimeSpan? ahead = null,
        TargetedChargeRequestLimits? limits = null) =>
        TargetedChargeRequestFactory.Create(
            Now + (ahead ?? TimeSpan.FromHours(9)),
            energyWh,
            targetSoc,
            priority,
            restSoc,
            vehicleSoc,
            limits ?? Limits,
            Now);

    [Fact]
    public void Takes_an_energy_target_as_it_is_given()
    {
        var request = Create(energyWh: 22_000).Request;

        Assert.NotNull(request);
        Assert.Equal(22_000, request!.RequiredEnergyWh);
        Assert.Equal(Now, request.ActivatedAt);
        Assert.Null(request.TargetSocPercent);
        Assert.False(request.IsSocBased);
    }

    [Fact]
    public void Converts_a_state_of_charge_target_and_records_what_it_was_asked_in()
    {
        var request = Create(targetSoc: 80, vehicleSoc: 42).Request;

        Assert.NotNull(request);
        Assert.Equal(32_511, request!.RequiredEnergyWh, 0);
        Assert.Equal(80, request.TargetSocPercent);
        Assert.Equal(42, request.VehicleSocPercentAtRequest);
    }

    [Fact]
    public void Refuses_a_state_of_charge_target_the_car_is_already_past()
    {
        var result = Create(targetSoc: 80, vehicleSoc: 85);

        Assert.Null(result.Request);
        Assert.Contains("already at 85%", result.Error);
    }

    [Fact]
    public void Refuses_a_state_of_charge_target_with_no_reading_to_measure_from()
    {
        var result = Create(targetSoc: 80);

        Assert.Null(result.Request);
        Assert.Contains("not reported a state of charge", result.Error);
    }

    [Fact]
    public void Refuses_a_state_of_charge_target_on_an_installation_that_cannot_convert_one()
    {
        var result = Create(targetSoc: 80, vehicleSoc: 42, limits: Limits with { BatteryCapacityKWh = 0 });

        Assert.Null(result.Request);
        Assert.Contains("usable capacity", result.Error);
    }

    [Fact]
    public void Refuses_a_request_that_says_it_both_ways()
    {
        var result = Create(energyWh: 22_000, targetSoc: 80, vehicleSoc: 42);

        Assert.Null(result.Request);
        Assert.Contains("not both", result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5000)]
    public void Refuses_an_energy_target_of_nothing(double energyWh)
    {
        Assert.Contains("how much energy", Create(energyWh: energyWh).Error);
    }

    [Fact]
    public void Refuses_a_departure_that_has_been_and_gone()
    {
        Assert.Contains("in the past", Create(energyWh: 22_000, ahead: TimeSpan.FromHours(-1)).Error);
    }

    [Fact]
    public void Refuses_a_departure_further_off_than_anything_can_promise()
    {
        var result = Create(energyWh: 22_000, ahead: TimeSpan.FromHours(48));

        Assert.Null(result.Request);
        Assert.Contains("36 hours away", result.Error);
    }

    [Fact]
    public void Splits_off_the_tail_that_a_just_in_time_hold_keeps_back()
    {
        var request = Create(
            targetSoc: 90,
            vehicleSoc: 42,
            priority: TargetedChargePriority.JustInTime).Request;

        Assert.NotNull(request);
        Assert.True(request!.HoldsTail);

        // 80 to 90% of the pack, at the charger: the part deliberately not taken on the sun.
        Assert.Equal(0.10 * 77_000 / 0.9, request.TailEnergyWh, 0);
        Assert.Equal(80, request.RestSocPercent);
    }

    [Fact]
    public void Holds_nothing_back_under_the_cheapest_priority()
    {
        var request = Create(targetSoc: 90, vehicleSoc: 42).Request;

        Assert.Equal(0, request!.TailEnergyWh);
        Assert.False(request.HoldsTail);
    }

    [Fact]
    public void Never_holds_back_more_than_the_request_itself_contains()
    {
        // A rest point under where the car already sits: everything asked for is above it, and holding
        // more than was asked for would be holding energy the request never contained.
        var request = Create(
            energyWh: 5_000,
            vehicleSoc: 90,
            restSoc: 50,
            priority: TargetedChargePriority.JustInTime).Request;

        Assert.NotNull(request);
        Assert.Equal(5_000, request!.TailEnergyWh);
    }

    [Fact]
    public void Cannot_hold_a_tail_it_has_no_way_to_measure()
    {
        // No reading, so there is no rest point to measure down from. The promise stands; only the
        // shape of the delivery is lost, which is the honest degradation.
        var request = Create(energyWh: 22_000, priority: TargetedChargePriority.JustInTime).Request;

        Assert.NotNull(request);
        Assert.Equal(0, request!.TailEnergyWh);
        Assert.False(request.HoldsTail);
    }
}
