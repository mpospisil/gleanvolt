using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Core.Tests.Strategies;

public class ForecastAccuracyTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

    // Six half-hour periods of a flat 4kW forecast: enough to close several and get past BiasMinPeriods.
    private static SolarForecast Forecast(double watts = 4000, int periods = 8) =>
        new(
            Start,
            [.. Enumerable.Range(1, periods).Select(i =>
                new SolarForecastPeriod(Start.AddMinutes(30 * i), TimeSpan.FromMinutes(30), watts))]);

    // Feeds samples every 5 minutes for the given span, at a fixed ratio of the forecast power.
    private static void Feed(ForecastAccuracyTracker tracker, SolarForecast forecast, TimeSpan duration, double actualRatio)
    {
        var forecastWatts = forecast.Periods[0].EstimatedPowerWatts;
        for (var elapsed = TimeSpan.Zero; elapsed <= duration; elapsed += TimeSpan.FromMinutes(5))
        {
            tracker.Add(Start + elapsed, forecastWatts * actualRatio, forecast);
        }
    }

    [Fact]
    public void TheBiasIsOneUntilEnoughPeriodsHaveClosed()
    {
        var tracker = new ForecastAccuracyTracker(minPeriods: 4);
        var forecast = Forecast();

        // Two periods' worth of badly under-producing samples: not yet enough to judge the day.
        Feed(tracker, forecast, TimeSpan.FromMinutes(60), actualRatio: 0.3);

        Assert.Equal(1.0, tracker.BiasFactor);
    }

    [Fact]
    public void AfterEnoughPeriods_TheBiasReflectsRealisedProduction()
    {
        var tracker = new ForecastAccuracyTracker(minPeriods: 2);
        var forecast = Forecast();

        Feed(tracker, forecast, TimeSpan.FromMinutes(180), actualRatio: 0.8);

        Assert.Equal(0.8, tracker.BiasFactor, 2);
    }

    [Fact]
    public void TheBiasIsClampedAtBothEnds()
    {
        var forecast = Forecast();

        var dismal = new ForecastAccuracyTracker(minPeriods: 2, clampMin: 0.5, clampMax: 1.2);
        Feed(dismal, forecast, TimeSpan.FromMinutes(180), actualRatio: 0.1);
        Assert.Equal(0.5, dismal.BiasFactor, 2);

        var glorious = new ForecastAccuracyTracker(minPeriods: 2, clampMin: 0.5, clampMax: 1.2);
        Feed(glorious, forecast, TimeSpan.FromMinutes(180), actualRatio: 2.0);
        Assert.Equal(1.2, glorious.BiasFactor, 2);
    }

    [Fact]
    public void EachClosedPeriodIsHandedOverExactlyOnce()
    {
        var tracker = new ForecastAccuracyTracker(minPeriods: 2);
        var forecast = Forecast();

        Feed(tracker, forecast, TimeSpan.FromMinutes(60), actualRatio: 0.5);

        var comparison = tracker.TakeClosedPeriod();
        Assert.NotNull(comparison);
        Assert.True(comparison.ActualWattHours < comparison.ForecastWattHours);
        Assert.Null(tracker.TakeClosedPeriod());
    }

    [Fact]
    public void ASustainedBreachOfTheTrustBandWithdrawsTrust()
    {
        var tracker = new ForecastAccuracyTracker(minPeriods: 2, trustMin: 0.6, trustMax: 1.4, trustBreachPeriods: 2);
        var forecast = Forecast();

        Feed(tracker, forecast, TimeSpan.FromMinutes(240), actualRatio: 0.2);

        Assert.True(tracker.TrustBreached);
    }

    [Fact]
    public void AForecastCloseToRealityKeepsTrust()
    {
        var tracker = new ForecastAccuracyTracker(minPeriods: 2, trustBreachPeriods: 2);
        var forecast = Forecast();

        Feed(tracker, forecast, TimeSpan.FromMinutes(240), actualRatio: 0.95);

        Assert.False(tracker.TrustBreached);
        Assert.Equal(0.95, tracker.BiasFactor, 2);
    }

    [Fact]
    public void NightPeriodsAreIgnoredRatherThanDraggingTheRatioAroundZero()
    {
        var tracker = new ForecastAccuracyTracker(minPeriods: 1);
        var darkness = Forecast(watts: 10);

        Feed(tracker, darkness, TimeSpan.FromMinutes(180), actualRatio: 0);

        Assert.Equal(1.0, tracker.BiasFactor);
        Assert.Equal(0, tracker.ForecastEnergyWhToday);
    }

    [Fact]
    public void SamplesWithNoCoveringForecastPeriodAreIgnored()
    {
        var tracker = new ForecastAccuracyTracker(minPeriods: 1);

        tracker.Add(Start.AddDays(3), 5000, Forecast());

        Assert.Equal(0, tracker.ActualEnergyWhToday);
        Assert.Equal(1.0, tracker.BiasFactor);
    }

    [Fact]
    public void ResetClearsTheDay()
    {
        var tracker = new ForecastAccuracyTracker(minPeriods: 2);
        Feed(tracker, Forecast(), TimeSpan.FromMinutes(180), actualRatio: 0.5);

        tracker.Reset();

        Assert.Equal(0, tracker.ActualEnergyWhToday);
        Assert.Equal(0, tracker.ForecastEnergyWhToday);
        Assert.Equal(1.0, tracker.BiasFactor);
        Assert.False(tracker.TrustBreached);
    }
}
