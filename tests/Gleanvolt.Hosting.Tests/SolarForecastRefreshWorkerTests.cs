using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting;
using Gleanvolt.Infrastructure.Solcast;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The refresh worker's pacing, which is what protects the API quota: overnight a fresh forecast
/// cannot change any decision made in the dark, and still costs a call.
///
/// <para>These tests exist because the worker now takes <see cref="ISolarForecastRefresh"/>. Against
/// the concrete <c>SolcastForecastService</c> they would have needed an HTTP client factory and a
/// live-ish forecast to say anything about scheduling, which is why there were none.</para>
/// </summary>
public class SolarForecastRefreshWorkerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Answers the two scheduling questions and nothing else, which is the whole point.</summary>
    private sealed class Stub(double? expectedNow, DateTimeOffset? nextDaylight) : ISolarForecastRefresh
    {
        public int Refreshes { get; private set; }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            Refreshes++;
            return Task.CompletedTask;
        }

        public double? ExpectedPowerWattsNow(DateTimeOffset instant) => expectedNow;

        public DateTimeOffset? NextDaylightStart(DateTimeOffset after, double thresholdWatts) => nextDaylight;
    }

    private static SolarForecastRefreshWorker Worker(
        double? expectedNow, DateTimeOffset? nextDaylight, SolcastOptions? options = null) =>
        new(
            new Stub(expectedNow, nextDaylight),
            Options.Create(options ?? new SolcastOptions()),
            NullLogger<SolarForecastRefreshWorker>.Instance,
            new FakeTimeProvider(Noon));

    [Fact]
    public void InDaylightItWaitsTheConfiguredInterval()
    {
        var options = new SolcastOptions();

        var delay = Worker(expectedNow: 4000, nextDaylight: Noon.AddHours(1), options).NextDelay();

        Assert.Equal(options.RefreshInterval, delay);
    }

    /// <summary>
    /// The rule that keeps the free tier viable: sleep through the dark rather than spending calls
    /// on a forecast that cannot change anything.
    /// </summary>
    [Fact]
    public void AfterDarkItSleepsTowardsFirstLight()
    {
        var options = new SolcastOptions { MaxNightSleep = TimeSpan.FromHours(9) };

        var delay = Worker(expectedNow: 0, nextDaylight: Noon.AddHours(8), options).NextDelay();

        // Half an hour early on purpose: arriving exactly at sunrise with a six-hour-old forecast
        // means the first hour of the day runs on the live-solar fallback.
        Assert.Equal(TimeSpan.FromHours(8) - TimeSpan.FromMinutes(30), delay);
    }

    [Fact]
    public void TheNightSleepIsCapped()
    {
        var options = new SolcastOptions { MaxNightSleep = TimeSpan.FromHours(6) };

        var delay = Worker(expectedNow: 0, nextDaylight: Noon.AddHours(20), options).NextDelay();

        Assert.Equal(options.MaxNightSleep, delay);
    }

    /// <summary>
    /// With nothing held there is nothing to tell day from night by, and the safe reading is "fetch":
    /// a worker that treated an absent forecast as night would never acquire one.
    /// </summary>
    [Fact]
    public void WithNoForecastAtAllItJustUsesTheInterval()
    {
        var options = new SolcastOptions();

        var delay = Worker(expectedNow: null, nextDaylight: null, options).NextDelay();

        Assert.Equal(options.RefreshInterval, delay);
    }

    [Fact]
    public void ADaylightStartAlreadyPastDoesNotProduceANegativeSleep()
    {
        var options = new SolcastOptions();

        var delay = Worker(expectedNow: 0, nextDaylight: Noon.AddHours(-1), options).NextDelay();

        Assert.Equal(options.RefreshInterval, delay);
    }
}
