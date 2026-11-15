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
/// The Solcast rooftop-sites API as a forecast source (issue #387): fetches the forecast and publishes it
/// to <see cref="SolarForecastStore"/>, which holds it and answers every query. A failed fetch publishes
/// nothing, so the store keeps the last good forecast.
///
/// <para>The account is read from <see cref="SolcastAccountStore"/> on every fetch rather than baked
/// into the HTTP client at startup (issue #286), so an account saved on Settings is used by the next
/// fetch. A key Solcast refused, or a site it does not know, is not asked about again until the account
/// changes: every such retry would be a call nothing can come of.</para>
/// </summary>
public sealed class SolcastSource : ISolarForecastSource
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used to reach Solcast.</summary>
    public const string HttpClientName = "Solcast";

    private static readonly SolarForecastOrigin Origin = new(SolarForecastSourceIds.Solcast, "Solcast");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SolcastAccountStore _account;
    private readonly SolarForecastStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SolcastSource> _logger;

    // The worker's schedule and a save on Settings can both ask for a fetch; one at a time, so two
    // answers never race to publish.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private volatile SolcastFetchStatus _status = SolcastFetchStatus.Never;

    // The account version Solcast refused (401/403/404), or -1. Compared with the store's, so a new
    // account is tried at once and the refused one never again.
    private int _refusedVersion = -1;

    public SolcastSource(
        IHttpClientFactory httpClientFactory,
        SolcastAccountStore account,
        SolarForecastStore store,
        ILogger<SolcastSource> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _account = account;
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Id => SolarForecastSourceIds.Solcast;

    /// <summary>How the last fetch went, for Settings.</summary>
    public SolcastFetchStatus Status => _status;

    /// <summary>Raised after every fetch attempt, whatever came of it, and after <see cref="Forget"/>.</summary>
    public event Action? StatusChanged;

    /// <summary>
    /// For an installation left with no account: drops the held forecast if it is Solcast's, so charge
    /// control plans as it does without one instead of on a forecast that will never be refreshed, and
    /// clears the status line. A forecast another source published is not Solcast's to drop.
    /// </summary>
    public void Forget()
    {
        if (_store.Current?.Origin.SourceId == Id)
        {
            _store.Forget();
        }

        _status = SolcastFetchStatus.Never;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Fetches the latest forecast from Solcast and publishes it. On any failure nothing is published
    /// and the error is logged -- the store keeps serving the last good forecast.
    /// </summary>
    public async Task RequestRefreshAsync(CancellationToken cancellationToken = default)
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
            _store.Publish(new SolarForecast(fetchedAt, periods), Origin);
            _status = new SolcastFetchStatus(fetchedAt, null, SolcastFetchFailure.None, (int)reply.StatusCode);
            Volatile.Write(ref _refusedVersion, -1);

            // The day's overall shape is logged here, once per refresh -- the polling loop only
            // logs the live actual-vs-forecast comparison, not this summary.
            var today = _store.GetForecastForToday();
            _logger.LogInformation(
                "Refreshed Solcast forecast: {PeriodCount} periods, PeakToday={PeakPowerWatts:F0}W, "
                + "EnergyToday={EnergyWattHours:F0}Wh (p10 {EnergyP10WattHours:F0}Wh, p90 {EnergyP90WattHours:F0}Wh), "
                + "{RetainedCount} periods retained for analysis.",
                periods.Count,
                today?.PeakPowerWatts ?? 0,
                today?.ExpectedEnergyWattHours ?? 0,
                today?.EnergyWattHoursAt(Core.Enums.ForecastConfidence.P10) ?? 0,
                today?.EnergyWattHoursAt(Core.Enums.ForecastConfidence.P90) ?? 0,
                _store.RetainedPeriodCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Publish nothing, so the store keeps the last good forecast; a transient outage/rate-limit must not blank it. A
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
