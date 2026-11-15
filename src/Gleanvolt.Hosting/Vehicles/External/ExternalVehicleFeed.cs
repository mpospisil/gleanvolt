using System.Collections.Concurrent;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;
using Gleanvolt.Providers.Server;
using Microsoft.AspNetCore.SignalR;

namespace Gleanvolt.Hosting.Vehicles.External;

/// <summary>
/// An external provider as the car's feed (#408): one more <see cref="IVehicleUpdateService"/> in
/// <see cref="ConfiguredVehicleFeed"/>, so the Overview's card, the feed band, Health, <i>Ask the car</i>
/// and the Home Assistant <b>Car feed</b> entity treat it as they treat volkswagen.de.
///
/// <para><b>It pushes.</b> The provider publishes on <see cref="VehicleHub"/> whenever the car has
/// something new, and the hub writes to the holder. Nothing polls it (<see cref="IsPushed"/>):
/// <see cref="FetchAsync"/> runs only when somebody asks the car or a plan wants a fresh reading. It
/// sends <see cref="IVehicleClient.RefreshRequested"/> through <c>IHubContext</c> and waits here, on
/// the host's side, for a publish quoting the request; the hub itself never waits.</para>
///
/// <para>Advisory, like every feed: a provider that disconnects only lets the reading age, and nothing
/// falls back to another feed.</para>
/// </summary>
public sealed class ExternalVehicleFeed : IVehicleUpdateService
{
    /// <summary>How long <see cref="FetchAsync"/> waits for the provider to answer.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _services;
    private readonly VehicleStateHolder _holder;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VehicleState?>> _waiting = new(StringComparer.Ordinal);

    // What the last ask ended in, when it ended badly; cleared by the next accepted reading.
    private volatile VehicleSourceHealth? _askFailed;

    public ExternalVehicleFeed(
        IServiceProvider services,
        VehicleStateHolder holder,
        string vehicleId,
        TimeProvider time,
        ILogger<ExternalVehicleFeed> logger)
    {
        _services = services;
        _holder = holder;
        _time = time;
        _logger = logger;
        VehicleId = string.IsNullOrWhiteSpace(vehicleId) ? "the car" : vehicleId.Trim();
    }

    /// <summary>The <see cref="IVehicleUpdateService.Manufacturer"/> of every external provider.</summary>
    public const string Id = "external";

    public string VehicleId { get; }

    public string Manufacturer => Id;

    /// <summary>The provider's name from its hello, or a plain description before one has said hello.</summary>
    public string DisplayName => Session?.ProviderName ?? Session?.ProviderId ?? "External provider";

    public bool IsPushed => true;

    public string? OwnerAction => Health.IsBlocked
        ? $"{DisplayName} needs you: {Session?.StatusMessage ?? "see the provider"}"
        : null;

    public Phrase? OwnerActionPhrase => OwnerAction is null
        ? null
        : Phrase.Of("ExternalVehicle.NeedsOwner", new PhraseArg.Text(DisplayName), new PhraseArg.Text(Session?.StatusMessage ?? string.Empty));

    public VehicleSourceHealth Health
    {
        get
        {
            var session = Session;

            if (session is null || session.State is ExternalProviderState.Off or ExternalProviderState.Waiting)
            {
                return VehicleSourceHealth.Degraded(
                    "waiting for the external car provider to connect",
                    Phrase.Of("ExternalVehicle.Waiting"));
            }

            if (session.State == ExternalProviderState.Disconnected)
            {
                return _holder.GetCurrentState() is { } held
                    ? VehicleSourceHealth.Degraded(
                        $"external car provider disconnected, reading from {held.CapturedAt:u}",
                        Phrase.Of("ExternalVehicle.Disconnected", new PhraseArg.Date(held.CapturedAt), new PhraseArg.Clock(held.CapturedAt)))
                    : VehicleSourceHealth.Degraded(
                        "external car provider disconnected, with no reading yet",
                        Phrase.Of("ExternalVehicle.DisconnectedNoReading"));
            }

            var name = DisplayName;

            if (session.Healthy == false)
            {
                var message = session.StatusMessage ?? "no reason given";
                return session.NeedsOwner
                    ? VehicleSourceHealth.NeedsOwner($"{name} needs you: {message}",
                        Phrase.Of("ExternalVehicle.ProviderNeedsOwner", new PhraseArg.Text(name), new PhraseArg.Text(message)))
                    : VehicleSourceHealth.Degraded($"{name} cannot read the car: {message}",
                        Phrase.Of("ExternalVehicle.ProviderUnhealthy", new PhraseArg.Text(name), new PhraseArg.Text(message)));
            }

            if (_askFailed is { } failed)
            {
                return failed;
            }

            if (session.LastProblem is { Count: > 0 } problems)
            {
                return VehicleSourceHealth.Degraded(
                    $"{name}'s last reading was refused: {string.Join(" ", problems)}",
                    Phrase.Of("ExternalVehicle.Refused", new PhraseArg.Text(name)));
            }

            return session.LastDataAt is null
                ? VehicleSourceHealth.Degraded(
                    $"{name} is connected and has not sent a reading yet",
                    Phrase.Of("ExternalVehicle.NoReadingYet", new PhraseArg.Text(name)))
                : VehicleSourceHealth.Ok($"{name} is connected", Phrase.Of("ExternalVehicle.Connected", new PhraseArg.Text(name)));
        }
    }

