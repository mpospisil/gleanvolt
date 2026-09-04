using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Vehicles;

/// <summary>
/// Runs each registered <see cref="IVehicleUpdateService"/> on the delay that service asks for, and
/// writes what comes back into <see cref="VehicleStateHolder"/> (issue #140).
///
/// <para><b>The host owns the cross-cutting parts and nothing else</b> — cancellation, the structured
/// log line, the floor under a delay, and the rule that a service's I/O never paces anything. It does
/// not decide when to ask: the interval is re-read from the service after every fetch, because VW's
/// portal is a quarter-hour batch and a car that has to be woken to answer is a different problem, and
/// no figure here could be right for both.</para>
///
/// <para><b>One loop per service, not one loop over services.</b> Two feeds on two cadences would
/// otherwise be paced by whichever was slower, and a manufacturer's cloud timing out would hold up
/// every other car behind it.</para>
///
/// <para><b>A blocked service is stopped, not slowed.</b> When a service reports
/// <see cref="VehicleSourceState.NeedsOwner"/> — a refused password, a consent screen, an OTP — asking
/// again cannot help and replaying a password on a clock is how accounts get locked. The loop ends,
/// the dashboard says <i>sign-in required</i>, and it is a restart (after the owner has done their
/// part) that puts the feed back on its clock.</para>
///
/// <para>Nothing here is on any hardware path. The car is advisory data: a manufacturer's cloud that
/// is unreachable, refusing or simply empty changes nothing about how the charger is driven.</para>
/// </summary>
public sealed class VehicleUpdateWorker : BackgroundService
{
    /// <summary>
    /// The shortest gap the host will honour, whatever a service asks for. A floor rather than a
    /// policy: it exists so that a service returning zero cannot turn into a spin against somebody's
    /// identity provider.
    /// </summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often a feed that is blocked on its owner says so again in the log.
    ///
    /// <para>Saying it once, at the moment it happens, is not saying it: a container's log is read
    /// hours or days later, most often with <c>--tail</c>, and by then the one warning has scrolled
    /// out of reach and the feed is silent in a way indistinguishable from a car that is parked. This
    /// costs four lines a day and no network traffic at all — <b>nothing</b> is fetched while blocked,
    /// which is the entire point of stopping.</para>
    /// </summary>
    public static readonly TimeSpan BlockedReminder = TimeSpan.FromHours(6);

    private readonly IReadOnlyList<IVehicleUpdateService> _services;
    private readonly VehicleStateHolder _holder;
    private readonly ILogger<VehicleUpdateWorker> _logger;
    private readonly TimeProvider _time;
    private readonly bool _mqttFeedConfigured;
    private readonly bool _onDemandOnly;

    /// <param name="vehicleOptions">
    /// The MQTT feed's settings, read for one line of log and nothing else: an installation running
    /// both feeds should be told so at startup, because "two sources, newest wins" is worth knowing
    /// before you wonder why the card sometimes moves between readings.
    /// </param>
    public VehicleUpdateWorker(
        IEnumerable<IVehicleUpdateService> services,
        VehicleStateHolder holder,
        ILogger<VehicleUpdateWorker> logger,
        IOptions<VehicleOptions>? vehicleOptions = null,
        TimeProvider? time = null)
    {
        _services = services.ToList();
        _holder = holder;
        _logger = logger;
        _mqttFeedConfigured = vehicleOptions?.Value.Enabled ?? false;
        _onDemandOnly = vehicleOptions?.Value.OnDemandOnly ?? false;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_services.Count == 0)
        {
            // The ordinary case, and deliberately not a warning: a car with no manufacturer feed
            // configured is a supported installation, not a misconfigured one.
            _logger.LogInformation("No vehicle update service is configured.");
            return;
        }

        if (_onDemandOnly)
        {
            // The services stay registered and IVehicleStateRefresh still drives them; what stops is
            // the clock. Logged at Information because a silent feed is otherwise indistinguishable
            // from a broken one.
            _logger.LogInformation(
                "Vehicle updates are on demand only: nothing is polled, and the car is asked when the "
                + "web UI or a plan asks for it.");
            return;
        }

