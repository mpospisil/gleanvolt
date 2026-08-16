using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

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

            _cached = new SolarForecast(_timeProvider.GetUtcNow(), periods);

            // The day's overall shape is logged here, once per refresh -- the polling loop only
            // logs the live actual-vs-forecast comparison, not this summary.
            var today = GetForecastForToday();
            _logger.LogInformation(
                "Refreshed Solcast forecast: {PeriodCount} periods, PeakToday={PeakPowerWatts:F0}W, "
                + "EnergyToday={EnergyWattHours:F0}Wh (p10 {EnergyP10WattHours:F0}Wh, p90 {EnergyP90WattHours:F0}Wh).",
                periods.Count,
                today?.PeakPowerWatts ?? 0,
                today?.ExpectedEnergyWattHours ?? 0,
                today?.EnergyWattHoursAt(Core.Enums.ForecastConfidence.P10) ?? 0,
                today?.EnergyWattHoursAt(Core.Enums.ForecastConfidence.P90) ?? 0);
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
