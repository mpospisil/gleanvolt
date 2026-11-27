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
public sealed partial class ProviderSessions
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<string, IClientProxy> _client;
    private ExternalProviderStatus _status;
    private HubCallerContext? _context;
    private int _contractMinor;
    private PendingRequest? _pending;
    private bool _pageOnThisHost;
    private readonly IRememberedProviders? _remembered;

    internal ProviderSessions(
        ProviderKind kind,
        bool hasToken,
        TimeProvider time,
        ILogger logger,
        Func<string, IClientProxy> client,
        IRememberedProviders? remembered = null)
    {
        Kind = kind;
        _remembered = kind.RemembersProvider ? remembered ?? new InMemoryRememberedProviders() : null;
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
                return _remembered is null ? _status : _status with { RememberedProviderId = _remembered.Find(Kind.Id) };
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
    /// The contract minor the attached provider said hello with, or null with none attached. A message
    /// added in a later minor is sent only to a provider that knows it (#414).
    /// </summary>
    public int? ContractMinor
    {
        get
        {
            lock (_gate)
            {
                return _status.State == ExternalProviderState.Connected && _context is not null ? _contractMinor : null;
            }
        }
    }

    /// <summary>
    /// Attaches <paramref name="context"/> as the session, or says why not: null when attached, else a
    /// refusal code. Replaces the same provider's older connection, which is told goodbye and closed.
    /// </summary>
    internal (string? Code, string? Reason, Phrase? Phrase) Attach(HubCallerContext context, ProviderHello hello, bool pageOnThisHost = false)
    {
        HubCallerContext? replaced = null;

        bool remember;

        lock (_gate)
        {
            var remembered = _remembered?.Find(Kind.Id);

            if (remembered is not null && !string.Equals(remembered, hello.ProviderId, StringComparison.Ordinal))
            {
                return (RefusalCodes.AnotherProvider,
                    $"Gleanvolt remembers the plugin {remembered} for this kind and lets in no other until the owner presses "
                    + "Forget on Settings → Plugins.",
                    Phrase.Of("Provider.Remembered", new PhraseArg.Text(remembered)));
            }

            remember = _remembered is not null && remembered is null;

            if (_status.State == ExternalProviderState.Connected && _context is { } current && current.ConnectionId != context.ConnectionId)
            {
                if (!string.Equals(_status.ProviderId, hello.ProviderId, StringComparison.Ordinal))
                {
                    return (RefusalCodes.AnotherProvider,
                        $"Another provider, {_status.ProviderName} ({_status.ProviderId}), is connected for this kind. Only one is active at a time.",
                        Phrase.Of("Provider.AnotherConnected", new PhraseArg.Text(_status.ProviderName ?? string.Empty),
                            new PhraseArg.Text(_status.ProviderId ?? string.Empty)));
                }

                replaced = current;
            }

            _context = context;
            _contractMinor = hello.ContractMinor;
            _pending = null;
            _pageOnThisHost = pageOnThisHost && hello.PageUrl is not null;
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
                StatusCode = null,
                StatusMessages = null,
                Unanswered = false,
                PageUrl = hello.PageUrl,
                LastRefusal = null,
            };
        }

        if (remember)
        {
            try
            {
                _remembered!.Remember(Kind.Id, hello.ProviderId);
                _logger.LogInformation(
                    "The {Kind} provider {Provider} is remembered: no other is let in until the owner forgets it.", Kind.Name, hello.ProviderId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Still remembered in memory: the store says so, and it holds until the next start.
                _logger.LogWarning("Could not keep the {Kind} provider {Provider} for the next start ({Reason}).", Kind.Name, hello.ProviderId, ex.Message);
            }
        }

        if (replaced is not null)
        {
            _logger.LogInformation("The {Kind} provider {Provider} reconnected; its older connection is closed.", Kind.Name, hello.ProviderId);
            _ = SayGoodbyeAsync(replaced, "replaced", "A newer connection of the same provider replaced this one.");
        }

        _logger.LogInformation("The {Kind} provider {Provider} ({Name} {Version}) is connected.", Kind.Name, hello.ProviderId, hello.Name, hello.Version);
        Changed?.Invoke();
        return (null, null, null);
    }

    /// <summary>A hello was refused: kept in the status for the owner, and logged.</summary>
    internal void Refused(ProviderHello hello, string code, string reason, IReadOnlyList<Phrase?>? phrases = null)
    {
        lock (_gate)
        {
            _status = _status with
            {
                LastRefusal = new ExternalProviderRefusal(hello.ProviderId, hello.Name, code, reason, phrases, _time.GetUtcNow()),
            };
        }

        _logger.LogWarning("The {Kind} provider {Provider} was refused ({Code}): {Reason}", Kind.Name, hello.ProviderId, code, reason);
        Changed?.Invoke();
    }

    /// <summary>
    /// Forgets the remembered provider (#440) and closes its session if it is attached, so the next provider to say
    /// hello is remembered instead. Nothing for a kind that remembers none.
    /// </summary>
    public async Task ForgetAsync()
    {
        if (_remembered is null)
        {
            return;
        }

        var forgotten = _remembered.Find(Kind.Id);

        try
        {
            _remembered.Forget(Kind.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Forgotten in memory all the same; only the next start may remember it again.
            _logger.LogWarning("Could not forget the {Kind} provider for the next start too ({Reason}).", Kind.Name, ex.Message);
        }

        _logger.LogInformation("The owner forgot the {Kind} provider {Provider}; the next to say hello is remembered.", Kind.Name, forgotten);
        Changed?.Invoke();

        await CloseAsync("forgotten", "The owner forgot this provider; the next provider to connect is remembered.").ConfigureAwait(false);
    }

    /// <summary>
    /// Where the browser opens the attached provider's page (#426). A provider on Gleanvolt's own machine
    /// that gave a loopback page (<c>http://localhost:8096/</c>, a plugin container on the host's network)
    /// is opened at <paramref name="viewerHost"/>, the name the browser reached Gleanvolt at, on the
    /// page's own port: <c>localhost</c> would be the viewer's machine, and another name than Gleanvolt's
    /// makes the page a third party in the frame. Any other page as the provider gave it.
    /// </summary>
    internal Uri? PageAddress(string? viewerHost)
    {
        lock (_gate)
        {
            if (_status.PageUrl is not { } page)
            {
                return null;
            }

            return _pageOnThisHost && !string.IsNullOrEmpty(viewerHost)
                ? new UriBuilder(page) { Host = viewerHost }.Uri
                : page;
        }
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
            _status = _status with
            {
                Healthy = status.Healthy,
                StatusMessage = Clip(status.Message),
                NeedsOwner = !status.Healthy && status.NeedsOwner,
                StatusCode = status.Code is { Length: > 0 and <= 64 } code ? code : null,
                StatusMessages = Languages(status.Messages),
            };
        }

        Changed?.Invoke();
    }

    /// <summary>The most languages a status keeps, and the longest text in each (#415).</summary>
    internal const int MaxStatusLanguages = 16;

    internal const int MaxStatusLength = 1000;

    private static string? Clip(string? text) => text is { Length: > MaxStatusLength } ? text[..MaxStatusLength] : text;

    // A status is shown on pages and sent to Home Assistant: keep it to what reads as a language tag and
    // a sentence. A tag that isn't one, or an empty text, is dropped rather than refusing the status.
    private static IReadOnlyDictionary<string, string>? Languages(IReadOnlyDictionary<string, string>? messages)
    {
        if (messages is null)
        {
            return null;
        }

        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tag, text) in messages)
        {
            if (kept.Count < MaxStatusLanguages && LanguageTag().IsMatch(tag ?? string.Empty) && !string.IsNullOrWhiteSpace(text))
            {
                kept.TryAdd(tag!, Clip(text)!);
            }
        }

        return kept.Count > 0 ? kept : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial System.Text.RegularExpressions.Regex LanguageTag();

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
        bool ownerTold;

        lock (_gate)
        {
            if (_pending != request)
            {
                return;
            }

            _pending = null;
            _status = _status with { Unanswered = true };

            // The provider already said only the owner can fix it (a charger plugin with no address, an
            // expired login): it can't answer until they do, and a warning per request would repeat that.
            ownerTold = _status.NeedsOwner;
        }

        request.Timer?.Dispose();
        _logger.Log(ownerTold ? LogLevel.Debug : LogLevel.Warning,
            "The {Kind} provider did not answer the request {Request}.", Kind.Name, request.Id);
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
