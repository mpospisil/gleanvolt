using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Infrastructure.Solcast;

namespace Gleanvolt.Hosting;

/// <summary>
/// Keeps the solar forecast fresh: asks the active <see cref="ISolarForecastSource"/> for a forecast
/// once at startup (so the store is warm) and then on the configured <see cref="SolcastOptions.RefreshInterval"/>,
/// sleeping through the dark. Which source is active is <see cref="SolarForecastSources"/>' business
/// (issue #387); a switch wakes this to ask the new one at once. The schedule reads only the store
/// (<see cref="ISolarForecastRefresh"/>), never a source.
/// </summary>
public sealed class SolarForecastRefreshWorker : BackgroundService
{
    private readonly ISolarForecastRefresh _forecast;
    private readonly SolarForecastSources _sources;
    private readonly ILogger<SolarForecastRefreshWorker> _logger;
    private readonly SolcastOptions _options;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _switched = new(0, 1);

    /// <summary>How far ahead of first light to refresh, so the day starts on a fresh forecast.</summary>
    private static readonly TimeSpan DaylightLeadTime = TimeSpan.FromMinutes(30);

    public SolarForecastRefreshWorker(
        ISolarForecastRefresh forecast,
        SolarForecastSources sources,
        IOptions<SolcastOptions> options,
        ILogger<SolarForecastRefreshWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _forecast = forecast;
        _sources = sources;
        _logger = logger;
        _options = options.Value;
        _refreshInterval = options.Value.RefreshInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sources.Switched += Wake;
    }

    public override void Dispose()
    {
        _sources.Switched -= Wake;
        _switched.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Asks the active source, if there is one, and says how long until the next time. A source does
    /// not throw for a failed fetch, so nothing here needs to catch one.
    /// </summary>
    internal async Task<TimeSpan> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        if (_sources.Active is { } source)
        {
            await source.RequestRefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        return NextDelay();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Solcast forecast refresh started; interval {RefreshInterval}.", _refreshInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await _switched.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Another source selected: ask it now rather than at the end of the old one's interval or night.
    private void Wake()
    {
        try
        {
            if (_switched.CurrentCount == 0)
            {
                _switched.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Two switches at once; one wake-up is enough.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// How long to wait before the next fetch. During daylight that is simply the configured interval;
    /// overnight the loop sleeps until the sun is forecast to come back, because a fresh forecast can't
    /// change any decision made in the dark but still spends an API call against the daily quota. The
    /// night sleep is capped so a missing or wrong forecast can't strand the loop.
    /// </summary>
    internal TimeSpan NextDelay()
    {
        var now = _timeProvider.GetUtcNow();
        var expectedNow = _forecast.ExpectedPowerWattsNow(now);

        if (expectedNow is null || expectedNow > _options.DaylightThresholdWatts)
        {
            return _refreshInterval;
        }

        var nextDaylight = _forecast.NextDaylightStart(now, _options.DaylightThresholdWatts);
        if (nextDaylight is null || nextDaylight <= now)
        {
            return _refreshInterval;
        }

        // Wake a little before first light rather than at it: the plan treats a forecast older than
        // ChargeControl:Forecast:StaleForecastAfter as unusable, and arriving exactly at sunrise with a
        // six-hour-old forecast means the first hour of the day runs on the live-solar fallback.
        var untilDaylight = nextDaylight.Value - now - DaylightLeadTime;
        if (untilDaylight <= _refreshInterval)
        {
            return _refreshInterval;
        }

        var sleep = untilDaylight < _options.MaxNightSleep ? untilDaylight : _options.MaxNightSleep;
        _logger.LogInformation(
            "Sun is down (forecast {ExpectedWatts:F0}W); next forecast refresh in {SleepHours:F1}h, around first light at {NextDaylight:HH:mm}.",
            expectedNow,
            sleep.TotalHours,
            nextDaylight.Value.LocalDateTime);

        return sleep;
    }
}