    // Nothing polls this feed; the worker skips it on IsPushed. A negative delay is the contract's
    // "do not ask on a clock" for a host that does not know IsPushed.
    public TimeSpan NextDelay => System.Threading.Timeout.InfiniteTimeSpan;

    private ExternalProviderStatus? Session => _services.GetRequiredService<ExternalProviderRegistry>().Find(VehicleContract.Kind);

    /// <summary>
    /// Asks the provider for the car and waits up to <see cref="Timeout"/> for a reading quoting the
    /// request. Null when none is connected, it does not answer, or its answer is refused; the reason is
    /// in <see cref="Health"/> and the holder keeps its last reading.
    /// </summary>
    public async Task<VehicleState?> FetchAsync(CancellationToken cancellationToken)
    {
        // Resolved here rather than injected: the registry is built from the kinds, and this kind's
        // switch reads the feed this is.
        var sessions = _services.GetRequiredService<ExternalProviderRegistry>().For(VehicleContract.Kind);

        if (sessions?.BeginRequest(Timeout) is not { } request)
        {
            _logger.LogInformation("Asked the car, but no external car provider is connected.");
            return null;
        }

        var answer = new TaskCompletionSource<VehicleState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[request.Id.Value] = answer;

        try
        {
            await _services.GetRequiredService<IHubContext<VehicleHub, IVehicleClient>>()
                .Clients.Client(request.ConnectionId)
                .RefreshRequested(new RefreshRequest(request.Id, _time.GetUtcNow()))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return await answer.Task.WaitAsync(Timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The external car provider did not answer within {Timeout}.", Timeout);
            _askFailed = VehicleSourceHealth.Degraded(
                $"{DisplayName} didn't answer within {Timeout.TotalSeconds:0} seconds",
                Phrase.Of("ExternalVehicle.NoAnswer", new PhraseArg.Text(DisplayName), new PhraseArg.Duration(Timeout)));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A feed does not throw for a failed request: the holder keeps what it has.
            _logger.LogWarning("Could not ask the external car provider for a reading ({Reason}).", ex.Message);
            _askFailed = VehicleSourceHealth.Degraded(
                $"{DisplayName} could not be asked: {ex.Message}",
                Phrase.Of("ExternalVehicle.AskFailed", new PhraseArg.Text(DisplayName), new PhraseArg.Text(ex.Message)));
            return null;
        }
        finally
        {
            _waiting.TryRemove(request.Id.Value, out _);
        }
    }

    /// <summary>The hub stored <paramref name="state"/>; an ask waiting on <paramref name="inReplyTo"/> has its answer.</summary>
    internal void Accepted(RequestId? inReplyTo, VehicleState state)
    {
        _askFailed = null;

        if (inReplyTo is not null && _waiting.TryGetValue(inReplyTo.Value, out var answer))
        {
            answer.TrySetResult(state);
        }
    }

    /// <summary>The hub refused a reading; an ask waiting on <paramref name="inReplyTo"/> ends without one.</summary>
    internal void Refused(RequestId? inReplyTo)
    {
        if (inReplyTo is not null && _waiting.TryGetValue(inReplyTo.Value, out var answer))
        {
            answer.TrySetResult(null);
        }
    }
}
