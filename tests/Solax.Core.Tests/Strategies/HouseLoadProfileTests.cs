using Solax.Core.Strategies;

namespace Solax.Core.Tests.Strategies;

public class HouseLoadProfileTests
{
    private static readonly DateTimeOffset Midnight = new(new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Local));

    // Feeds a bucket until it has converged. The smoothing is deliberately slow -- a bucket's time
    // constant is around three days of that hour -- so "learned" here means many more samples than a
    // single day would supply.
    private static void Teach(HouseLoadProfile profile, int hour, double watts, int samples = 20_000)
    {
        var at = Midnight.AddHours(hour);
        for (var i = 0; i < samples; i++)
        {
            profile.Add(at, watts);
        }
    }

    [Fact]
    public void UnobservedHoursFallBackToTheSeed()
    {
        var profile = new HouseLoadProfile(seedWatts: 350);

        Assert.Equal(350, profile.ExpectedWattsAt(Midnight.AddHours(14)));
        Assert.False(profile.IsFullyLearned);
    }

    [Fact]
    public void EachHourLearnsItsOwnLoad()
    {
        var profile = new HouseLoadProfile(seedWatts: 350);

        Teach(profile, hour: 3, watts: 300);
        Teach(profile, hour: 13, watts: 5000);

        Assert.Equal(300, profile.ExpectedWattsAt(Midnight.AddHours(3)), 0);
        Assert.Equal(5000, profile.ExpectedWattsAt(Midnight.AddHours(13)), 0);
    }

    [Fact]
    public void TheAfternoonPeakDoesNotLeakIntoTheEvening()
    {
        // The defect this type exists to fix: a single trailing average reported the 15:00 load as the
        // expectation for 21:00, which wiped out the car's budget for the rest of the day.
        var profile = new HouseLoadProfile(seedWatts: 350);

        Teach(profile, hour: 15, watts: 5400);
        Teach(profile, hour: 21, watts: 800);

        Assert.Equal(5400, profile.ExpectedWattsAt(Midnight.AddHours(15)), 0);
        Assert.Equal(800, profile.ExpectedWattsAt(Midnight.AddHours(21)), 0);
    }

    [Fact]
    public void NegativeReadingsDoNotBiasTheProfileLow()
    {
        var profile = new HouseLoadProfile(seedWatts: 350);

        Teach(profile, hour: 2, watts: -500);

        Assert.Equal(0, profile.ExpectedWattsAt(Midnight.AddHours(2)), 0);
    }

    [Fact]
    public void FullyLearnedOnceEveryHourHasBeenSeen()
    {
        var profile = new HouseLoadProfile(seedWatts: 350, minSamplesPerHour: 10);

        for (var hour = 0; hour < 24; hour++)
        {
            Teach(profile, hour, watts: 1000, samples: 10);
        }

        Assert.True(profile.IsFullyLearned);
        Assert.Equal(24, profile.HourlyWatts.Count);
    }
}
