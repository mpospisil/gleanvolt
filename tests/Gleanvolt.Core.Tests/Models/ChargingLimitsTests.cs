using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Models;

/// <summary>
/// The intersection of what the charger allows and what the car accepts (#124). Three lines of
/// arithmetic — so what is worth asserting is the direction each one narrows in, and that an
/// undescribed car changes nothing.
/// </summary>
public class ChargingLimitsTests
{
    /// <summary>The reference install: 6–16 A on three phases.</summary>
    private static ChargingLimits For(EvInfo ev) => ChargingLimits.Intersect(6, 16, 3, ev);

    private static EvInfo Car(int? phases = null, int? minAmps = null, int? maxAmps = null) =>
        EvInfo.Unknown with { Phases = phases, MinChargingCurrentAmps = minAmps, MaxChargingCurrentAmps = maxAmps };

    [Fact]
    public void AnUndescribedCarNarrowsNothing()
    {
        var limits = For(EvInfo.Unknown);

        Assert.Equal(6, limits.MinAmps);
        Assert.Equal(16, limits.MaxAmps);
        Assert.Equal(3, limits.Phases);
        Assert.False(limits.IsEmpty);
    }

    [Fact]
    public void ACarThatStatesNothingNarrowsNothingEither()
    {
        // A car described only by its pack size charges exactly as it did before anybody described it.
        Assert.Equal(For(EvInfo.Unknown), For(Car()));
    }

    [Fact]
    public void TheCarsFloorWinsWhenItIsHigher()
    {
        // Commanded 6A when it needs 8, the car draws nothing at all -- and a connected car taking no
        // power is what the fast mode's completion dwell reads as "finished".
        Assert.Equal(8, For(Car(minAmps: 8)).MinAmps);
    }

    [Fact]
    public void TheChargersFloorWinsWhenTheCarsIsLower()
    {
        // The wallbox cannot run below its own floor whatever the car would accept.
        Assert.Equal(6, For(Car(minAmps: 4)).MinAmps);
    }

    [Fact]
    public void TheCarsCeilingWinsWhenItIsLower()
    {
        Assert.Equal(10, For(Car(maxAmps: 10)).MaxAmps);
    }

    [Fact]
    public void ACarCanNeverRaiseTheInstallationsCeiling()
    {
        // That limit is the site's supply and the wiring in the wall, not a preference.
        Assert.Equal(16, For(Car(maxAmps: 32)).MaxAmps);
    }

    [Fact]
    public void TheFewerPhasesWin()
    {
        // The field the whole issue was written around: a single-phase car behind a three-phase wallbox
        // had every power figure in the system overstated threefold.
        Assert.Equal(1, For(Car(phases: 1)).Phases);
        Assert.Equal(3, For(Car(phases: 3)).Phases);
    }

    [Fact]
    public void ACarCannotConjurePhasesTheWallboxDoesNotHave()
    {
        Assert.Equal(3, For(Car(phases: 3)).Phases);
        Assert.Equal(1, ChargingLimits.Intersect(6, 16, 1, Car(phases: 3)).Phases);
    }

    [Fact]
    public void AFloorAboveTheCeilingIsAnEmptyBand()
    {
        // The car can never charge here. Caught at startup rather than discovered as "nothing happens".
        Assert.True(ChargingLimits.Intersect(6, 16, 3, Car(minAmps: 20)).IsEmpty);
    }
}
