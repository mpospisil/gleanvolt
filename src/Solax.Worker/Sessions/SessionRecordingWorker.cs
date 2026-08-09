using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;
using Solax.Worker.Configuration;

namespace Solax.Worker.Sessions;

/// <summary>
/// Records charging sessions to the local store.
///
/// <para>It is a <b>subscriber</b> to <see cref="ChargeControlStatusHolder.Updated"/>, exactly like the
/// Home Assistant worker — the poll loop knows nothing about it. That is the whole point: the control
/// path must never acquire a dependency on a disk, and a failing store must never be able to stall a
/// Modbus cycle.</para>
///
/// <para>The handler does nothing but drop the snapshot into a channel. All the work — attribution,
/// session tracking, SQLite — happens on this worker's own loop, so a slow SD card costs latency here
/// and nowhere else.</para>
/// </summary>
public sealed class SessionRecordingWorker : BackgroundService
{
    // ~40 minutes of buffer at the default 5-second poll. If the writer ever falls that far behind,
    // the oldest snapshots are dropped rather than blocking the publisher: the energy integrators
    // simply hold the last known power across the gap, which costs a little accuracy and nothing else.
    private const int QueueCapacity = 512;

    private readonly Channel<ChargeControlStatus> _queue = Channel.CreateBounded<ChargeControlStatus>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly SessionStoreOptions _options;
    private readonly IChargingSessionStore _store;
    private readonly ChargeControlStatusHolder _statusHolder;
    private readonly ISolarForecastService _forecast;
    private readonly ChargingSessionTracker _tracker;
    private readonly ILogger<SessionRecordingWorker> _logger;
    private readonly TimeProvider _timeProvider;

    // Pending rows, held back so a busy session commits once a minute instead of once a poll.
    private readonly List<ChargingSessionSample> _pendingSamples = [];
    private readonly List<ChargingSessionEvent> _pendingEvents = [];
    private DateTimeOffset _lastFlush;

    // Set once the store has proved unusable, so a broken mount degrades to "no history" rather than
    // to a warning on every single poll for as long as the service runs.
    private bool _storeFailed;

    public SessionRecordingWorker(
        IOptions<SessionStoreOptions> options,
        IChargingSessionStore store,
        ChargeControlStatusHolder statusHolder,
        ISolarForecastService forecast,
        ILogger<SessionRecordingWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _store = store;
        _statusHolder = statusHolder;
        _forecast = forecast;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tracker = new ChargingSessionTracker(
            _options.SampleInterval,
            _options.RecordUncontrolledSessions,
            _timeProvider);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Charging session recording is disabled.");
            return;
        }

        try
        {
            var recovered = await _store.InitializeAsync(stoppingToken).ConfigureAwait(false);
            if (recovered > 0)
            {
                _logger.LogInformation("{Count} interrupted charging session(s) were closed at their last sample.", recovered);
            }

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
                "Could not open the charging session store; sessions will not be recorded this run. "
                + "Everything else — polling, charge control, Home Assistant — is unaffected.");
            return;
        }

        _logger.LogInformation(
            "Recording charging sessions: sampling every {SampleInterval}, committing every {FlushInterval}, "
            + "keeping {RetentionDays} days{Uncontrolled}.",
            _options.SampleInterval,
            _options.FlushInterval,
            _options.RetentionDays,
            _options.RecordUncontrolledSessions ? ", including uncontrolled sessions" : string.Empty);

        _statusHolder.Updated += OnStatusUpdated;
        _lastFlush = _timeProvider.GetUtcNow();

        try
        {
            await foreach (var status in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await HandleAsync(status, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown; StopAsync closes the open session.
        }
        finally
        {
            _statusHolder.Updated -= OnStatusUpdated;
        }
    }

    /// <summary>
    /// Closes any session still open, so a planned restart produces a session ended with
    /// <see cref="ChargingSessionEndReason.ServiceStopped"/> rather than one the next startup has to
    /// recover as interrupted. Runs on a fresh token, like the charger's own shutdown pause.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled && !_storeFailed)
        {
            try
            {
                var update = _tracker.Close(ChargingSessionEndReason.ServiceStopped, _timeProvider.GetUtcNow());
                await PersistAsync(update, cancellationToken).ConfigureAwait(false);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close the open charging session on shutdown; it will be recovered on next startup.");
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
            var update = _tracker.Observe(status, ForecastPowerWatts(status.Timestamp), ForecastRemainingTodayWh(status.Timestamp));
            await PersistAsync(update, cancellationToken).ConfigureAwait(false);
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
            _logger.LogWarning(ex, "Failed to record a charging session update; continuing.");
        }
    }

    private async Task PersistAsync(ChargingSessionUpdate update, CancellationToken cancellationToken)
    {
        if (update.IsEmpty)
        {
            return;
        }

        if (update.Started is { } started)
        {
            // Written immediately rather than batched: the header is what everything else references,
            // and a power cut that loses it would orphan every sample behind it.
            await _store.StartSessionAsync(started, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Charging session {SessionId} started in {Mode} at {Soc:F0}% SOC.",
                started.Id,
                started.StartMode,
                started.StartBatterySocPercent);
        }

        if (update.Sample is { } sample)
        {
            _pendingSamples.Add(sample);
        }

        if (update.Events.Count > 0)
        {
            _pendingEvents.AddRange(update.Events);
        }

        if (update.Ended is { } ended)
        {
            // Flush before completing so the header's totals never describe rows that aren't there yet.
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            await _store.CompleteSessionAsync(ended, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Charging session {SessionId} ended ({Reason}) after {Duration:hh\\:mm}: {Delivered:F2}kWh delivered "
                + "— {Solar:F2} solar / {Grid:F2} grid / {Battery:F2} battery, SOC {StartSoc:F0}% → {EndSoc:F0}%.",
                ended.Id,
                ended.EndReason,
                ended.Duration(ended.EndedAt!.Value),
                ended.EnergyDeliveredWh / 1000,
                ended.FromSolarWh / 1000,
                ended.FromGridWh / 1000,
                ended.FromBatteryWh / 1000,
                ended.StartBatterySocPercent,
                ended.EndBatterySocPercent ?? 0);

            return;
        }

        if (_timeProvider.GetUtcNow() - _lastFlush >= _options.FlushInterval)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        _lastFlush = _timeProvider.GetUtcNow();

        if (_pendingSamples.Count == 0 && _pendingEvents.Count == 0)
        {
            return;
        }

        // Copied out and cleared before the await: a failed write drops the batch rather than
        // retrying it forever, which is the right trade for telemetry.
        var samples = _pendingSamples.ToArray();
        var events = _pendingEvents.ToArray();
        _pendingSamples.Clear();
        _pendingEvents.Clear();

        await _store.AppendAsync(samples, events, cancellationToken).ConfigureAwait(false);
    }

    private double? ForecastPowerWatts(DateTimeOffset at) =>
        _forecast.GetForecastForToday()?.ExpectedPowerWattsAt(at);

    /// <summary>
    /// Forecast PV still to come between now and the end of the local day — the "estimated solar
    /// energy" a session is judged against, recorded once on the header when the session opens.
    /// </summary>
    private double? ForecastRemainingTodayWh(DateTimeOffset now)
    {
        var zone = _timeProvider.LocalTimeZone;
        var localMidnight = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1);
        var endOfDay = new DateTimeOffset(localMidnight, zone.GetUtcOffset(localMidnight));

        var forecast = _forecast.GetForecast(now, endOfDay);
        return forecast?.Periods.Count > 0 ? forecast.ExpectedEnergyWattHours : null;
    }
}
