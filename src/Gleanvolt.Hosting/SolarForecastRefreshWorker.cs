using Microsoft.Extensions.Options;
using Gleanvolt.Infrastructure.Solcast;

namespace Gleanvolt.Hosting;

/// <summary>
/// Drives <see cref="SolcastForecastService.RefreshAsync"/>: fetches the forecast once at startup
/// (so the cache is warm) and then re-fetches on the configured <see cref="SolcastOptions.RefreshInterval"/>.
/// Hosting this as a background service is also what causes the singleton forecast service to be
/// instantiated when the application starts.
/// </summary>
public sealed class SolarForecastRefreshWorker : BackgroundService
{
    private readonly SolcastForecastService _forecastService;
    private readonly ILogger<SolarForecastRefreshWorker> _logger;
    private readonly SolcastOptions _options;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeProvider _timeProvider;

    /// <summary>How far ahead of first light to refresh, so the day starts on a fresh forecast.</summary>
    private static readonly TimeSpan DaylightLeadTime = TimeSpan.FromMinutes(30);

    public SolarForecastRefreshWorker(
        SolcastForecastService forecastService,
        IOptions<SolcastOptions> options,
        ILogger<SolarForecastRefreshWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _forecastService = forecastService;
        _logger = logger;
        _options = options.Value;
        _refreshInterval = options.Value.RefreshInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            // Stopped rather than looped over a call that would return immediately: a loop that wakes
            // every three hours to do nothing is a loop somebody has to read the source of to
            // understand. The singleton it warms has already been constructed by now, which is the
            // other thing this worker is for.
            _logger.LogInformation(
                "Solcast is switched off (Solcast:Enabled is false); no forecast will be fetched and "
                + "the plan falls back to what it does with no forecast. The daily quota belongs to the "
                + "site, so a workstation sharing this configuration spends the controller's calls.");
            return;
        }

        _logger.LogInformation("Solcast forecast refresh started; interval {RefreshInterval}.", _refreshInterval);

        // RefreshAsync swallows its own failures (keeping any cached forecast), so the loop here
        // just paces the calls -- a bad fetch doesn't need to stop future refreshes.
        while (!stoppingToken.IsCancellationRequested)
        {
            await _forecastService.RefreshAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(NextDelay(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// How long to wait before the next fetch. During daylight that is simply the configured interval;
    /// overnight the loop sleeps until the sun is forecast to come back, because a fresh forecast can't
    /// change any decision made in the dark but still spends an API call against the daily quota. The
    /// night sleep is capped so a missing or wrong forecast can't strand the loop.
    /// </summary>
    private TimeSpan NextDelay()
    {
        var now = _timeProvider.GetUtcNow();
        var expectedNow = _forecastService.ExpectedPowerWattsNow(now);

        if (expectedNow is null || expectedNow > _options.DaylightThresholdWatts)
        {
            return _refreshInterval;
        }

        var nextDaylight = _forecastService.NextDaylightStart(now, _options.DaylightThresholdWatts);
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
