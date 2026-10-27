using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Infrastructure.Solcast;

/// <summary>
/// <see cref="ISolarForecastService"/> backed by the Solcast rooftop-sites API. Holds the last
/// successfully-fetched forecast in memory and serves queries from it; refreshing is driven
/// externally (see the worker that calls <see cref="RefreshAsync"/> on a schedule). A failed
/// refresh keeps the previously cached forecast intact.
///
/// <para>The account is read from <see cref="SolcastAccountStore"/> on every fetch rather than baked
/// into the HTTP client at startup (issue #286), so an account saved on Settings is used by the next
/// fetch. A key Solcast refused, or a site it does not know, is not asked about again until the account
/// changes: every such retry would be a call nothing can come of.</para>
/// </summary>
public sealed class SolcastForecastService : ISolarForecastService, ISolarForecastRefresh
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used to reach Solcast.</summary>
    public const string HttpClientName = "Solcast";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SolcastAccountStore _account;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SolcastForecastService> _logger;

    // Reference-typed cache updated wholesale on refresh; volatile so query threads see the latest
    // publication. Reads snapshot the field once into a local before use.
    private volatile SolarForecast? _cached;

    // What the cache above cannot hold: the periods that have already gone by. Solcast's forecasts
    // endpoint only ever returns the future, so a wholesale replacement loses this morning the moment
    // this morning happens -- and "what did we expect the roof to do at 09:00?" is exactly the
    // question a finished session has to be read against. Nothing in charge control reads it.
    private readonly SolarForecastHistory _history = new();

    // The history's per-day summaries, worked out once per refresh and published wholesale like the cache.
    // They change only when a refresh lands, and the dashboard asks for today's and tomorrow's on every
    // poll: summing a week of periods every few seconds to get the same numbers back is work with nothing
    // to show for it. Keyed by local date rather than holding "today", because the night sleep outlasts
    // midnight by hours and the day has to roll over without a refresh.
    private volatile IReadOnlyDictionary<DateOnly, SolarDayForecastSummary> _daySummaries =
        FrozenDictionary<DateOnly, SolarDayForecastSummary>.Empty;

    // The worker's schedule and a save on Settings can both ask for a fetch; one at a time, so two
    // answers never race to publish and the history is never merged from two threads.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private volatile SolcastFetchStatus _status = SolcastFetchStatus.Never;

    // The account version Solcast refused (401/403/404), or -1. Compared with the store's, so a new
    // account is tried at once and the refused one never again.
    private int _refusedVersion = -1;

    public SolcastForecastService(
        IHttpClientFactory httpClientFactory,
        SolcastAccountStore account,
        ILogger<SolcastForecastService> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _account = account;
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

    /// <summary>A lookup into the summaries taken at the last refresh; no periods are summed here.</summary>
    public double? GetDayEnergyWattHours(DateOnly localDate) => GetDaySummary(localDate)?.ExpectedWh;

    /// <summary>A lookup into the summaries taken at the last refresh; no periods are summed here.</summary>
    public SolarDayForecastSummary? GetDaySummary(DateOnly localDate) =>
        _daySummaries.TryGetValue(localDate, out var day) ? day : null;

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

    /// <summary>How the last fetch went, for Settings.</summary>
    public SolcastFetchStatus Status => _status;

    /// <summary>Raised after every fetch attempt, whatever came of it, and after <see cref="Forget"/>.</summary>
    public event Action? StatusChanged;

    /// <summary>
    /// Drops the held forecast, for an installation left with no account: charge control then plans
    /// as it does without one, instead of on a forecast that will never be refreshed. The history of
    /// what was expected stays — it is still what was expected.
    /// </summary>
    public void Forget()
    {
        _cached = null;
        _status = SolcastFetchStatus.Never;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Fetches the latest forecast from Solcast and replaces the cache. On any failure the cache
    /// is left untouched and the error is logged -- callers keep serving the last good forecast.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }

        StatusChanged?.Invoke();
    }

    private async Task FetchAsync(CancellationToken cancellationToken)
    {
        // Read once: a save on Settings mid-fetch is for the next one, and the refusal below has to be
        // filed against the account that was actually refused.
        var version = _account.Version;
        var account = _account.Current;

        if (account is null)
        {
            _logger.LogWarning(
                "Solcast is not configured (missing ApiKey and/or ResourceId); skipping forecast refresh. "
                + "Enter the account on Settings, or set Solcast__ApiKey and Solcast__ResourceId.");
            return;
        }

        if (Volatile.Read(ref _refusedVersion) == version)
        {
            _logger.LogInformation(
                "Solcast refused this account at {FailedAt:u}; not asking again until it is changed on Settings.",
                _status.FailedAt);
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            // rooftop_sites/{resource_id}/forecasts returns 30-minute pv_estimate periods (kW).
            var requestUri = $"rooftop_sites/{Uri.EscapeDataString(account.ResourceId)}/forecasts?format=json";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.ApiKey);

            using var reply = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!reply.IsSuccessStatusCode)
            {
                Refused(reply.StatusCode, version, account);
                return;
            }

            var response = await reply.Content
                .ReadFromJsonAsync<SolcastForecastResponse>(cancellationToken)
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

            var fetchedAt = _timeProvider.GetUtcNow();
            var forecast = new SolarForecast(fetchedAt, periods);
            _cached = forecast;
            _status = new SolcastFetchStatus(fetchedAt, null, SolcastFetchFailure.None, (int)reply.StatusCode);
            Volatile.Write(ref _refusedVersion, -1);

            // Merged, and the day summaries taken from it, *after* publication: the history is for later
            // analysis, and no query on the hot path should ever wait behind it.
            _history.Merge(forecast);
            _daySummaries = _history.DailySummaries(_timeProvider.LocalTimeZone);

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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Keep the previously cached forecast; a transient outage/rate-limit must not blank it. A
            // timeout is an OperationCanceledException too, but not ours, so it lands here as well.
            _status = _status with { FailedAt = _timeProvider.GetUtcNow(), Failure = SolcastFetchFailure.Other, HttpStatus = null };
            _logger.LogWarning(ex, "Failed to refresh Solcast forecast; keeping the last cached forecast.");
        }
    }

    // A status that is an answer: kept on the status line, and for a refusal remembered against the
    // account so the next scheduled refresh does not spend a call to be told the same thing.
    private void Refused(HttpStatusCode status, int version, SolcastAccount account)
    {
        var failure = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SolcastFetchFailure.KeyRefused,
            HttpStatusCode.NotFound => SolcastFetchFailure.SiteNotFound,
            HttpStatusCode.TooManyRequests => SolcastFetchFailure.QuotaExhausted,
            _ => SolcastFetchFailure.Other,
        };

        _status = _status with { FailedAt = _timeProvider.GetUtcNow(), Failure = failure, HttpStatus = (int)status };

        if (_status.IsRefused)
        {
            Volatile.Write(ref _refusedVersion, version);
        }

        _logger.LogWarning(
            "Solcast answered {StatusCode} for rooftop site {ResourceId} ({Failure}); keeping the last cached forecast.",
            (int)status,
            account.ResourceId,
            failure);
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
