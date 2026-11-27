using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Client.Charger;

/// <summary>
/// What a charger plugin's author writes (#435): read the charger, and carry out Gleanvolt's two commands on it.
/// Gleanvolt keeps the safeguards (the threshold, the clamp, dry-run); this only maps onto the device.
/// </summary>
public interface IChargerSource
{
    /// <summary>
    /// What the charger can do: its current range and phases (charger 1.1, #440). Sent in every hello; Gleanvolt
    /// refuses the hello with <see cref="RefusalCodes.Limits"/> when its charge control asks for more, and never
    /// sends a setpoint above the maximum. Asked again at every reconnect, so a plugin that learns its model on its
    /// own page can answer differently once it knows.
    /// </summary>
    ChargerCapabilities Capabilities { get; }

    /// <summary>
    /// The charger now. Throw when it cannot be read: the SDK tells Gleanvolt nothing, and Gleanvolt reports a
    /// charger it could not read as unknown. <see cref="ChargerReading.InReplyTo"/> is filled in by the SDK.
    /// </summary>
    Task<ChargerReading> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Set the charging current. Below the charger's minimum (0, in practice) is a pause: a charger that takes
    /// nothing below 6 A pauses another way and remembers it, so the next setpoint of 6 A or more resumes, and
    /// readings say 0 A while it holds. Throw when the charger does not accept it.
    /// </summary>
    Task SetCurrentAsync(int amps, CancellationToken cancellationToken);

    /// <summary>
    /// Let the charger charge, or stop it, whatever it was doing. Never touches the setpoint; a start while paused
    /// stays paused. Throw when the charger does not accept it.
    /// </summary>
    Task SetChargingAllowedAsync(bool allowed, CancellationToken cancellationToken);
}

/// <summary>
/// The charger kind's connection (#435): publishes once after every accepted hello, answers every
/// <see cref="IChargerClient.RefreshRequested"/> with a reading, carries out every command and says how it
/// ended, and publishes whenever the provider calls <see cref="PublishNowAsync"/>. Commands are carried out
/// one at a time, in the order they came, so a stop cannot overtake the setpoint before it.
/// </summary>
public sealed class ChargerProviderConnection : ProviderConnection<ChargerWelcome>
{
    private readonly IChargerSource _source;
    private readonly SemaphoreSlim _device = new(1, 1);

    public ChargerProviderConnection(
        IOptions<ProviderOptions> options, IChargerSource source, ILogger<ChargerProviderConnection> logger,
        ProviderCredentials? credentials = null)
        : base(options.Value, ChargerContract.HubPath, ChargerContract.Major, ChargerContract.Minor, logger, credentials)
    {
        _source = source;
    }

    /// <summary>The charge control's limits from the welcome. Null before the first hello.</summary>
    public ChargerLimits? Limits => Welcome?.Limits;

    /// <summary>Reads the charger and publishes, unprompted. Null when no session is attached or the read failed.</summary>
    public Task<PublishResult?> PublishNowAsync(CancellationToken cancellationToken = default) => PublishAsync(null, cancellationToken);

    protected override void OnConfigure(HubConnection connection)
    {
        // Each answered from a task of its own: the receive loop is not held while the charger answers.
        connection.On<RefreshRequest>(ProviderMethods.RefreshRequested, request =>
        {
            _ = Task.Run(() => PublishAsync(request.Id, CancellationToken.None));
            return Task.CompletedTask;
        });

        connection.On<ChargerCurrentCommand>(ProviderMethods.SetCurrent, command =>
        {
            _ = Task.Run(() => CommandAsync(
                command.Id,
                $"set {command.Amps} A ({command.Reason})",
                token => _source.SetCurrentAsync(command.Amps, token)));
            return Task.CompletedTask;
        });

        connection.On<ChargerAllowedCommand>(ProviderMethods.SetChargingAllowed, command =>
        {
            _ = Task.Run(() => CommandAsync(
                command.Id,
                $"{(command.Allowed ? "allow" : "stop")} charging ({command.Reason})",
                token => _source.SetChargingAllowedAsync(command.Allowed, token)));
            return Task.CompletedTask;
        });
    }

    protected override ProviderHello OnHello(ProviderHello hello)
    {
        var capabilities = _source.Capabilities;

        if (capabilities.Check() is { Count: > 0 } problems)
        {
            // Sent anyway: Gleanvolt refuses it and says why, and its owner sees that on Settings.
            Logger.LogError("The charger's capabilities are not usable: {Problems}", string.Join(" ", problems));
        }

        return hello with { Charger = capabilities };
    }

    protected override Task OnWelcomedAsync(ChargerWelcome welcome, CancellationToken cancellationToken) =>
        PublishAsync(null, cancellationToken);

    private async Task CommandAsync(RequestId id, string what, Func<CancellationToken, Task> run)
    {
        ChargerCommandResult result;
        await _device.WaitAsync().ConfigureAwait(false);

        try
        {
            await run(CancellationToken.None).ConfigureAwait(false);
            Logger.LogInformation("Charger: {What}.", what);
            result = ChargerCommandResult.Done(id);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Charger: could not {What}: {Reason}", what, ex.Message);
            result = ChargerCommandResult.Failed(id, ex.Message);
        }
        finally
        {
            _device.Release();
        }

        try
        {
            if (IsAttached)
            {
                await Connection.InvokeAsync(ProviderMethods.CommandDone, result).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Could not tell Gleanvolt how a command ended ({Reason}).", ex.Message);
        }
    }

    private async Task<PublishResult?> PublishAsync(RequestId? inReplyTo, CancellationToken cancellationToken)
    {
        if (!IsAttached)
        {
            return null;
        }

        ChargerReading reading;
        await _device.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            reading = await _source.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No answer at all: Gleanvolt's read times out and the poll says unknown, which is the truth.
            Logger.LogWarning("Could not read the charger ({Reason}).", ex.Message);
            return null;
        }
        finally
        {
            _device.Release();
        }

        try
        {
            var result = await Connection.InvokeAsync<PublishResult>(
                ProviderMethods.Publish, reading with { InReplyTo = inReplyTo }, cancellationToken).ConfigureAwait(false);

            if (!result.Accepted)
            {
                Logger.LogWarning("Gleanvolt refused the charger's reading: {Problems}", string.Join(" ", result.Problems));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not publish the charger's reading ({Reason}).", ex.Message);
            return null;
        }
    }
}

/// <summary>Registration.</summary>
public static class ChargerProviderServiceCollectionExtensions
{
    /// <summary>
    /// Runs a charger plugin in this host: <typeparamref name="TSource"/> reads and commands the charger, and
    /// the connection to Gleanvolt runs as a hosted service. Resolve <see cref="ChargerProviderConnection"/> to
    /// publish unprompted, for example when the car is plugged in.
    /// </summary>
    public static IServiceCollection AddGleanvoltChargerProvider<TSource>(this IServiceCollection services, Action<ProviderOptions> configure)
        where TSource : class, IChargerSource
    {
        services.Configure(configure);
        services.TryAddSingleton<ProviderCredentials>();
        services.AddSingleton<IChargerSource, TSource>();
        services.AddSingleton<ChargerProviderConnection>();
        services.AddHostedService(provider => provider.GetRequiredService<ChargerProviderConnection>());
        services.AddGleanvoltPairing(provider => provider.GetRequiredService<ChargerProviderConnection>().HubPath);
        return services;
    }
}
