using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// Composing a fast-charge limit (#119). Three doors compose one — the web tab, the HTTP API and the
/// Home Assistant button — and the point of the type is that all three reject the same things for the
/// same reasons.
/// </summary>
public class FastChargeLimitFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 22, 0, 0, TimeSpan.FromHours(1));

    private static readonly VehiclePackLimits Pack = new(BatteryCapacityKWh: 77, ChargeEfficiency: 0.9);

    private static FastChargeLimitFactory.Result Create(
        FastChargeBasis basis,
        double? energyWh = null,
        double? targetSoc = null,
        double? vehicleSoc = null,
        VehiclePackLimits? pack = null) =>
        FastChargeLimitFactory.Create(basis, energyWh, targetSoc, vehicleSoc, pack ?? Pack, Now);

    [Fact]
    public void Full_is_accepted_and_carries_no_limit()
    {
        var result = Create(FastChargeBasis.Full);

        Assert.True(result.Accepted);
        Assert.Null(result.Limit);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Full_ignores_figures_left_in_the_boxes()
    {
        // The surfaces keep both boxes filled while the owner switches between bases; refusing the
        // press would be refusing the one basis that asks for nothing.
        var result = Create(FastChargeBasis.Full, energyWh: 20_000, targetSoc: 60, vehicleSoc: 42);

        Assert.True(result.Accepted);
        Assert.Null(result.Limit);
    }

    [Fact]
    public void Takes_an_energy_limit_as_it_is_given()
    {
        var limit = Create(FastChargeBasis.Energy, energyWh: 20_000).Limit;

        Assert.NotNull(limit);
        Assert.Equal(20_000, limit!.RequiredEnergyWh);
        Assert.Equal(Now, limit.ActivatedAt);
        Assert.Null(limit.TargetSocPercent);
        Assert.False(limit.IsSocBased);
    }

    [Fact]
    public void Converts_a_state_of_charge_and_records_what_it_was_asked_in()
    {
        var limit = Create(FastChargeBasis.Soc, targetSoc: 60, vehicleSoc: 42).Limit;

        Assert.NotNull(limit);

        // (60 - 42) / 100 * 77000 / 0.9
        Assert.Equal(15_400, limit!.RequiredEnergyWh, 0);
        Assert.Equal(60, limit.TargetSocPercent);
        Assert.Equal(42, limit.VehicleSocPercentAtRequest);
        Assert.True(limit.IsSocBased);
    }

    [Fact]
    public void Converts_through_the_same_arithmetic_the_targeted_factory_uses()
    {
        var fast = Create(FastChargeBasis.Soc, targetSoc: 80, vehicleSoc: 42).Limit;

        var targeted = TargetedChargeRequestFactory.Create(
            Now.AddHours(9),
            energyWh: null,
            targetSocPercent: 80,
            TargetedChargePriority.Cheapest,
            restSocPercent: null,
            vehicleSocPercent: 42,
            new TargetedChargeRequestLimits(TimeSpan.FromHours(36), 77, 0.9),
            Now).Request;

        Assert.NotNull(fast);
        Assert.NotNull(targeted);
        Assert.Equal(targeted!.RequiredEnergyWh, fast!.RequiredEnergyWh, 6);
    }

    [Fact]
    public void Rejects_an_energy_basis_with_nothing_in_the_box()
    {
        Assert.Equal("Enter how much energy the car needs.", Create(FastChargeBasis.Energy).Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5000)]
    public void Rejects_a_non_positive_energy(double energyWh)
    {
        var result = Create(FastChargeBasis.Energy, energyWh: energyWh);

        Assert.False(result.Accepted);
        Assert.Null(result.Limit);
    }

    [Fact]
    public void Rejects_a_state_of_charge_basis_without_a_configured_capacity()
    {
        var result = Create(FastChargeBasis.Soc, targetSoc: 60, vehicleSoc: 42, pack: new VehiclePackLimits());

        Assert.False(result.Accepted);
        Assert.Contains("usable capacity", result.Error);
        Assert.Contains("kilowatt-hours", result.Error);
    }

    [Fact]
    public void Rejects_a_state_of_charge_basis_when_the_car_has_reported_nothing()
    {
        var result = Create(FastChargeBasis.Soc, targetSoc: 60, vehicleSoc: null);

        Assert.False(result.Accepted);
        Assert.Contains("has not reported", result.Error);
    }

    [Fact]
    public void Rejects_a_car_already_at_the_target_in_words_rather_than_completing_instantly()
    {
        // A mode that switches itself off within one poll of being pressed looks like a fault.
        var result = Create(FastChargeBasis.Soc, targetSoc: 60, vehicleSoc: 64);

        Assert.False(result.Accepted);
        Assert.Contains("already at 64%", result.Error);
        Assert.Contains("60%", result.Error);
    }

    [Fact]
    public void Rejects_a_state_of_charge_basis_with_nothing_in_the_box()
    {
        Assert.Equal("Enter the state of charge to stop at.", Create(FastChargeBasis.Soc, vehicleSoc: 42).Error);
    }

    [Fact]
    public void A_limit_is_met_only_once_the_energy_has_been_delivered()
    {
        var limit = Create(FastChargeBasis.Energy, energyWh: 20_000).Limit!;

        Assert.False(limit.IsMet(19_999));
        Assert.True(limit.IsMet(20_000));
        Assert.True(limit.IsMet(21_000));
    }
}
