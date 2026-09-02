using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Infrastructure.Solcast;

/// <summary>
/// <see cref="ISolarForecastService"/> backed by the Solcast rooftop-sites API. Holds the last
/// successfully-fetched forecast in memory and serves queries from it; refreshing is driven
/// externally (see the worker that calls <see cref="RefreshAsync"/> on a schedule). A failed
/// refresh keeps the previously cached forecast intact.
/// </summary>
public sealed class SolcastForecastService : ISolarForecastService
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used to reach Solcast.</summary>
    public const string HttpClientName = "Solcast";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SolcastOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SolcastForecastService> _logger;

    // Reference-typed cache updated wholesale on refresh; volatile so query threads see the latest
    // publication. Reads snapshot the field once into a local before use.
    private volatile SolarForecast? _cached;

    // Said once. A refresh loop repeating "this is switched off" every three hours is the noise this
    // project has spent several issues removing from elsewhere.
    private bool _saidItIsOff;

    // What the cache above cannot hold: the periods that have already gone by. Solcast's forecasts
    // endpoint only ever returns the future, so a wholesale replacement loses this morning the moment
    // this morning happens -- and "what did we expect the roof to do at 09:00?" is exactly the
    // question a finished session has to be read against. Nothing in charge control reads it.
    private readonly SolarForecastHistory _history = new();

    public SolcastForecastService(
        IHttpClientFactory httpClientFactory,
        IOptions<SolcastOptions> options,
        ILogger<SolcastForecastService> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SolarForecast? GetForecastForToday()
    {
        var forecast = _cached;
        if (forecast is null)
        {
            return null;
        }

        var localNow = _timeProvider.GetLocalNow();
        return forecast.ForDate(DateOnly.FromDateTime(localNow.DateTime), _timeProvider.LocalTimeZone);
    }

    public SolarForecast? GetForecast(DateTimeOffset from, DateTimeOffset to)
    {
        return _cached?.ForPeriod(from, to);
    }

    public SolarForecast? GetDayForecast(DateOnly localDate) =>
        _history.ForDate(localDate, _timeProvider.LocalTimeZone);

    /// <summary>
    /// When the sun next rises above <paramref name="thresholdWatts"/> according to the cached
    /// forecast, or null if that can't be determined (no forecast yet, or none of the remaining
    /// periods clear the threshold). Used to skip refreshes overnight, where a new forecast cannot
    /// change any decision but still costs an API call against the daily quota.
    /// </summary>
    public DateTimeOffset? NextDaylightStart(DateTimeOffset after, double thresholdWatts) =>
        _cached?.NextPeriodStartAbove(after, thresholdWatts);

    /// <summary>Expected PV power right now per the cached forecast, or null when it can't say.</summary>
    public double? ExpectedPowerWattsNow(DateTimeOffset instant) => _cached?.ExpectedPowerWattsAt(instant);

    /// <summary>
    /// Fetches the latest forecast from Solcast and replaces the cache. On any failure the cache
    /// is left untouched and the error is logged -- callers keep serving the last good forecast.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            // Information rather than a warning, and said once rather than every refresh: this is a
            // setting somebody chose, not a fault. The worker below stops on the same flag, so in
            // practice this guards a direct call rather than the loop.
            if (!_saidItIsOff)
            {
                _saidItIsOff = true;
                _logger.LogInformation(
                    "Solcast is switched off (Solcast:Enabled is false); no forecast will be fetched. "
                    + "The daily quota belongs to the site, so a workstation sharing this configuration "
                    + "spends the controller's calls.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ResourceId))
        {
            _logger.LogWarning(
                "Solcast is not configured (missing ApiKey and/or ResourceId); skipping forecast refresh. "
                + "Set the API key via user-secrets or an environment variable.");
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            // rooftop_sites/{resource_id}/forecasts returns 30-minute pv_estimate periods (kW).
            var requestUri = $"rooftop_sites/{Uri.EscapeDataString(_options.ResourceId)}/forecasts?format=json";
            var response = await client
                .GetFromJsonAsync<SolcastForecastResponse>(requestUri, cancellationToken)
                .ConfigureAwait(false);

            var entries = response?.Forecasts ?? [];
            var periods = entries
                .Select(e => new SolarForecastPeriod(
                    e.PeriodEnd,
                    ParsePeriod(e.Period),
                    EstimatedPowerWatts: e.PvEstimateKw * 1000.0,
                    EstimatedPowerWattsP10: e.PvEstimate10Kw * 1000.0,
                    EstimatedPowerWattsP90: e.PvEstimate90Kw * 1000.0))
                .OrderBy(p => p.PeriodEnd)
                .ToList();

            var forecast = new SolarForecast(_timeProvider.GetUtcNow(), periods);
            _cached = forecast;

            // Merged *after* publication: the history is for later analysis, and no query on the hot
            // path should ever wait behind it.
            _history.Merge(forecast);

            // The day's overall shape is logged here, once per refresh -- the polling loop only
            // logs the live actual-vs-forecast comparison, not this summary.
            var today = GetForecastForToday();
            _logger.LogInformation(
                "Refreshed Solcast forecast: {PeriodCount} periods, PeakToday={PeakPowerWatts:F0}W, "
                + "EnergyToday={EnergyWattHours:F0}Wh (p10 {EnergyP10WattHours:F0}Wh, p90 {EnergyP90WattHours:F0}Wh), "
                + "{RetainedCount} periods retained for analysis.",
                periods.Count,
                today?.PeakPowerWatts ?? 0,
                today?.ExpectedEnergyWattHours ?? 0,
                today?.EnergyWattHoursAt(Core.Enums.ForecastConfidence.P10) ?? 0,
                today?.EnergyWattHoursAt(Core.Enums.ForecastConfidence.P90) ?? 0,
                _history.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the previously cached forecast; a transient outage/rate-limit must not blank it.
            _logger.LogWarning(ex, "Failed to refresh Solcast forecast; keeping the last cached forecast.");
        }
    }

    // Solcast reports the period length as an ISO-8601 duration (e.g. "PT30M").
    private static TimeSpan ParsePeriod(string? period) =>
        string.IsNullOrWhiteSpace(period) ? TimeSpan.FromMinutes(30) : XmlConvert.ToTimeSpan(period);

    private sealed record SolcastForecastResponse(
        [property: JsonPropertyName("forecasts")] IReadOnlyList<SolcastForecastEntry>? Forecasts);

    // pv_estimate10/90 are the 10th/90th-percentile bands Solcast returns alongside the median. The
    // day plan is built on the p10 band: planning an "the battery will be full by evening" guarantee
    // against the median means missing it roughly half the time.
    private sealed record SolcastForecastEntry(
        [property: JsonPropertyName("pv_estimate")] double PvEstimateKw,
        [property: JsonPropertyName("period_end")] DateTimeOffset PeriodEnd,
        [property: JsonPropertyName("period")] string? Period,
        [property: JsonPropertyName("pv_estimate10")] double? PvEstimate10Kw = null,
        [property: JsonPropertyName("pv_estimate90")] double? PvEstimate90Kw = null);
}
