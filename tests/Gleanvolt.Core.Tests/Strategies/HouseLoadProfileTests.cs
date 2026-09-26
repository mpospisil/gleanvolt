using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

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

    // One quarter-hour row of stored history: the house drew houseWatts (car excluded) for the minutes
    // observed, and the car took evWatts on top.
    private static EnergyInterval Quarter(DateTimeOffset start, double houseWatts, double evWatts = 0, double coveredMinutes = 15)
    {
        var hours = coveredMinutes / 60;
        var importKwh = (houseWatts + evWatts) * hours / 1000;
        return new EnergyInterval(
            start, start.AddMinutes(15), "Europe/Prague", DateOnly.FromDateTime(start.LocalDateTime),
            SolarKwh: 0, ForecastSolarKwh: null, GridImportKwh: importKwh, GridExportKwh: 0, EvKwh: evWatts * hours / 1000,
            BatteryChargeKwh: 0, BatteryDischargeKwh: 0,
            SocStartPercent: 50, SocEndPercent: 50, SocMinPercent: 50, SocMaxPercent: 50, SocMeanPercent: 50,
            Covered: TimeSpan.FromMinutes(coveredMinutes), SampleCount: 100);
    }

    // days whole days of one hour's quarters, all at the same load.
    private static IEnumerable<EnergyInterval> Days(int hour, double houseWatts, int days, double evWatts = 0) =>
        from day in Enumerable.Range(0, days)
        from quarter in Enumerable.Range(0, 4)
        select Quarter(Midnight.AddDays(-day - 1).AddHours(hour).AddMinutes(15 * quarter), houseWatts, evWatts);

    [Fact]
    public void AnHourSeededFromHistoryIsTrustedAtOnce()
    {
        // Issue #229: a deploy used to send every hour back to the seed, and the slow smoothing left the
        // afternoon still 80 % seed a day and a half later. Two weeks of history is a learned hour.
        var profile = new HouseLoadProfile(seedWatts: 350);

        var seeded = profile.SeedFrom(Days(hour: 17, houseWatts: 900, days: 14));

        Assert.Equal(1, seeded);
        Assert.Equal(900, profile.ExpectedWattsAt(Midnight.AddHours(17)), 3);
        Assert.Equal(350, profile.ExpectedWattsAt(Midnight.AddHours(3)));
    }

    [Fact]
    public void TheHourIsTheMeanOfItsEnergyNotOfItsReadings()
    {
        // The planner sums energy, so an evening that ran the oven counts for what it drew: one day at
        // 2.5 kW and two at 400 W is 1.1 kW of expected load, not the 400 W a median would say.
        var profile = new HouseLoadProfile(seedWatts: 350);

        profile.SeedFrom(Days(hour: 18, houseWatts: 400, days: 2).Concat(
            Days(hour: 18, houseWatts: 2500, days: 1).Select(q => q with { PeriodStart = q.PeriodStart.AddDays(-5) })));

        Assert.Equal(1100, profile.ExpectedWattsAt(Midnight.AddHours(18)), 3);
    }

    [Fact]
    public void TheCarIsNotPartOfTheHouse()
    {
        var profile = new HouseLoadProfile(seedWatts: 350);

        profile.SeedFrom(Days(hour: 12, houseWatts: 600, days: 3, evWatts: 4200));

        Assert.Equal(600, profile.ExpectedWattsAt(Midnight.AddHours(12)), 3);
    }

    [Fact]
    public void APartlyObservedQuarterCountsForTheMinutesItSaw()
    {
        // A restart at 16:07 leaves a row with 8 minutes in it. Divided by the full quarter it would read
        // as the house going half-dark; divided by what was observed it reads as what it was.
        var profile = new HouseLoadProfile(seedWatts: 350);
        var history = Days(hour: 16, houseWatts: 800, days: 3).ToList();
        history.Add(Quarter(Midnight.AddDays(-9).AddHours(16), houseWatts: 800, coveredMinutes: 8));

        profile.SeedFrom(history);

        Assert.Equal(800, profile.ExpectedWattsAt(Midnight.AddHours(16)), 3);
    }

    [Fact]
    public void TooLittleHistoryLeavesTheSeed()
    {
        // One evening is exactly the sample the slow smoothing exists to keep from rewriting an hour.
        var profile = new HouseLoadProfile(seedWatts: 350);

        var seeded = profile.SeedFrom(Days(hour: 20, houseWatts: 3000, days: 1));

        Assert.Equal(0, seeded);
        Assert.Equal(350, profile.ExpectedWattsAt(Midnight.AddHours(20)));
    }

    [Fact]
    public void LiveSamplesCarryOnFromTheSeededHour()
    {
        var profile = new HouseLoadProfile(seedWatts: 350);
        profile.SeedFrom(Days(hour: 9, houseWatts: 1000, days: 14));

        Teach(profile, hour: 9, watts: 400);

        Assert.Equal(400, profile.ExpectedWattsAt(Midnight.AddHours(9)), 0);
    }
}
