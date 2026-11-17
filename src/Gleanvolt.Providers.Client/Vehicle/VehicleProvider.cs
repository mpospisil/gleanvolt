using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Client.Vehicle;

/// <summary>What a car provider's author writes: the car's latest reading, on demand.</summary>
public interface IVehicleSource
{
    /// <summary>
    /// The car's latest reading, or null when there is nothing to send yet. <see cref="VehicleReading.InReplyTo"/>
    /// is filled in by the SDK.
    /// </summary>
    /// <param name="car">The car, from the welcome or the latest <see cref="IVehicleClient.VehicleChanged"/>.</param>
    Task<VehicleReading?> GetReadingAsync(VehicleInfo car, CancellationToken cancellationToken);
}

/// <summary>
/// The car's connection (#408): publishes once after every accepted hello, answers every
/// <see cref="IVehicleClient.RefreshRequested"/>, publishes again when the car changes, and publishes
/// whenever the provider calls <see cref="PublishNowAsync"/>. A car provider mostly publishes on its own
/// events (plugging in, a charge starting, a step in SOC); Gleanvolt does not poll it.
///
/// <para>It also keeps whether Gleanvolt has a charging session open (<see cref="Session"/>, #414), for a
/// provider that reads the car on a clock: read more often while one is.</para>
/// </summary>
public sealed class VehicleProviderConnection : ProviderConnection<VehicleWelcome>
{
    private readonly IVehicleSource _source;
    private VehicleInfo? _car;
    private readonly Lock _sessionGate = new();
    private volatile ChargingSessionInfo? _session;

    public VehicleProviderConnection(
        IOptions<ProviderOptions> options, IVehicleSource source, ILogger<VehicleProviderConnection> logger)
        : base(options.Value, VehicleContract.HubPath, VehicleContract.Major, VehicleContract.Minor, logger)
    {
        _source = source;
        Welcomed += welcome =>
        {
            _car = welcome.Car;
            SetSession(welcome.Session);
        };
    }

    /// <summary>The car: the welcome's, or the latest change. Null before the first hello.</summary>
    public VehicleInfo? Car => _car;

    /// <summary>
    /// Whether Gleanvolt has a charging session open: the welcome's, or the latest
    /// <see cref="IVehicleClient.ChargingSessionChanged"/>. Null before the first hello, and from a
    /// Gleanvolt older than vehicle 1.2, which doesn't say: go by the car's own charge state then.
    /// </summary>
    public ChargingSessionInfo? Session => _session;

    /// <summary>
    /// <see cref="Session"/> changed: a session opened or closed, or an open one's mode changed. Raised
    /// on the connection's thread; keep it short.
    /// </summary>
    public event Action<ChargingSessionInfo?>? SessionChanged;

    /// <summary>
    /// Asks the source and publishes, unprompted: for a provider that has just seen the car change.
    /// Null when no session is attached or the source had nothing.
    /// </summary>
    public Task<PublishResult?> PublishNowAsync(CancellationToken cancellationToken = default) => PublishAsync(null, cancellationToken);

    protected override void OnConfigure(HubConnection connection)
    {
        connection.On<RefreshRequest>(ProviderMethods.RefreshRequested, request =>
        {
            // Answered from a task of its own: the receive loop is not held while the source works.
            _ = Task.Run(() => PublishAsync(request.Id, CancellationToken.None));
            return Task.CompletedTask;
        });

        connection.On<VehicleInfo>(ProviderMethods.VehicleChanged, car =>
        {
            _car = car;
            Logger.LogInformation("Gleanvolt's car changed to {Car}; publishing a reading for it.", car.Id);
            _ = Task.Run(() => PublishAsync(null, CancellationToken.None));
            return Task.CompletedTask;
        });

        connection.On<ChargingSessionInfo>(ProviderMethods.ChargingSessionChanged, session =>
        {
            SetSession(session);
            return Task.CompletedTask;
        });
    }

    // Gleanvolt says the state again whenever a provider attaches, so an unchanged one is not news. The
    // welcome and that repeat arrive on different threads, often at once: the check and the set are one
    // step, or both would see the old state and raise the change twice.
    private void SetSession(ChargingSessionInfo? session)
    {
        lock (_sessionGate)
        {
            if (session == _session)
            {
                return;
            }

            _session = session;
        }

        if (session is { Open: true })
        {
            Logger.LogInformation("Gleanvolt has a charging session open ({Mode}).", session.Mode);
        }
        else if (session is not null)
        {
            Logger.LogInformation("Gleanvolt has no charging session open.");
        }

        try
        {
            SessionChanged?.Invoke(session);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "A handler of the charging session failed.");
        }
    }

    protected override Task OnWelcomedAsync(VehicleWelcome welcome, CancellationToken cancellationToken) =>
        PublishAsync(null, cancellationToken);

    private async Task<PublishResult?> PublishAsync(RequestId? inReplyTo, CancellationToken cancellationToken)
    {
        if (_car is not { } car || !IsAttached)
        {
            return null;
        }

        try
        {
            if (await _source.GetReadingAsync(car, cancellationToken).ConfigureAwait(false) is not { } reading)
            {
                Logger.LogDebug("The source had no reading to publish.");
                return null;
            }

            var result = await Connection.InvokeAsync<PublishResult>(
                ProviderMethods.Publish, reading with { InReplyTo = inReplyTo }, cancellationToken).ConfigureAwait(false);

            if (result.Accepted)
            {
                Logger.LogInformation("Published the car's reading captured {CapturedAt:O}: SOC {Soc}%.", reading.CapturedAt, reading.SocPercent);
            }
            else
            {
                Logger.LogWarning("Gleanvolt refused the reading: {Problems}", string.Join(" ", result.Problems));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not publish the car's reading ({Reason}).", ex.Message);
            await ReportStatusAsync(new ProviderStatus(false, ex.Message), CancellationToken.None).ConfigureAwait(false);
            return null;
        }
    }
}

/// <summary>Registration.</summary>
public static class VehicleProviderServiceCollectionExtensions
{
    /// <summary>
    /// Runs a car provider in this host: <typeparamref name="TSource"/> supplies the readings, and the
    /// connection to Gleanvolt runs as a hosted service. Resolve <see cref="VehicleProviderConnection"/>
    /// to publish unprompted.
    /// </summary>
    public static IServiceCollection AddGleanvoltVehicleProvider<TSource>(this IServiceCollection services, Action<ProviderOptions> configure)
        where TSource : class, IVehicleSource
    {
        services.Configure(configure);
        services.AddSingleton<IVehicleSource, TSource>();
        services.AddSingleton<VehicleProviderConnection>();
        services.AddHostedService(provider => provider.GetRequiredService<VehicleProviderConnection>());
        return services;
    }
}
