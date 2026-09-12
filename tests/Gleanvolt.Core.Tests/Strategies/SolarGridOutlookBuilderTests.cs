using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class SolarGridOutlookBuilderTests
{
    private static readonly DateTimeOffset Morning = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndOfDay = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(4);

    // A flat 300W house: the forecast surplus is the forecast PV less 300W.
    private static readonly HouseLoadProfile House = new(300);

    private static SolarForecastPeriod Period(DateTimeOffset end, double watts, double? p10 = null, double? p90 = null) =>
        new(end, TimeSpan.FromMinutes(30), watts, p10, p90);

    // 10:00-11:30 a building morning, a gap, one good half-hour after lunch, then a fading afternoon.
    private static SolarForecast Day(DateTimeOffset retrievedAt) => new(
        retrievedAt,
        [
            Period(Morning.AddHours(2.5), 1_000),   // 10:00-10:30: 700W surplus
            Period(Morning.AddHours(3), 3_000),     // 10:30-11:00: 2700W
            Period(Morning.AddHours(3.5), 5_000),   // 11:00-11:30: 4700W
            Period(Morning.AddHours(6.5), 2_500),   // 14:00-14:30: 2200W
            Period(Morning.AddHours(7.5), 1_000),   // 15:00-15:30: 700W
        ]);

    private static SolarGridOutlook Build(
        SolarForecast? forecast, DateTimeOffset now, double minimum = 2000, ForecastConfidence confidence = ForecastConfidence.P50) =>
        SolarGridOutlookBuilder.Build(forecast, now, EndOfDay, House, minimum, confidence, StaleAfter);

    [Fact]
    public void NoForecast_IsUnusable()
    {
        var outlook = Build(null, Morning);

        Assert.False(outlook.ForecastUsable);
        Assert.False(outlook.NoSunLeftToday);
    }

    [Fact]
    public void FindsTheFirstAndLastStretchesThatClearTheMinimum()
    {
        var outlook = Build(Day(Morning), Morning);

        Assert.True(outlook.ForecastUsable);
        Assert.Equal(Morning.AddHours(2.5), outlook.NextSunAt);
        Assert.Equal(Morning.AddHours(6.5), outlook.SunUntil);
    }

    [Fact]
    public void InsideAUsefulPeriod_TheSunIsNow()
    {
        var now = Morning.AddHours(3.25);

        var outlook = Build(Day(Morning), now);

        Assert.Equal(now, outlook.NextSunAt);
    }

    [Fact]
    public void AfterTheLastUsefulPeriod_NoSunIsLeft()
    {
        var outlook = Build(Day(Morning.AddHours(6)), Morning.AddHours(6.75));

        Assert.True(outlook.NoSunLeftToday);
    }

    [Fact]
    public void AHigherMinimumShortensTheDay()
    {
        var outlook = Build(Day(Morning), Morning, minimum: 4000);

        Assert.Equal(Morning.AddHours(3), outlook.NextSunAt);
        Assert.Equal(Morning.AddHours(3.5), outlook.SunUntil);
    }

    [Fact]
    public void AZeroMinimumDoesNotCountDarkness()
    {
        var dark = new SolarForecast(Morning, [Period(Morning.AddHours(1), 0)]);

        var outlook = Build(dark, Morning, minimum: 0);

        Assert.True(outlook.NoSunLeftToday);
    }

    [Fact]
    public void ReadsTheConfiguredBand()
    {
        var forecast = new SolarForecast(Morning, [Period(Morning.AddHours(1), 3_000, p10: 1_500)]);

        Assert.NotNull(Build(forecast, Morning, confidence: ForecastConfidence.P50).SunUntil);
        Assert.True(Build(forecast, Morning, confidence: ForecastConfidence.P10).NoSunLeftToday);
    }

    [Fact]
    public void TomorrowsSunDoesNotKeepTodayGoing()
    {
        var tomorrow = new SolarForecast(Morning.AddHours(12), [Period(EndOfDay.AddHours(10), 6_000)]);

        var outlook = Build(tomorrow, Morning.AddHours(12));

        Assert.True(outlook.NoSunLeftToday);
    }

    [Fact]
    public void AStaleForecastWithSunToCome_IsNotBelieved()
    {
        var outlook = Build(Day(Morning.AddHours(-5)), Morning);

        Assert.False(outlook.ForecastUsable);
        Assert.Contains("old", outlook.Reason);
    }

    [Fact]
    public void AStaleForecastWithNoPvLeft_StillSaysTheSunHasSet()
    {
        // The evening case: the forecast stops being refreshed once there is no sun worth a provider
        // call, and "nothing left today" does not go out of date.
        var outlook = Build(Day(Morning), Morning.AddHours(12));

        Assert.True(outlook.ForecastUsable);
        Assert.True(outlook.NoSunLeftToday);
    }

    [Fact]
    public void AStaleForecastIsJudgedOnItsOptimisticBand()
    {
        // The median says nothing is left, but the optimistic band still has some: not certain enough
        // to end anything on an old forecast.
        var forecast = new SolarForecast(
            Morning.AddHours(-10), [Period(Morning.AddHours(1), 0, p90: 800)]);

        var outlook = Build(forecast, Morning);

        Assert.False(outlook.ForecastUsable);
    }
}
