using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// The one thing this type exists for: a provider that only ever talks about the future must still
/// leave us able to say what the morning was predicted to bring.
/// </summary>
public class SolarForecastHistoryTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    private static readonly DateTimeOffset Midnight = new(2026, 8, 9, 0, 0, 0, TimeSpan.FromHours(2));

    private static SolarForecast Forecast(DateTimeOffset retrievedAt, params (DateTimeOffset End, double Watts)[] periods) =>
        new(retrievedAt, [.. periods.Select(p => new SolarForecastPeriod(p.End, TimeSpan.FromMinutes(30), p.Watts))]);

    [Fact]
    public void NothingIsHeldBeforeTheFirstRefresh()
    {
        var history = new SolarForecastHistory();

        Assert.Null(history.ForDate(DateOnly.FromDateTime(Midnight.DateTime), Prague));
    }

    [Fact]
    public void PeriodsTheProviderHasStoppedReportingSurviveTheRefreshThatDroppedThem()
    {
        var history = new SolarForecastHistory();

        // 08:00, fetched at 07:30: the whole day ahead.
        history.Merge(Forecast(
            Midnight.AddHours(7.5),
            (Midnight.AddHours(8), 1000),
            (Midnight.AddHours(13), 4000)));

        // 14:00: Solcast now returns only what is left, and the morning is gone from its answer.
        history.Merge(Forecast(Midnight.AddHours(14), (Midnight.AddHours(15), 3000)));

        var day = history.ForDate(DateOnly.FromDateTime(Midnight.DateTime), Prague)!;

        Assert.Equal(3, day.Periods.Count);
        Assert.Equal(1000, day.Periods[0].EstimatedPowerWatts);
        Assert.Equal((1000 + 4000 + 3000) * 0.5, day.ExpectedEnergyWattHours, 3);
    }

    [Fact]
    public void TheLatestPredictionForAPeriodIsTheOneKept()
    {
        var history = new SolarForecastHistory();
        var noon = Midnight.AddHours(12);

        history.Merge(Forecast(Midnight.AddHours(6), (noon, 5000)));

        // Re-forecast at 11:30, half an hour before it happens: cloud has come in.
        history.Merge(Forecast(Midnight.AddHours(11.5), (noon, 2000)));

        var day = history.ForDate(DateOnly.FromDateTime(Midnight.DateTime), Prague)!;

        Assert.Equal(2000, Assert.Single(day.Periods).EstimatedPowerWatts);
        Assert.Equal(Midnight.AddHours(11.5), day.RetrievedAt);
    }

    [Fact]
    public void OneDayIsNotAnsweredWithAnother()
    {
        var history = new SolarForecastHistory();

        history.Merge(Forecast(
            Midnight.AddHours(6),
            (Midnight.AddHours(12), 5000),
            (Midnight.AddDays(1).AddHours(12), 6000)));

        var today = history.ForDate(DateOnly.FromDateTime(Midnight.DateTime), Prague)!;
        var tomorrow = history.ForDate(DateOnly.FromDateTime(Midnight.AddDays(1).DateTime), Prague)!;

        Assert.Equal(5000, Assert.Single(today.Periods).EstimatedPowerWatts);
        Assert.Equal(6000, Assert.Single(tomorrow.Periods).EstimatedPowerWatts);
    }

    [Fact]
    public void ADayWithNothingHeldForItAnswersNullRatherThanAnEmptyCurve()
    {
        // An empty curve would sum to zero and read as a day the sun never came up, which is a very
        // different claim from "we weren't running then".
        var history = new SolarForecastHistory();
        history.Merge(Forecast(Midnight.AddHours(6), (Midnight.AddHours(12), 5000)));

        Assert.Null(history.ForDate(DateOnly.FromDateTime(Midnight.AddDays(-1).DateTime), Prague));
    }

    [Fact]
    public void HistoryOlderThanTheRetentionWindowIsDropped()
    {
        var history = new SolarForecastHistory(TimeSpan.FromDays(2));

        history.Merge(Forecast(Midnight.AddHours(6), (Midnight.AddHours(12), 5000)));
        history.Merge(Forecast(Midnight.AddDays(5), (Midnight.AddDays(5).AddHours(12), 4000)));

        Assert.Equal(1, history.Count);
        Assert.Null(history.ForDate(DateOnly.FromDateTime(Midnight.DateTime), Prague));
    }
}
