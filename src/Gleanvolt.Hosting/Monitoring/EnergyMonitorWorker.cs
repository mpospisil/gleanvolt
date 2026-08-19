using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Monitoring;

/// <summary>
/// Records the site's energy history as fixed-length intervals — one row per quarter hour by default,
/// every day, whether or not a car is anywhere near the charger.
///
/// <para><b>What it is for.</b> The session store answers questions about charges; this answers
/// questions about the <em>site</em>: how much the roof made last March, how well the forecast tracked
/// it across a season, how much was exported at noon while the evening was bought back from the grid.
/// None of that is derivable from session data, because most of the year has no session in it.</para>
///
/// <para><b>It observes and nothing else.</b> Like <see cref="Sessions.SessionRecordingWorker"/> it is
/// a subscriber to <see cref="ChargeControlStatusHolder.Updated"/>, so the poll loop knows nothing
/// about it, touches no register, and cannot be stalled by a slow disk. The handler does nothing but
/// drop the snapshot into a channel; the integration and the SQLite write happen on this worker's own
/// loop.</para>
///
/// <para><b>Separate from the session recorder rather than folded into it</b>, even though both
/// consume the same snapshots. They have different lifetimes (one runs while a car charges, this runs
/// always), different retention, and different files — and the request that produced this was
/// explicitly for a service responsible for monitoring alone. Sharing a worker would couple the two
/// failure paths for nothing but one less class.</para>
/// </summary>
public sealed class EnergyMonitorWorker : BackgroundService
{
    // ~40 minutes of buffer at the default 5-second poll. If the writer ever falls that far behind,
    // the oldest snapshots are dropped rather than blocking the publisher: the interval simply loses
    // a little of its coverage, which the row then says so on covered_seconds.
    private const int QueueCapacity = 512;

    private readonly Channel<ChargeControlStatus> _queue = Channel.CreateBounded<ChargeControlStatus>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly EnergyMonitorOptions _options;
    private readonly IEnergyIntervalStore _store;
    private readonly ChargeControlStatusHolder _statusHolder;
    private readonly ISolarForecastService _forecast;
    private readonly EnergyIntervalTracker _tracker;
    private readonly ILogger<EnergyMonitorWorker> _logger;
    private readonly TimeProvider _timeProvider;

    // Set once the store has proved unusable, so a broken mount degrades to "no history" rather than
    // to a warning on every poll for as long as the service runs.
    private bool _storeFailed;

    public EnergyMonitorWorker(
        IOptions<EnergyMonitorOptions> options,
        IEnergyIntervalStore store,
        ChargeControlStatusHolder statusHolder,
        ISolarForecastService forecast,
        ILogger<EnergyMonitorWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _store = store;
        _statusHolder = statusHolder;
        _forecast = forecast;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tracker = new EnergyIntervalTracker(_options.Interval, _options.MaxGap, _timeProvider);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Energy interval monitoring is disabled.");
            return;
        }

        // A misconfigured interval is corrected rather than fatal: the tracker has already fallen back
        // to 15 minutes, and losing the history over a config typo would be a worse outcome than
        // recording it at a length the operator didn't ask for and can see in this line.
        if (!EnergyIntervalTracker.IsValidInterval(_options.Interval))
        {
            _logger.LogWarning(
                "EnergyMonitor:Interval of {Configured} does not divide 24 hours evenly and would leave a "
                + "short bucket at midnight; recording at {Interval} instead.",
                _options.Interval,
                _tracker.Interval);
        }

        try
        {
            await _store.InitializeAsync(stoppingToken).ConfigureAwait(false);

            if (_options.RetentionDays > 0)
            {
                await _store.PruneAsync(TimeSpan.FromDays(_options.RetentionDays), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _storeFailed = true;
            _logger.LogError(
                ex,
                "Could not open the energy interval store; the energy history will not be recorded this run. "
                + "Everything else — polling, charge control, session recording, Home Assistant — is unaffected.");
            return;
        }

        _logger.LogInformation(
            "Recording energy intervals of {Interval}, {Retention}.",
            _tracker.Interval,
            _options.RetentionDays > 0 ? $"keeping {_options.RetentionDays} days" : "keeping everything");

        _statusHolder.Updated += OnStatusUpdated;

        try
        {
            await foreach (var status in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await HandleAsync(status, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown; StopAsync writes the part-finished interval.
        }
        finally
        {
            _statusHolder.Updated -= OnStatusUpdated;
        }
    }

    /// <summary>
    /// Writes the interval that was still filling, so a planned restart keeps the minutes before it
    /// instead of throwing them away. The store merges this partial row with the one the next run
    /// contributes for the same window, so the two halves end up as one whole interval. Runs on a
    /// fresh token, like the charger's own shutdown pause.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled && !_storeFailed)
        {
            try
            {
                var completed = _tracker.Flush(_timeProvider.GetUtcNow());
                await WriteAsync(completed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write the part-finished energy interval on shutdown; it is lost.");
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    // Runs on the poll loop's thread. It must do nothing that can throw or block -- writing to a
    // bounded channel that drops rather than waits satisfies both.
    private void OnStatusUpdated(ChargeControlStatus status) => _queue.Writer.TryWrite(status);

    private async Task HandleAsync(ChargeControlStatus status, CancellationToken cancellationToken)
    {
        if (_storeFailed)
        {
            return;
        }

        try
        {
            var completed = _tracker.Observe(status, ForecastPowerWatts(status.Timestamp));
            await WriteAsync(completed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad write must not end recording for the rest of the run, but a store that keeps
            // failing shouldn't fill the log either -- so this warns and carries on, and only the
            // startup path (above) can disable the feature outright.
            _logger.LogWarning(ex, "Failed to record an energy interval; continuing.");
        }
    }

    private async Task WriteAsync(IReadOnlyList<EnergyInterval> intervals, CancellationToken cancellationToken)
    {
        if (intervals.Count == 0)
        {
            return;
        }

        await _store.AppendAsync(intervals, cancellationToken).ConfigureAwait(false);

        foreach (var interval in intervals)
        {
            _logger.LogDebug(
                "Energy interval {Start:HH:mm}–{End:HH:mm}: {Solar:F2} kWh solar (forecast {Forecast}), "
                + "{Import:F2} in / {Export:F2} out, {Ev:F2} to the car, battery {Charge:F2}/{Discharge:F2}, "
                + "SOC {SocStart:F0}%→{SocEnd:F0}%, {Coverage:P0} covered.",
                interval.PeriodStart,
                interval.PeriodEnd,
                interval.SolarKwh,
                interval.ForecastSolarKwh is { } forecast ? $"{forecast:F2} kWh" : "none",
                interval.GridImportKwh,
                interval.GridExportKwh,
                interval.EvKwh,
                interval.BatteryChargeKwh,
                interval.BatteryDischargeKwh,
                interval.SocStartPercent,
                interval.SocEndPercent,
                interval.Coverage);
        }
    }

    /// <summary>
    /// What the forecast expected at this instant, or null when none covers it. Read from the forecast
    /// service rather than taken off <see cref="ChargeControlStatus.ForecastSolarPowerWatts"/>, which
    /// reports 0 for "no forecast" and so cannot be told apart from a genuine night-time zero — and
    /// the difference is exactly what <c>forecast_solar_kwh</c>'s nullability exists to preserve.
    /// </summary>
    private double? ForecastPowerWatts(DateTimeOffset at) =>
        _forecast.GetForecastForToday()?.ExpectedPowerWattsAt(at);
}
