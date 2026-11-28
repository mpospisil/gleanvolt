using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Cars;

/// <summary>
/// Reads the car on its clock (#473): once at start, then every <see cref="PluginCar.NextDelay"/>, at once when
/// woken (Read now, the owner's sign-in) or when Gleanvolt opens a charging session (#414), and never while the car is
/// blocked. With Gleanvolt attached it publishes through the SDK, which asks the car; without, it still reads, so the
/// plugin's page shows the car before the owner has accepted the plugin.
/// </summary>
public sealed class CarPoller : BackgroundService
{
    private readonly VehicleProviderConnection _connection;
    private readonly PluginCar _car;
    private readonly TimeProvider _time;
    private readonly ILogger<CarPoller> _logger;
    private readonly Lock _gate = new();
    private TaskCompletionSource _wake = NewWake();
    private ProviderStatus? _logged;

    public CarPoller(VehicleProviderConnection connection, PluginCar car, TimeProvider time, ILogger<CarPoller> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(car);
        _connection = connection;
        _car = car;
        _time = time;
        _logger = logger;

        // Here rather than when the loop starts: the first welcome, and its session, can come before it.
        connection.SessionChanged += OnSessionChanged;
        car.Resumed += Wake;
        car.Changed += LogStatus;
    }

    /// <summary>Reads now, rather than at the next tick.</summary>
    public void Wake()
    {
        lock (_gate)
        {
            _wake.TrySetResult();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!first)
            {
                await WaitAsync(stoppingToken).ConfigureAwait(false);
            }

            first = false;
            if (stoppingToken.IsCancellationRequested || _car.IsBlocked)
            {
                continue;
            }

            try
            {
                if (_connection.IsAttached)
                {
                    await _connection.PublishNowAsync(stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await _car.ReadAsync(null, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The car reports its own failures; this is anything else, and the clock goes on.
                _logger.LogWarning(ex, "Reading the car failed.");
            }
        }
    }

    // Ends at the car's next tick, on a wake, or on the stop; none of them throws. The tick's timer is cancelled with
    // the wait, so a car woken often leaves none behind.
    private async Task WaitAsync(CancellationToken stoppingToken)
    {
        Task wake;
        lock (_gate)
        {
            wake = _wake.Task;
        }

        using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delay = Task.Delay(_car.NextDelay, _time, tick.Token);
        await Task.WhenAny(delay, wake).ConfigureAwait(false);
        await tick.CancelAsync().ConfigureAwait(false);

        lock (_gate)
        {
            if (_wake.Task.IsCompleted)
            {
                _wake = NewWake();
            }
        }
    }

    // A session opening reads the car at once, rather than at the end of an idle wait, and from then on every
    // PollInterval (#414). The car may not have reported the plug-in yet; the next read catches it.
    private void OnSessionChanged(ChargingSessionInfo? session)
    {
        var opened = session is { Open: true } && !_car.ChargingSessionOpen;
        _car.ChargingSessionOpen = session is { Open: true };

        if (opened)
        {
            Wake();
        }
    }

    // The log is where a failure on a headless machine is found: each change of the car's status, once, and the
    // recovery after one. Unchanged statuses and the first healthy read say nothing.
    private void LogStatus()
    {
        if (_car.Status is not { } status)
        {
            return;
        }

        lock (_gate)
        {
            var previous = _logged;
            if (previous is not null && previous.Healthy == status.Healthy && previous.Code == status.Code && previous.Message == status.Message)
            {
                return;
            }

            _logged = status;
            if (!status.Healthy)
            {
                _logger.LogWarning(
                    "Not reading the car ({Code}{Owner}): {Message}",
                    status.Code ?? "problem", status.NeedsOwner ? ", the owner is needed on the plugin's page" : string.Empty, status.Message);
            }
            else if (previous is { Healthy: false })
            {
                _logger.LogInformation("Reading the car again.");
            }
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
