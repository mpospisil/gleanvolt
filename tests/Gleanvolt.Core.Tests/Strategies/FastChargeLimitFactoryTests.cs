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
        VehiclePackLimits? pack = null,
        DateTimeOffset? departBy = null,
        TimeSpan? maxHorizon = null,
        VehicleSocBasis? socBasis = null) =>
        FastChargeLimitFactory.Create(
            basis, energyWh, targetSoc, socBasis ?? Fresh(vehicleSoc), pack ?? Pack, Now, departBy, maxHorizon);

    /// <summary>A reading taken this instant, as every test written before #179 assumed.</summary>
    private static VehicleSocBasis Fresh(double? soc) =>
        soc is null ? VehicleSocBasis.None : new(soc, TimeSpan.Zero, TimeSpan.FromHours(12));

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
            vehicleSoc: Fresh(42),
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

    // -- The departure (#122).

    [Fact]
    public void AnAmountWithNoDepartureChargesNow()
    {
        var limit = Create(FastChargeBasis.Energy, energyWh: 20_000).Limit!;

        Assert.Null(limit.DepartBy);
        Assert.False(limit.IsDeferred);
    }

    [Fact]
    public void CarriesADepartureOnAnEnergyAmount()
    {
        var departure = Now.AddHours(9);
        var limit = Create(FastChargeBasis.Energy, energyWh: 30_000, departBy: departure).Limit!;

        Assert.Equal(departure, limit.DepartBy);
        Assert.True(limit.IsDeferred);
    }

    [Fact]
    public void CarriesADepartureOnAStateOfChargeAmount()
    {
        var departure = Now.AddHours(9);
        var limit = Create(FastChargeBasis.Soc, targetSoc: 90, vehicleSoc: 42, departBy: departure).Limit!;

        Assert.Equal(departure, limit.DepartBy);
        Assert.Equal(90, limit.TargetSocPercent);
    }

    [Fact]
    public void RefusesADepartureWithNothingToTime()
    {
        // Full gives no duration to work back from, so there is no such thing as the latest moment it
        // could start. Refused rather than quietly charging at once -- which is what the owner would
        // find at 07:00.
        var result = Create(FastChargeBasis.Full, departBy: Now.AddHours(9));

        Assert.False(result.Accepted);
        Assert.Contains("needs an amount", result.Error);
    }

    [Fact]
    public void RefusesADepartureInThePast()
    {
        var result = Create(FastChargeBasis.Energy, energyWh: 30_000, departBy: Now.AddMinutes(-1));

        Assert.False(result.Accepted);
        Assert.Contains("in the past", result.Error);
    }

    [Fact]
    public void RefusesADepartureBeyondTheHorizon()
    {
        var result = Create(
            FastChargeBasis.Energy,
            energyWh: 30_000,
            departBy: Now.AddHours(40),
            maxHorizon: TimeSpan.FromHours(36));

        Assert.False(result.Accepted);
        Assert.Contains("36 hours", result.Error);
    }

    [Fact]
    public void RefusesTheDepartureBeforeItSaysAnythingAboutTheAmount()
    {
        // "When" is refused in its own terms rather than after the owner has been told something about
        // kilowatt-hours they did not ask about.
        var result = Create(FastChargeBasis.Energy, energyWh: 0, departBy: Now.AddMinutes(-1));

        Assert.Contains("in the past", result.Error);
    }

    /// <summary>
    /// The guard (#179), on the surface where it bites hardest: a fast charge <b>stops itself</b> on
    /// this amount, so an amount derived from an hours-old percentage is a charge that ends in the
    /// wrong place — and this mode draws the site's supply limit while it does it.
    /// </summary>
    [Fact]
    public void Refuses_a_state_of_charge_basis_measured_from_a_stale_reading()
    {
        var stale = new VehicleSocBasis(42, TimeSpan.FromHours(13), TimeSpan.FromHours(12));

        var result = Create(FastChargeBasis.Soc, targetSoc: 80, socBasis: stale);

        Assert.False(result.Accepted);
        Assert.Contains("13.0 h", result.Error);
        Assert.Contains("kilowatt-hours", result.Error);
    }

    /// <summary>The fallback: an energy basis never reads the car, so a dead feed cannot stop it.</summary>
    [Fact]
    public void An_energy_basis_is_untouched_by_a_stale_reading()
    {
        var stale = new VehicleSocBasis(42, TimeSpan.FromDays(3), TimeSpan.FromHours(12));

        var limit = Create(FastChargeBasis.Energy, energyWh: 20_000, socBasis: stale).Limit;

        Assert.NotNull(limit);
        Assert.Equal(20_000, limit!.RequiredEnergyWh);
    }

    /// <summary>
    /// And Full least of all — "charge until the car says stop" asks the car for nothing, so a stale
    /// reading has nothing to spoil. The basis that keeps working when the feed has died.
    /// </summary>
    [Fact]
    public void Full_is_untouched_by_a_stale_reading()
    {
        var stale = new VehicleSocBasis(42, TimeSpan.FromDays(3), TimeSpan.FromHours(12));

        var result = Create(FastChargeBasis.Full, socBasis: stale);

        Assert.True(result.Accepted);
        Assert.Null(result.Limit);
    }

    /// <summary>Absent is refused for being absent, not for being old (#137).</summary>
    [Fact]
    public void A_car_with_no_reading_is_still_refused_for_having_none()
    {
        var result = Create(FastChargeBasis.Soc, targetSoc: 80, socBasis: VehicleSocBasis.None);

        Assert.False(result.Accepted);
        Assert.Contains("has not reported a state of charge", result.Error);
        Assert.DoesNotContain("Vehicle:MaxAge", result.Error);
    }

    /// <summary>
    /// Both factories refuse a stale reading in the same words, which is the whole reason the message
    /// lives on the basis rather than in either of them: three doors, one wording.
    /// </summary>
    [Fact]
    public void Refuses_it_in_the_same_words_the_targeted_factory_uses()
    {
        var stale = new VehicleSocBasis(42, TimeSpan.FromHours(20), TimeSpan.FromHours(12));

        var fast = Create(FastChargeBasis.Soc, targetSoc: 80, socBasis: stale).Error;

        var targeted = TargetedChargeRequestFactory.Create(
            Now.AddHours(9),
            energyWh: null,
            targetSocPercent: 80,
            TargetedChargePriority.Cheapest,
            restSocPercent: null,
            vehicleSoc: stale,
            new TargetedChargeRequestLimits(TimeSpan.FromHours(36), 77, 0.9),
            Now).Error;

        Assert.Equal(targeted, fast);
    }
}