        if (_mqttFeedConfigured)
        {
            _logger.LogInformation(
                "Both vehicle feeds are on: the MQTT topic and the manufacturer's own service. They "
                + "write to one holder and the newest reading wins, so whichever saw the car most "
                + "recently is what the dashboard shows.");
        }

        await Task.WhenAll(_services.Select(service => RunAsync(service, stoppingToken))).ConfigureAwait(false);
    }

    private async Task RunAsync(IVehicleUpdateService service, CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Vehicle update service {Manufacturer} started for {Vehicle}; it asks to be run every {Delay}.",
            service.Manufacturer, service.VehicleId, service.NextDelay);

        var lastHealth = service.Health;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var state = await service.FetchAsync(stoppingToken).ConfigureAwait(false);

                if (state is not null)
                {
                    // A rejected fetch deliberately leaves the previous reading in place: "the reading
                    // is getting older" is diagnosable, whereas blanking it looks exactly like having
                    // no car at all. Same rule the MQTT feed follows (#73).
                    var taken = _holder.Set(state);

                    _logger.LogInformation(
                        "{Manufacturer} read {Vehicle}: SOC={Soc}% charge={ChargeState} plug={PlugState} "
                        + "captured {CapturedAt:O}.{Ignored}",
                        service.Manufacturer, service.VehicleId, state.SocPercent, state.ChargeState,
                        state.PlugState, state.CapturedAt,
                        // Not a fault and worth saying: on an installation running both feeds this is
                        // the line that explains why the card did not move.
                        taken ? string.Empty : " Another feed holds a newer reading, so this one stands.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A service is contracted not to throw for an expected failure, so this is a bug in
                // one. It is logged and the loop goes on: a broken feed must not take the controller
                // down with it.
                _logger.LogError(
                    ex, "The {Manufacturer} update service for {Vehicle} threw; it will be asked again.",
                    service.Manufacturer, service.VehicleId);
            }

            lastHealth = Report(service, lastHealth);

            var delay = service.NextDelay;

            if (service.Health.IsBlocked || delay < TimeSpan.Zero)
            {
                await WaitOnTheOwnerAsync(service, stoppingToken).ConfigureAwait(false);
                break;
            }

            try
            {
                await Task.Delay(Floor(delay), _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// What a stopped feed does for the rest of the process's life: nothing, loudly.
    ///
    /// <para>It never fetches again — a password replayed on a clock is how accounts get locked, and a
    /// consent screen is answered by nobody here. But it repeats the reason on
    /// <see cref="BlockedReminder"/> so that the log of a controller that has been sitting blocked for
    /// three days says so, rather than saying nothing at all. The web UI carries the same sentence on
    /// every page for as long as this is true.</para>
    /// </summary>
    private async Task WaitOnTheOwnerAsync(IVehicleUpdateService service, CancellationToken stoppingToken)
    {
        while (true)
        {
            _logger.LogWarning(
                "The {Manufacturer} feed for {Vehicle} has stopped and needs you: {Reason} It will not "
                + "be asked again until you have cleared it and restarted the controller — press "
                + "\"Read the car now\" on the Vehicle portal page to check.",
                service.Manufacturer, service.VehicleId, service.Health.Message);

            try
            {
                await Task.Delay(BlockedReminder, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Logs the health only when it changes. A feed that is degraded for a day would otherwise write
    /// the same warning ninety-six times, which is the noise this project has spent several issues
    /// removing from elsewhere.
    /// </summary>
    private VehicleSourceHealth Report(IVehicleUpdateService service, VehicleSourceHealth last)
    {
        var health = service.Health;

        if (health == last)
        {
            return last;
        }

        if (health.State == VehicleSourceState.Ok)
        {
            _logger.LogInformation(
                "The {Manufacturer} feed for {Vehicle} is healthy again: {Reason}",
                service.Manufacturer, service.VehicleId, health.Message);
        }
        else
        {
            _logger.LogWarning(
                "The {Manufacturer} feed for {Vehicle} is {State}: {Reason}",
                service.Manufacturer, service.VehicleId, health.State, health.Message);
        }

        return health;
    }

    private static TimeSpan Floor(TimeSpan delay) => delay < MinimumDelay ? MinimumDelay : delay;
}
