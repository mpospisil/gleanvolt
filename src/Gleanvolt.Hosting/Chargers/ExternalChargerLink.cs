using System.Collections.Concurrent;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;
using Gleanvolt.Providers.Server;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Hosting.Chargers;

/// <summary>
/// Gleanvolt's side of a charger plugin (#435): what <see cref="ExternalChargerDriver"/> asks, and what
/// <see cref="ChargerHub"/> answers. A read sends <see cref="IChargerClient.RefreshRequested"/> through
/// <c>IHubContext</c> and waits, on the host's side, for a publish quoting it; a command is sent the same way
/// and waits for <see cref="IChargerHub.CommandDone"/>. The hub itself never waits.
///
/// <para>Every failure throws: the driver contract reports a charger it could not read as unknown, and a
/// command that did not happen as one that failed. Nothing here guesses.</para>
/// </summary>
public sealed class ExternalChargerLink
{
    private readonly IServiceProvider _services;
    private readonly ExternalChargerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ExternalChargerLink> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ChargerReading>> _reads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ChargerCommandResult>> _commands = new(StringComparer.Ordinal);
    private volatile Held? _held;

    public ExternalChargerLink(
        IServiceProvider services,
        IOptions<ExternalChargerOptions> options,
        TimeProvider time,
        ILogger<ExternalChargerLink> logger)
    {
        _services = services;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>The newest accepted reading, or null. Stale as soon as a command is sent.</summary>
    public ChargerReading? Current => _held?.Reading;

    /// <summary>The charger's own ceiling from the newest reading that said one, or null.</summary>
    public int? DeviceMaxAmps { get; private set; }

    /// <summary>Whether a charger plugin is attached right now.</summary>
    public bool IsAttached => Sessions?.ConnectionId is not null;

    /// <summary>The attached plugin's name, or null.</summary>
    public string? ProviderName => Sessions?.Status.ProviderName;

    // Resolved here rather than injected: the registry is built from the kinds, and this kind's switch reads
    // the installation, which the registry's construction must not wait on.
    private ProviderSessions? Sessions => _services.GetRequiredService<ExternalProviderRegistry>().For(ChargerContract.Kind);

    /// <summary>
    /// A reading of the charger: the held one if it arrived within <see cref="ExternalChargerOptions.ReadingMaxAge"/>
    /// and no command was sent since, otherwise a fresh one asked for. Throws when no plugin is attached, it does
    /// not answer in time, or its answer was refused.
    /// </summary>
    public async Task<ChargerReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_held is { Stale: false } held && _time.GetUtcNow() - held.ReceivedAt <= _options.ReadingMaxAge)
        {
            return held.Reading;
        }

        var timeout = _options.ReadTimeout;
        var request = Sessions?.BeginRequest(timeout)
            ?? throw new InvalidOperationException("No charger plugin is connected to Gleanvolt.");

        var answer = new TaskCompletionSource<ChargerReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reads[request.Id.Value] = answer;

        try
        {
            await Client(request.ConnectionId)
                .RefreshRequested(new RefreshRequest(request.Id, _time.GetUtcNow()))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return await answer.Task.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The charger plugin did not answer a read within {timeout.TotalSeconds:0.#} s.");
        }
        finally
        {
            _reads.TryRemove(request.Id.Value, out _);
        }
    }

    /// <summary>Sets the current through the plugin. Throws unless it says it did.</summary>
    public Task SetCurrentAsync(int amps, string reason, CancellationToken cancellationToken = default) =>
        CommandAsync(
            (client, id) => client.SetCurrent(new ChargerCurrentCommand(id, amps, reason)),
            $"set {amps} A",
            cancellationToken);

    /// <summary>Allows or stops charging through the plugin. Throws unless it says it did.</summary>
    public Task SetChargingAllowedAsync(bool allowed, string reason, CancellationToken cancellationToken = default) =>
        CommandAsync(
            (client, id) => client.SetChargingAllowed(new ChargerAllowedCommand(id, allowed, reason)),
            allowed ? "allow charging" : "stop charging",
            cancellationToken);

    private async Task CommandAsync(Func<IChargerClient, RequestId, Task> send, string what, CancellationToken cancellationToken)
    {
        var connection = Sessions?.ConnectionId
            ?? throw new InvalidOperationException($"Could not {what}: no charger plugin is connected to Gleanvolt.");

        // Whatever happens next, the held reading no longer says what the charger has.
        if (_held is { } held)
        {
            _held = held with { Stale = true };
        }

        var id = RequestId.New();
        var answer = new TaskCompletionSource<ChargerCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands[id.Value] = answer;
        var timeout = _options.CommandTimeout;

        try
        {
            await send(Client(connection), id).WaitAsync(cancellationToken).ConfigureAwait(false);
            var result = await answer.Task.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"The charger plugin could not {what}: {(string.IsNullOrWhiteSpace(result.Error) ? "it gave no reason" : result.Error)}.");
            }
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The charger plugin did not say whether it could {what} within {timeout.TotalSeconds:0.#} s.");
        }
        finally
        {
            _commands.TryRemove(id.Value, out _);
        }
    }

    /// <summary>The hub accepted <paramref name="reading"/>; a read waiting on its request has its answer.</summary>
    internal void Accepted(ChargerReading reading)
    {
        _held = new Held(reading, _time.GetUtcNow(), Stale: false);

        if (reading.MaxCurrentAmps is { } ceiling)
        {
            DeviceMaxAmps = ceiling;
        }

        if (reading.InReplyTo is { } id && _reads.TryGetValue(id.Value, out var answer))
        {
            answer.TrySetResult(reading);
        }
    }

    /// <summary>The hub refused a reading; a read waiting on <paramref name="inReplyTo"/> fails with the problems.</summary>
    internal void Refused(RequestId? inReplyTo, IReadOnlyList<string> problems)
    {
        if (inReplyTo is not null && _reads.TryGetValue(inReplyTo.Value, out var answer))
        {
            answer.TrySetException(new InvalidOperationException(
                "Gleanvolt refused the charger plugin's reading: " + string.Join(" ", problems)));
        }
    }

    /// <summary>The plugin said how a command ended. One nobody waits for any more is logged and dropped.</summary>
    internal void CommandDone(ChargerCommandResult result)
    {
        if (result.InReplyTo is { } id && _commands.TryGetValue(id.Value, out var answer))
        {
            answer.TrySetResult(result);
        }
        else
        {
            _logger.LogInformation(
                "The charger plugin answered a command Gleanvolt no longer waits for ({Outcome}).",
                result.Succeeded ? "done" : result.Error);
        }
    }

    private IChargerClient Client(string connectionId) =>
        _services.GetRequiredService<IHubContext<ChargerHub, IChargerClient>>().Clients.Client(connectionId);

    private sealed record Held(ChargerReading Reading, DateTimeOffset ReceivedAt, bool Stale);
}
