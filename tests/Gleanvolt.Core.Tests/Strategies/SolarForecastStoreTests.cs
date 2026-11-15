using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// The store every forecast reader is served from, whichever source published to it (issue #387): one
/// snapshot replaced whole, a history that outlives it, and <see cref="SolarForecastStore.Forget"/>
/// dropping the one and not the other.
/// </summary>
public class SolarForecastStoreTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 8, 9, 0, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateOnly Today = DateOnly.FromDateTime(Midnight.DateTime);
    private static readonly SolarForecastOrigin Solcast = new(SolarForecastSourceIds.Solcast, "Solcast");
    private static readonly SolarForecastOrigin External = new("external", "External (mock)");

    private readonly Clock _clock = new() { Now = Midnight.AddHours(7) };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    }

    private static SolarForecast Forecast(DateTimeOffset retrievedAt, params (double Hour, double Watts)[] periods) =>
        new(retrievedAt, [.. periods.Select(p => new SolarForecastPeriod(Midnight.AddHours(p.Hour), TimeSpan.FromMinutes(30), p.Watts))]);

    [Fact]
    public void NothingIsHeldBeforeTheFirstPublish()
    {
        var store = new SolarForecastStore(_clock);

        Assert.Null(store.Current);
        Assert.Null(store.GetForecastForToday());
        Assert.Null(store.GetForecast(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.Null(store.GetDayForecast(Today));
        Assert.Null(store.GetDaySummary(Today));
        Assert.Null(store.ExpectedPowerWattsNow(Midnight.AddHours(10)));
        Assert.Null(store.NextDaylightStart(Midnight, 100));
    }

    [Fact]
    public void APublishReplacesTheSnapshotWhole()
    {
        var store = new SolarForecastStore(_clock);
        store.Publish(Forecast(Midnight.AddHours(6), (10, 1000), (11, 2000)), Solcast);
        var first = store.Current;

        _clock.Now += TimeSpan.FromHours(3);
        var second = Forecast(Midnight.AddHours(9), (11, 3000));
        store.Publish(second, External);

        Assert.NotSame(first, store.Current);
        Assert.Same(second, store.Current!.Forecast);
        Assert.Equal(External, store.Current.Origin);
        Assert.Equal(Midnight.AddHours(10), store.Current.ReceivedAt);

        // The earlier snapshot is untouched: a reader holding it never sees half of the next one.
        Assert.Equal(Solcast, first!.Origin);
        Assert.Equal(2, first.Forecast.Periods.Count);
    }

    [Fact]
    public void QueriesAnswerFromTheLatestForecastAndTheDayFromTheHistory()
    {
        var store = new SolarForecastStore(_clock);
        store.Publish(Forecast(Midnight.AddHours(6), (8, 1000), (13, 4000)), Solcast);
        store.Publish(Forecast(Midnight.AddHours(12), (13, 3000)), Solcast);

        // The live forecast is only what the last publish said...
        Assert.Single(store.GetForecastForToday()!.Periods);
        Assert.Equal(3000, store.ExpectedPowerWattsNow(Midnight.AddHours(12.75)));

        // ...and the day keeps the morning it no longer mentions, with the later word on 13:00.
        var day = store.GetDayForecast(Today)!;
        Assert.Equal([1000, 3000], day.Periods.Select(p => p.EstimatedPowerWatts));
        Assert.Equal((1000 + 3000) * 0.5, store.GetDayEnergyWattHours(Today)!.Value, 3);
        Assert.Equal(store.GetDaySummary(Today)!.ExpectedWh, day.ExpectedEnergyWattHours, 3);
        Assert.Equal(2, store.RetainedPeriodCount);
    }

    [Fact]
    public void ForgettingDropsTheForecastButNotTheHistory()
    {
        var store = new SolarForecastStore(_clock);
        store.Publish(Forecast(Midnight.AddHours(6), (11, 4000)), Solcast);

        store.Forget();

        Assert.Null(store.Current);
        Assert.Null(store.GetForecastForToday());
        Assert.Null(store.ExpectedPowerWattsNow(Midnight.AddHours(10.75)));
        Assert.Null(store.NextDaylightStart(Midnight, 100));
        Assert.NotNull(store.GetDayForecast(Today));
        Assert.Equal(2000, store.GetDayEnergyWattHours(Today)!.Value, 3);
    }

    [Fact]
    public void AfterForgettingTheNextPublishIsHeldAgain()
    {
        var store = new SolarForecastStore(_clock);
        store.Publish(Forecast(Midnight.AddHours(6), (11, 4000)), Solcast);
        store.Forget();

        store.Publish(Forecast(Midnight.AddHours(7), (12, 5000)), External);

        Assert.Equal(External, store.Current!.Origin);
        Assert.Equal(2, store.GetDayForecast(Today)!.Periods.Count);
    }

    [Fact]
    public void EveryPublishAndForgetIsAChange()
    {
        var store = new SolarForecastStore(_clock);
        var changes = 0;
        store.Changed += () => changes++;

        store.Publish(Forecast(Midnight.AddHours(6), (11, 4000)), Solcast);
        store.Forget();

        Assert.Equal(2, changes);
    }

    /// <summary>A reader sees the state before a publish or after it, never a mixture.</summary>
    [Fact]
    public async Task ReadersDuringPublishesSeeAConsistentForecast()
    {
        var store = new SolarForecastStore(_clock);
        using var stop = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                store.Publish(Forecast(Midnight.AddHours(6), (11, i), (12, i)), Solcast);
            }

            stop.Cancel();
        });

        while (!stop.IsCancellationRequested)
        {
            if (store.Current is { } current)
            {
                var watts = current.Forecast.Periods.Select(p => p.EstimatedPowerWatts).Distinct();
                Assert.Single(watts);
            }
        }

        await writer;
    }
}
