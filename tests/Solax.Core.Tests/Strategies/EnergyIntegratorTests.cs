using Solax.Core.Strategies;

namespace Solax.Core.Tests.Strategies;

public class EnergyIntegratorTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HoldsEachSamplesPowerUntilTheNextOne()
    {
        var integrator = new EnergyIntegrator();

        // At the service's 5-second poll interval, samples arrive far closer together than this; the
        // point is that each sample's power counts for the interval that follows it.
        integrator.Add(Start, 4000);
        integrator.Add(Start.AddMinutes(3), 4000);

        Assert.Equal(200, integrator.EnergyWattHours, 1);
    }

    [Fact]
    public void TheFirstSampleContributesNothingOnItsOwn()
    {
        var integrator = new EnergyIntegrator();

        Assert.Equal(0, integrator.Add(Start, 5000));
    }

    [Fact]
    public void GapsLongerThanTheLimitAreSkippedRatherThanInvented()
    {
        // A service that was asleep for an hour did not deliver an hour of charge.
        var integrator = new EnergyIntegrator(maxGap: TimeSpan.FromMinutes(5));

        integrator.Add(Start, 10_000);
        integrator.Add(Start.AddHours(1), 10_000);

        Assert.Equal(0, integrator.EnergyWattHours);
    }

    [Fact]
    public void ResetClearsTheTotalAndTheLastSample()
    {
        var integrator = new EnergyIntegrator();
        integrator.Add(Start, 4000);
        integrator.Add(Start.AddMinutes(3), 4000);

        integrator.Reset();
        integrator.Add(Start.AddMinutes(6), 4000);

        Assert.Equal(0, integrator.EnergyWattHours);
    }
}
