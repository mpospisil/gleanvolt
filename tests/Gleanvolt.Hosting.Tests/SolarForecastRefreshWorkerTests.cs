using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting;
using Gleanvolt.Infrastructure.Solcast;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The refresh worker's pacing, which is what protects the API quota: overnight a fresh forecast
/// cannot change any decision made in the dark, and still costs a call. And since issue #387, that it
/// asks the source <c>SolarForecast:Source</c> selects, and no other.
/// </summary>
public class SolarForecastRefreshWorkerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Answers the two scheduling questions and nothing else, which is the whole point.</summary>
    private sealed class Stub(double? expectedNow, DateTimeOffset? nextDaylight) : ISolarForecastRefresh
    {
        public double? ExpectedPowerWattsNow(DateTimeOffset instant) => expectedNow;

        public DateTimeOffset? NextDaylightStart(DateTimeOffset after, double thresholdWatts) => nextDaylight;
    }

    private sealed class Source(string id) : ISolarForecastSource
    {
        public string Id => id;

        public int Requests { get; private set; }

        public Task RequestRefreshAsync(CancellationToken cancellationToken = default)
        {
            Requests++;
            return Task.CompletedTask;
        }
    }

    /// <summary>An options monitor whose value the test changes, as a configuration reload would.</summary>
    private sealed class Monitor(string source) : IOptionsMonitor<SolarForecastOptions>
    {
        private Action<SolarForecastOptions, string?>? _listener;

        public SolarForecastOptions CurrentValue { get; private set; } = new() { Source = source };

        public SolarForecastOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<SolarForecastOptions, string?> listener)
        {
            _listener = listener;
            return new Subscription();
        }

        public void Select(string id)
        {
            CurrentValue = new SolarForecastOptions { Source = id };
            _listener?.Invoke(CurrentValue, null);
        }

        private sealed class Subscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static SolarForecastSources Sources(Monitor monitor, params ISolarForecastSource[] sources) =>
        new(sources, monitor, NullLogger<SolarForecastSources>.Instance);

    private static SolarForecastRefreshWorker Worker(
        double? expectedNow, DateTimeOffset? nextDaylight, SolcastOptions? options = null, SolarForecastSources? sources = null) =>
        new(
            new Stub(expectedNow, nextDaylight),
            sources ?? Sources(new Monitor(SolarForecastSourceIds.Solcast), new Source(SolarForecastSourceIds.Solcast)),
            Options.Create(options ?? new SolcastOptions()),
            NullLogger<SolarForecastRefreshWorker>.Instance,
            new FakeTimeProvider(Noon));

    [Fact]
    public async Task ItAsksTheSelectedSourceAndNoOther()
    {
        var solcast = new Source(SolarForecastSourceIds.Solcast);
        var other = new Source("other");
        var worker = Worker(4000, null, sources: Sources(new Monitor("other"), solcast, other));

        await worker.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(0, solcast.Requests);
        Assert.Equal(1, other.Requests);
    }

    /// <summary>Unset means Solcast, as it did before there was a choice.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("SOLCAST")]
    public async Task UnsetOrInAnyCaseItIsSolcast(string configured)
    {
        var solcast = new Source(SolarForecastSourceIds.Solcast);
        var worker = Worker(4000, null, sources: Sources(new Monitor(configured), solcast));

        await worker.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(1, solcast.Requests);
    }

    [Fact]
    public async Task AnUnknownSourceAsksNothingAndKeepsTheSchedule()
    {
        var solcast = new Source(SolarForecastSourceIds.Solcast);
        var options = new SolcastOptions();
        var worker = Worker(null, null, options, Sources(new Monitor("nowhere"), solcast));

        var delay = await worker.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(0, solcast.Requests);
        Assert.Equal(options.RefreshInterval, delay);
    }

    /// <summary>
    /// Switched at runtime, the way a tariff strategy is: the new source is asked at once, not after the
    /// old one's interval or night sleep has run out.
    /// </summary>
    [Fact]
    public async Task ASwitchAsksTheNewSourceAtOnce()
    {
        var solcast = new Source(SolarForecastSourceIds.Solcast);
        var other = new Source("other");
        var monitor = new Monitor(SolarForecastSourceIds.Solcast);
        var options = new SolcastOptions { RefreshInterval = TimeSpan.FromHours(3) };
        using var worker = Worker(4000, null, options, Sources(monitor, solcast, other));

        await worker.StartAsync(CancellationToken.None);
        await WaitFor(() => solcast.Requests == 1);

        monitor.Select("other");
        await WaitFor(() => other.Requests == 1);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, solcast.Requests);
    }

    [Fact]
    public void AReloadThatKeepsTheSourceIsNoSwitch()
    {
        var monitor = new Monitor(SolarForecastSourceIds.Solcast);
        using var sources = Sources(monitor, new Source(SolarForecastSourceIds.Solcast));
        var switches = 0;
        sources.Switched += () => switches++;

        monitor.Select("Solcast");

        Assert.Equal(0, switches);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the worker.");
            await Task.Delay(10);
        }
    }

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
    public async Task SettingsChoiceSwitchesAtOnceAndOutlivesAConfigurationReload()
    {
        // #388: the choice is saved for the next start, so a reload until then must not undo it.
        var solcast = new Source(SolarForecastSourceIds.Solcast);
        var external = new Source(SolarForecastSourceIds.External);
        var monitor = new Monitor(SolarForecastSourceIds.Solcast);
        var sources = Sources(monitor, solcast, external);
        var switches = 0;
        sources.Switched += () => switches++;

        sources.Select("External");
        monitor.Select(SolarForecastSourceIds.Solcast);
        await Worker(expectedNow: 500, nextDaylight: null, sources: sources).RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(1, switches);
        Assert.Equal(SolarForecastSourceIds.External, sources.Selected);
        Assert.Equal(1, external.Requests);
        Assert.Equal(0, solcast.Requests);
    }

    [Fact]
    public void SelectedAndHasNameOnlyRegisteredSources()
    {
        var sources = Sources(new Monitor("nonsense"), new Source(SolarForecastSourceIds.Solcast), new Source(SolarForecastSourceIds.External));

        Assert.Null(sources.Active);
        Assert.Equal("nonsense", sources.Selected);
        Assert.Equal([SolarForecastSourceIds.Solcast, SolarForecastSourceIds.External], sources.Ids);
        Assert.True(sources.Has(" EXTERNAL "));
        Assert.False(sources.Has("nonsense"));
    }

    [Fact]
    public void ADaylightStartAlreadyPastDoesNotProduceANegativeSleep()
    {
        var options = new SolcastOptions();

        var delay = Worker(expectedNow: 0, nextDaylight: Noon.AddHours(-1), options).NextDelay();

        Assert.Equal(options.RefreshInterval, delay);
    }
}
