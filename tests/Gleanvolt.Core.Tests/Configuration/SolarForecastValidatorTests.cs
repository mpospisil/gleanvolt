using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Configuration;

/// <summary>What an external solar forecast provider may publish (#388): each rule on its own.</summary>
public class SolarForecastValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HalfHour = TimeSpan.FromMinutes(30);

    // 5 kWp installed.
    private const double Installed = 5000;

    // Half-hour periods from 08:00 to 16:00 today, 2 kW each.
    private static List<SolarForecastPeriod> Day() =>
        [.. Enumerable.Range(1, 16).Select(i => new SolarForecastPeriod(Now.AddHours(-2) + HalfHour * i, HalfHour, 2000, 1000, 3000))];

    private static IReadOnlyList<string?> Keys(IReadOnlyList<SolarForecastPeriod> periods, double? installed = Installed, DateTimeOffset? now = null) =>
        [.. SolarForecastValidator.Check(periods, installed, now ?? Now).Phrases.Select(phrase => phrase?.Key)];

    private static List<SolarForecastPeriod> With(int index, Func<SolarForecastPeriod, SolarForecastPeriod> change)
    {
        var periods = Day();
        periods[index] = change(periods[index]);
        return periods;
    }

    [Fact]
    public void AUsableForecastHasNoProblems() => Assert.Empty(Keys(Day()));

    [Fact]
    public void GapsAreAllowed()
    {
        var periods = Day();
        periods.RemoveRange(3, 4);

        Assert.Empty(Keys(periods));
    }

    [Fact]
    public void BandsAreOptional() =>
        Assert.Empty(Keys([.. Day().Select(period => period with { EstimatedPowerWattsP10 = null, EstimatedPowerWattsP90 = null })]));

    [Fact]
    public void AnEmptyForecastIsRefused() => Assert.Equal(["SolarForecast.Empty"], Keys([]));

    [Fact]
    public void TooManyPeriodsAreRefused()
    {
        var periods = Enumerable.Range(1, SolarForecastValidator.MaxPeriods + 1)
            .Select(i => new SolarForecastPeriod(Now + TimeSpan.FromMinutes(5 * i), TimeSpan.FromMinutes(5), 0))
            .ToList();

        Assert.Equal(["SolarForecast.TooMany"], Keys(periods));
    }

    [Fact]
    public void APeriodNotInUtcIsRefused() =>
        Assert.Equal(["SolarForecast.NotUtc"], Keys(With(2, period => period with { PeriodEnd = period.PeriodEnd.ToOffset(TimeSpan.FromHours(1)) })));

    [Theory]
    [InlineData(4)]
    [InlineData(61)]
    public void APeriodOutside5To60MinutesIsRefused(int minutes)
    {
        // The last period, so its length cannot also make it overlap the one before.
        var periods = Day();
        periods[^1] = periods[^1] with { Period = TimeSpan.FromMinutes(minutes) };

        Assert.Contains("SolarForecast.Length", Keys(periods));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    public void FiveAndSixtyMinutesAreAccepted(int minutes)
    {
        var period = TimeSpan.FromMinutes(minutes);
        var periods = Enumerable.Range(1, 4).Select(i => new SolarForecastPeriod(Now + period * i, period, 100)).ToList();

        Assert.Empty(Keys(periods));
    }

    [Fact]
    public void ANegativePowerIsRefused() =>
        Assert.Equal(["SolarForecast.Negative"], Keys(With(3, period => period with { EstimatedPowerWatts = -1, EstimatedPowerWattsP10 = -2 })));

    [Fact]
    public void APowerThatIsNotANumberIsRefused() =>
        Assert.Equal(["SolarForecast.Negative"], Keys(With(3, period => period with { EstimatedPowerWattsP90 = double.NaN })));

    [Fact]
    public void P10AboveP50IsRefused() =>
        Assert.Equal(["SolarForecast.Bands"], Keys(With(4, period => period with { EstimatedPowerWattsP10 = 2500 })));

    [Fact]
    public void P90BelowP50IsRefused() =>
        Assert.Equal(["SolarForecast.Bands"], Keys(With(4, period => period with { EstimatedPowerWattsP90 = 1500 })));

    [Fact]
    public void AnOverlapIsRefused() =>
        Assert.Equal(["SolarForecast.Overlap"], Keys(With(5, period => period with { PeriodEnd = period.PeriodEnd - TimeSpan.FromMinutes(10) })));

    [Fact]
    public void PeriodsOutOfOrderAreRefused()
    {
        var periods = Day();
        (periods[6], periods[7]) = (periods[7], periods[6]);

        Assert.Contains("SolarForecast.Overlap", Keys(periods));
    }

    [Fact]
    public void ProblemsWithSinglePeriodsAreListedUpToALimitAndTheRestCounted()
    {
        var periods = Day().Select(period => period with { EstimatedPowerWatts = -1 }).ToList();
        var problems = SolarForecastValidator.Check(periods, Installed, Now);

        Assert.Equal(SolarForecastValidator.MaxListedPeriodProblems + 1, problems.Count);
        Assert.Equal("SolarForecast.More", problems.Phrases[^1]!.Key);
        Assert.Contains("6 more", problems[^1]);
    }

    [Fact]
    public void AForecastWhollyInThePastIsRefused() => Assert.Equal(["SolarForecast.Past"], Keys(Day(), now: Now.AddDays(1)));

    [Fact]
    public void AForecastReachingBeyond14DaysIsRefused()
    {
        var periods = Day();
        periods.Add(new SolarForecastPeriod(Now + TimeSpan.FromDays(14) + HalfHour, HalfHour, 0));

        Assert.Equal(["SolarForecast.TooFar"], Keys(periods));
    }

    [Fact]
    public void APeakAboveTheAllowanceIsRefused() =>
        Assert.Equal(["SolarForecast.Peak"], Keys(With(8, period => period with { EstimatedPowerWatts = 6100, EstimatedPowerWattsP90 = 7000 })));

    [Fact]
    public void APeakJustWithinTheAllowanceIsAccepted() =>
        Assert.Empty(Keys(With(8, period => period with { EstimatedPowerWatts = 6000, EstimatedPowerWattsP90 = 7000 })));

    [Fact]
    public void WithoutAnInstalledPeakThePeakIsNotChecked() =>
        Assert.Empty(Keys(With(8, period => period with { EstimatedPowerWatts = 60000, EstimatedPowerWattsP90 = 70000 }), installed: null));

    [Fact]
    public void EveryProblemComesInEnglishToo()
    {
        var problems = SolarForecastValidator.Check(With(2, period => period with { EstimatedPowerWattsP10 = 2500 }), Installed, Now);

        Assert.Equal("In period 3, P10, P50 and P90 are not in order: P10 must be at most P50, and P90 at least P50.", Assert.Single(problems));
    }
}
