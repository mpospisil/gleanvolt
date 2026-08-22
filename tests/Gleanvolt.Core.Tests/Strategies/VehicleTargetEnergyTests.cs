using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// "Charge it to 80%" in kilowatt-hours (#99). Small enough to be obvious and important enough to
/// pin: every SOC-based target the owner ever sets is this one multiplication, and getting the
/// efficiency the wrong way round would leave every car short by roughly a tenth.
/// </summary>
public class VehicleTargetEnergyTests
{
    /// <summary>An ID.4 Pro's usable pack — the figure its own SOC is a percentage of.</summary>
    private const double PackWh = 77_000;

    [Fact]
    public void Asks_for_more_than_the_gap_in_the_pack_because_the_target_is_metered_at_the_charger()
    {
        // 38% of 77 kWh is 29.26 kWh in the cells; at 90% the charger has to deliver 32.51 kWh.
        var wh = VehicleTargetEnergy.RequiredWh(42, 80, PackWh, 0.9);

        Assert.NotNull(wh);
        Assert.Equal(32_511, wh!.Value, 0);
        Assert.True(wh > 29_260, "asking only for the gap in the pack arrives short by the losses");
    }

    [Fact]
    public void Is_the_gap_itself_at_perfect_efficiency()
    {
        Assert.Equal(29_260, VehicleTargetEnergy.RequiredWh(42, 80, PackWh, 1.0)!.Value, 0);
    }

    [Fact]
    public void Says_nothing_to_do_when_the_car_is_already_there()
    {
        // Zero, not null: this is an answer -- the caller refuses the request and says why. Null would
        // mean "this basis cannot be offered at all", which is a different fact.
        Assert.Equal(0, VehicleTargetEnergy.RequiredWh(85, 80, PackWh, 0.9));
        Assert.Equal(0, VehicleTargetEnergy.RequiredWh(80, 80, PackWh, 0.9));
    }

    [Fact]
    public void Refuses_to_guess_without_a_reported_soc()
    {
        Assert.Null(VehicleTargetEnergy.RequiredWh(null, 80, PackWh, 0.9));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-77_000)]
    public void Refuses_to_guess_a_pack_size(double capacityWh)
    {
        // Vehicle:BatteryCapacityKWh unset. A guessed capacity would make every target quietly wrong
        // rather than visibly unavailable, which is the worse of the two failures by a long way.
        Assert.Null(VehicleTargetEnergy.RequiredWh(42, 80, capacityWh, 0.9));
    }

    [Fact]
    public void Clamps_an_efficiency_that_would_ask_for_a_pack_and_a_half()
    {
        // A mistyped 0.09 in the config asks for ten times the gap. Clamped to a band no charger is
        // outside of, so the mistake costs a rounding error rather than an overnight grid import.
        var absurd = VehicleTargetEnergy.RequiredWh(42, 80, PackWh, 0.09)!.Value;

        Assert.Equal(VehicleTargetEnergy.RequiredWh(42, 80, PackWh, 0.5)!.Value, absurd, 0);
        Assert.True(absurd < PackWh);
    }

    [Fact]
    public void Clamps_percentages_into_the_range_they_are_percentages_of()
    {
        Assert.Equal(
            VehicleTargetEnergy.RequiredWh(0, 100, PackWh, 0.9)!.Value,
            VehicleTargetEnergy.RequiredWh(-10, 130, PackWh, 0.9)!.Value,
            0);
    }

    // --- The rest-point split (#101) ---

    [Theory]
    // 45% now, 100% target, 80% rest: the tail is 80 -> 100, which is 20% of 77kWh over 0.9.
    [InlineData(45, 100, 80, 17_111)]
    // Already past the rest point: measured from where the car actually is, not from a phantom 80%.
    [InlineData(90, 100, 80, 8_556)]
    // Target at the rest point exactly: nothing above it, so nothing to hold.
    [InlineData(45, 80, 80, 0)]
    // Target below the rest point: still nothing.
    [InlineData(45, 60, 80, 0)]
    public void TailAboveRest_MeasuresFromTheHigherOfTheRestPointAndTheCar(
        double now, double target, double rest, double expectedWh)
    {
        var tail = VehicleTargetEnergy.TailAboveRestWh(now, target, rest, 77_000, 0.9);

        Assert.NotNull(tail);
        Assert.Equal(expectedWh, tail!.Value, 0);
    }

    [Fact]
    public void TailAboveRest_IsNullWithoutAnSocOrACapacity()
    {
        Assert.Null(VehicleTargetEnergy.TailAboveRestWh(null, 100, 80, 77_000, 0.9));
        Assert.Null(VehicleTargetEnergy.TailAboveRestWh(45, 100, 80, 0, 0.9));
    }

    [Fact]
    public void ResultingSoc_IsTheInverseOfRequiredWh()
    {
        // The round trip is what lets an owner who asked in kilowatt-hours still get a rest point.
        var requiredWh = VehicleTargetEnergy.RequiredWh(45, 80, 77_000, 0.9);
        var back = VehicleTargetEnergy.ResultingSocPercent(45, requiredWh!.Value, 77_000, 0.9);

        Assert.Equal(80, back!.Value, 6);
    }

    [Fact]
    public void ResultingSoc_NeverClaimsMoreThanAFullBattery()
    {
        var soc = VehicleTargetEnergy.ResultingSocPercent(90, 100_000, 77_000, 0.9);

        Assert.Equal(100, soc!.Value, 6);
    }

    [Fact]
    public void ResultingSoc_IsNullWithoutAnSocOrACapacity()
    {
        Assert.Null(VehicleTargetEnergy.ResultingSocPercent(null, 10_000, 77_000, 0.9));
        Assert.Null(VehicleTargetEnergy.ResultingSocPercent(45, 10_000, 0, 0.9));
    }
}
