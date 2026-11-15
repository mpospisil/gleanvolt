using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Providers.Server;

/// <summary>
/// One kind's session: at most one provider attached at a time (#386). The same provider reconnecting
/// replaces its old session, since a reconnect arrives on a new connection before the old one has timed
/// out; a different provider is refused while one is attached. A disconnect from a connection that has
/// already been replaced changes nothing.
///
/// <para>Also tracks the host's requests: one outstanding at a time, marked unanswered when the
/// provider has not quoted it within the timeout.</para>
/// </summary>
public sealed class ProviderSessions
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<string, IClientProxy> _client;
    private ExternalProviderStatus _status;
    private HubCallerContext? _context;
    private PendingRequest? _pending;

    internal ProviderSessions(ProviderKind kind, bool hasToken, TimeProvider time, ILogger logger, Func<string, IClientProxy> client)
    {
        Kind = kind;
        _time = time;
        _logger = logger;
        _client = client;
        _status = new ExternalProviderStatus(kind.Id, kind.Name, kind.HubPath, hasToken, ExternalProviderState.Waiting);
    }

    /// <summary>The kind.</summary>
    public ProviderKind Kind { get; }

    /// <summary>The session as it stands. The <see cref="ExternalProviderState.Off"/> state is the registry's to add.</summary>
    public ExternalProviderStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>The attached connection, or null.</summary>
    public string? ConnectionId
    {
        get
        {
            lock (_gate)
            {
                return _status.State == ExternalProviderState.Connected ? _context?.ConnectionId : null;
            }
        }
    }

    /// <summary>Raised after every change.</summary>
    public event Action? Changed;

    /// <summary>Whether <paramref name="connectionId"/> is the attached session.</summary>
    public bool IsCurrent(string connectionId) => ConnectionId == connectionId;

    /// <summary>
    /// Attaches <paramref name="context"/> as the session, or says why not: null when attached, else a
    /// refusal code. Replaces the same provider's older connection, which is told goodbye and closed.
    /// </summary>
    internal (string? Code, string? Reason) Attach(HubCallerContext context, ProviderHello hello)
    {
        HubCallerContext? replaced = null;

        lock (_gate)
        {
            if (_status.State == ExternalProviderState.Connected && _context is { } current && current.ConnectionId != context.ConnectionId)
            {
                if (!string.Equals(_status.ProviderId, hello.ProviderId, StringComparison.Ordinal))
                {
                    return (RefusalCodes.AnotherProvider,
                        $"Another provider, {_status.ProviderName} ({_status.ProviderId}), is connected for this kind. Only one is active at a time.");
                }

                replaced = current;
            }

            _context = context;
            _pending = null;
            _status = _status with
            {
                State = ExternalProviderState.Connected,
                ProviderId = hello.ProviderId,
                ProviderName = hello.Name,
                ProviderVersion = hello.Version,
                Since = _time.GetUtcNow(),
                Healthy = null,
                StatusMessage = null,
                NeedsOwner = false,
                Unanswered = false,
                PageUrl = hello.PageUrl,
            };
        }

        if (replaced is not null)
        {
            _logger.LogInformation("The {Kind} provider {Provider} reconnected; its older connection is closed.", Kind.Name, hello.ProviderId);
            _ = SayGoodbyeAsync(replaced, "replaced", "A newer connection of the same provider replaced this one.");
        }

        _logger.LogInformation("The {Kind} provider {Provider} ({Name} {Version}) is connected.", Kind.Name, hello.ProviderId, hello.Name, hello.Version);
        Changed?.Invoke();
        return (null, null);
    }

    /// <summary>The connection is gone. Nothing changes unless it is the attached one.</summary>
    internal void Detach(string connectionId)
    {
        lock (_gate)
        {
            if (_status.State != ExternalProviderState.Connected || _context?.ConnectionId != connectionId)
            {
                return;
            }

            _context = null;
            _pending = null;
            _status = _status with { State = ExternalProviderState.Disconnected, Since = _time.GetUtcNow() };
        }

        _logger.LogWarning("The {Kind} provider {Provider} disconnected; its last data is kept.", Kind.Name, _status.ProviderId);
        Changed?.Invoke();
    }

    /// <summary>Closes the attached session, telling the provider why. Does nothing without one.</summary>
    public async Task CloseAsync(string code, string reason)
    {
        HubCallerContext? context;

        lock (_gate)
        {
            context = _status.State == ExternalProviderState.Connected ? _context : null;
        }

        if (context is null)
        {
            return;
        }

        _logger.LogInformation("Closing the {Kind} provider's session: {Reason}", Kind.Name, reason);
        await SayGoodbyeAsync(context, code, reason).ConfigureAwait(false);
        Detach(context.ConnectionId);
    }

    /// <summary>For tests: the attached connection drops, as a network would drop it, without a goodbye.</summary>
    internal void DropConnection()
    {
        lock (_gate)
        {
            _context?.Abort();
        }
    }

    /// <summary>Data from the attached session was stored.</summary>
    public void DataAccepted(RequestId? inReplyTo)
    {
        lock (_gate)
        {
            // An answer, even a late one, is a provider that answers.
            if (inReplyTo is not null && _pending?.Id == inReplyTo)
            {
                _pending = null;
            }

            _status = _status with
            {
                LastDataAt = _time.GetUtcNow(),
                LastProblem = null,
                LastProblemPhrases = null,
                Unanswered = inReplyTo is null && _status.Unanswered,
            };
        }

        Changed?.Invoke();
    }

    /// <summary>Data from the attached session was refused for <paramref name="problems"/>.</summary>
    public void DataRefused(RequestId? inReplyTo, IReadOnlyList<string> problems, IReadOnlyList<Phrase?> phrases)
    {
        lock (_gate)
        {
            if (inReplyTo is not null && _pending?.Id == inReplyTo)
            {
                _pending = null;
                _status = _status with { Unanswered = false };
            }

            _status = _status with { LastProblem = problems, LastProblemPhrases = phrases };
        }

        _logger.LogWarning("The {Kind} provider's data was refused: {Problems}", Kind.Name, string.Join(" ", problems));
        Changed?.Invoke();
    }

    /// <summary>What the provider said about itself.</summary>
    internal void Reported(ProviderStatus status)
    {
        lock (_gate)
        {
            // Only an unhealthy provider can need its owner: "healthy, and needs you" is read as healthy.
            _status = _status with { Healthy = status.Healthy, StatusMessage = status.Message, NeedsOwner = !status.Healthy && status.NeedsOwner };
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Starts a request to the attached session: its id and connection, or null when none is attached.
    /// It is marked unanswered when no data quoting it arrives within <paramref name="timeout"/>.
    /// </summary>
    public (RequestId Id, string ConnectionId)? BeginRequest(TimeSpan timeout)
    {
        PendingRequest request;

        lock (_gate)
        {
            if (_status.State != ExternalProviderState.Connected || _context is null)
            {
                return null;
            }

            request = new PendingRequest(RequestId.New(), _context.ConnectionId);
            _pending = request;
        }

        request.Timer = _time.CreateTimer(_ => TimedOut(request), null, timeout, Timeout.InfiniteTimeSpan);
        return (request.Id, request.ConnectionId);
    }

    /// <summary>The token changed: whether one exists now.</summary>
    internal void TokenChanged(bool hasToken)
    {
        lock (_gate)
        {
            _status = _status with { HasToken = hasToken };
        }

        Changed?.Invoke();
    }

    private void TimedOut(PendingRequest request)
    {
        lock (_gate)
        {
            if (_pending != request)
            {
                return;
            }

            _pending = null;
            _status = _status with { Unanswered = true };
        }

        request.Timer?.Dispose();
        _logger.LogWarning("The {Kind} provider did not answer the request {Request}.", Kind.Name, request.Id);
        Changed?.Invoke();
    }

    private async Task SayGoodbyeAsync(HubCallerContext context, string code, string reason)
    {
        try
        {
            await _client(context.ConnectionId).SendAsync(ProviderMethods.Goodbye, code, reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not say goodbye to {Connection} ({Reason}).", context.ConnectionId, ex.Message);
        }
        finally
        {
            context.Abort();
        }
    }

    private sealed class PendingRequest(RequestId id, string connectionId)
    {
        public RequestId Id { get; } = id;

        public string ConnectionId { get; } = connectionId;

        public ITimer? Timer { get; set; }
    }
}
