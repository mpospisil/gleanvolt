using Gleanvolt.Core.Models;
using Gleanvolt.Providers.Contracts.Vehicle;
using Gleanvolt.Providers.Server;
using Microsoft.AspNetCore.SignalR;

namespace Gleanvolt.Hosting.Vehicles.External;

/// <summary>
/// Tells the car provider whether Gleanvolt has a charging session open (#414), so it can read the car
/// as often as the built-in feeds do while one is: every 5 minutes, rather than waiting for the car to
/// report that it charges. The welcome carries the state as the provider says hello
/// (<see cref="Current"/>); <see cref="IVehicleClient.ChargingSessionChanged"/> carries every change.
///
/// <para>Charge control reports every poll, every few seconds; only a change is sent. The state is also
/// said when a provider attaches: a session that opened between its welcome and its attaching would
/// otherwise go unsaid. The SDK ignores a state it already has.</para>
/// </summary>
public sealed class ChargingSessionNotifier : IHostedService
{
    private readonly ChargeControlStatusHolder _status;
    private readonly ExternalProviderRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<ChargingSessionNotifier> _logger;
    private readonly Lock _lock = new();

    private readonly SemaphoreSlim _sending = new(1, 1);

    private ChargingSessionInfo _current = ChargingSessionInfo.Closed;
    private (string Connection, ChargingSessionInfo Session)? _sent;

    public ChargingSessionNotifier(
        ChargeControlStatusHolder status,
        ExternalProviderRegistry registry,
        IServiceProvider services,
        TimeProvider time,
        ILogger<ChargingSessionNotifier> logger)
    {
        _status = status;
        _registry = registry;
        _services = services;
        _time = time;
        _logger = logger;
    }

    /// <summary>The session as a provider is told it.</summary>
    public ChargingSessionInfo Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_status.Current is { } current)
        {
            Observe(current);
        }

        _status.Updated += Observe;
        _registry.Changed += OnProvidersChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _status.Updated -= Observe;
        _registry.Changed -= OnProvidersChanged;
        return Task.CompletedTask;
    }

    /// <summary>Takes charge control's latest status; a change is sent to the attached provider.</summary>
    internal void Observe(ChargeControlStatus status)
    {
        lock (_lock)
        {
            var next = !status.ChargingSessionOpen
                ? ChargingSessionInfo.Closed
                : new ChargingSessionInfo(true, _current.Open ? _current.Since : _time.GetUtcNow(), status.Mode.ToString());

            if (next == _current)
            {
                return;
            }

            _current = next;
        }

        _ = SendLatestAsync();
    }

    // The attached provider's connection, when it knows the message (1.2): an older one would only log
    // that it has no handler for it.
    private string? ConnectionId =>
        _registry.For(VehicleContract.Kind) is { ContractMinor: >= 2, ConnectionId: { } connection } ? connection : null;

    // A provider attached (or left): a new one hears the state once.
    private void OnProvidersChanged() => _ = SendLatestAsync();

    // One send at a time, and each sends the state as it is then, not as it was when asked: two sends in
    // flight could arrive out of order and leave the provider with the older one. What was last said to
    // a connection is not said again; a failed send is tried again on the next change.
    private async Task SendLatestAsync()
    {
        await _sending.WaitAsync().ConfigureAwait(false);

        try
        {
            if (ConnectionId is not { } connection)
            {
                return;
            }

            var session = Current;

            if (_sent is { } sent && sent.Connection == connection && sent.Session == session)
            {
                return;
            }

            _logger.LogInformation(
                session.Open
                    ? "Telling the car provider a charging session is open ({Mode})."
                    : "Telling the car provider no charging session is open.",
                session.Mode);

            await _services.GetRequiredService<IHubContext<VehicleHub, IVehicleClient>>()
                .Clients.Client(connection)
                .ChargingSessionChanged(session)
                .ConfigureAwait(false);

            _sent = (connection, session);
        }
        catch (Exception ex)
        {
            // Advisory: the provider falls back to the car's own charge state, and hears the next change.
            _logger.LogDebug("Could not tell the car provider about the charging session ({Reason}).", ex.Message);
        }
        finally
        {
            _sending.Release();
        }
    }
}
