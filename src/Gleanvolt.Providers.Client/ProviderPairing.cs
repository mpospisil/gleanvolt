using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Providers.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Client;

/// <summary>Where a provider's pairing stands.</summary>
public enum PairingPhase
{
    /// <summary>There is a token, configured or paired: nothing to ask for.</summary>
    NotNeeded,

    /// <summary>Looking for a Gleanvolt to ask.</summary>
    Searching,

    /// <summary>Asked; waiting for the owner to accept in Gleanvolt.</summary>
    Waiting,

    /// <summary>The owner rejected it; the provider asks again after a long wait or a restart.</summary>
    Rejected,
}

/// <summary>What a provider's page and log say about its pairing.</summary>
/// <param name="Phase">Where it stands.</param>
/// <param name="Code">
/// The six digits the owner compares with what Gleanvolt shows (e.g. <c>482 193</c>); empty when there is
/// nothing to pair.
/// </param>
/// <param name="Gleanvolt">The Gleanvolt asked, or the one in use once paired; null while searching.</param>
public sealed record PairingStatus(PairingPhase Phase, string Code, Uri? Gleanvolt);

/// <summary>
/// Gets a provider its token while it runs (#459): with no token it finds Gleanvolt, knocks with the hash of a
/// secret it keeps, shows the code derived from it, and polls until the owner answers in Gleanvolt. Accepted, the
/// token and the address that accepted it go to <see cref="ProviderCredentials"/>, and the connection starts with
/// them. A token in the configuration means it never knocks.
///
/// <para>It never stops the provider: no Gleanvolt, a Gleanvolt that restarts, or one that answers nonsense is a
/// search again later, said once at Information and then at Debug, so a plugin that waits for days is quiet.</para>
/// </summary>
public sealed class ProviderPairing : BackgroundService
{
    /// <summary>The store's name for the pairing secret, kept so the code stays the same across restarts.</summary>
    internal const string SecretFile = "gleanvolt-pairing-secret";

    /// <summary>How often a waiting provider asks whether the owner has answered.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>How long a rejected provider waits before it asks again.</summary>
    internal static readonly TimeSpan RejectedDelay = TimeSpan.FromHours(1);

    // Long enough for a Raspberry Pi under load, short enough that an address that swallows packets does not hold
    // up the next one for long.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // As Gleanvolt writes them: camelCase enums, the same as the hubs.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _gate = new();
    private readonly ProviderOptions _options;
    private readonly ProviderCredentials _credentials;
    private readonly IGleanvoltFinder _finder;
    private readonly IProviderSecretStore? _store;
    private readonly IHttpClientFactory? _clients;
    private readonly TimeProvider _time;
    private readonly ILogger<ProviderPairing> _logger;
    private readonly string _hubPath;
    private HttpMessageHandler? _ownHandler;
    private string? _secret;
    private string _code = string.Empty;
    private PairingStatus _status;
    private TaskCompletionSource _credentialsChanged = NewSignal();

    /// <param name="options">The provider's id, name, version and page, which the knock carries.</param>
    /// <param name="credentials">Where an accepted token goes, and whether one is needed at all.</param>
    /// <param name="finder">Where to look for Gleanvolt.</param>
    /// <param name="store">Keeps the pairing secret across restarts; null keeps it for this run only, so the code changes on restart.</param>
    /// <param name="clients">Makes the HTTP clients; null uses one of its own.</param>
    /// <param name="time">The clock the polls and waits run on.</param>
    /// <param name="logger">Where the code and the status are logged.</param>
    /// <param name="hubPath">The kind's hub path (<see cref="ProviderConnection{TWelcome}.HubPath"/>), which names the kind to Gleanvolt.</param>
    public ProviderPairing(ProviderOptions options, ProviderCredentials credentials, IGleanvoltFinder finder,
        IProviderSecretStore? store, IHttpClientFactory? clients, TimeProvider time, ILogger<ProviderPairing> logger,
        string hubPath)
    {
        _options = options;
        _credentials = credentials;
        _finder = finder;
        _store = store;
        _clients = clients;
        _time = time;
        _logger = logger;
        _hubPath = hubPath;

        // The secret is made only when there is something to pair, so a provider with a configured token never
        // writes one; but then at once, so the page shows the code before the first search ends.
        if (credentials.Token is null)
        {
            EnsureSecret();
        }

        _status = credentials.Token is null
            ? new PairingStatus(PairingPhase.Searching, _code, null)
            : new PairingStatus(PairingPhase.NotNeeded, string.Empty, credentials.Endpoint);
        credentials.Changed += OnCredentialsChanged;
    }

    /// <summary>Where the pairing stands now.</summary>
    public PairingStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Raised when <see cref="Status"/> changes, for a page showing it.</summary>
    public event Action? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = _options.InitialRetryDelay;
        var told = new Told();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Taken before the token is looked at, so a change between the two still wakes the wait below.
                var changed = NextCredentialsChange();
                if (_credentials.Token is not null)
                {
                    SetStatus(PairingPhase.NotNeeded, _credentials.Endpoint);
                    await changed.WaitAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var secret = EnsureSecret();
                // Quiet while Gleanvolt keeps forgetting the request: each knock goes back through searching.
                var level = told.Unknown ? LogLevel.Debug : LogLevel.Information;
                SetStatus(PairingPhase.Searching, null, level);

                var knock = await KnockAsync(secret, stoppingToken).ConfigureAwait(false);
                if (knock.Ticket is not { } ticket)
                {
                    if (knock.Refused is { } refused)
                    {
                        LogRefused(told, refused);
                    }
                    else
                    {
                        LogNotFound(told, lost: null);
                    }

                    await WaitAsync(delay, stoppingToken).ConfigureAwait(false);
                    delay = Longer(delay);
                    continue;
                }

                // The backoff and what was said are reset only by the owner's answer, not by a knock taken nor by a
                // poll that is still waiting: two copies of one plugin replace each other's request (same id, same
                // address), so each knock is taken, each waits a poll or two, and each then hears Unknown. Reset by
                // either, they would knock in turn every few seconds, filling the log and the source's knock budget.
                SetStatus(PairingPhase.Waiting, ticket.Gleanvolt, level);
                void Answered()
                {
                    told = new Told();
                    delay = _options.InitialRetryDelay;
                }

                var answer = await AwaitAnswerAsync(ticket, secret, stoppingToken).ConfigureAwait(false);
                switch (answer?.State)
                {
                    case null:
                        // The token came another way while waiting, or Gleanvolt went away: the top of the loop
                        // tells which, and a lost Gleanvolt is searched for again after the backoff.
                        if (_credentials.Token is null)
                        {
                            LogNotFound(told, ticket.Gleanvolt);
                            await WaitAsync(delay, stoppingToken).ConfigureAwait(false);
                            delay = Longer(delay);
                        }

                        break;

                    case PairingState.Accepted when !string.IsNullOrWhiteSpace(answer.Token):
                        Answered();
                        Paired(ticket.Gleanvolt, answer.Token);
                        break;

                    case PairingState.Rejected:
                        Answered();
                        SetStatus(PairingPhase.Rejected, ticket.Gleanvolt);
                        await WaitAsync(RejectedDelay, stoppingToken).ConfigureAwait(false);
                        break;

                    default:
                        // Unknown: Gleanvolt restarted, or the request expired, and its waiting list is in memory
                        // only; or another copy of this plugin replaced the request. An Accepted without a token is no
                        // answer either. Knock again after the backoff, said once at Information.
                        _logger.Log(told.Unknown ? LogLevel.Debug : LogLevel.Information,
                            "Gleanvolt at {Gleanvolt} no longer knows the pairing request; asking again in {Delay}.", ticket.Gleanvolt, delay);
                        told.Unknown = true;
                        await WaitAsync(delay, stoppingToken).ConfigureAwait(false);
                        delay = Longer(delay);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A finder or a store that throws is a search that failed, not a provider that stops.
                _logger.LogDebug(ex, "Pairing failed ({Reason}); trying again in {Delay}.", ex.Message, delay);
                try
                {
                    await WaitAsync(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                delay = Longer(delay);
            }
        }
    }

    public override void Dispose()
    {
        _credentials.Changed -= OnCredentialsChanged;
        _ownHandler?.Dispose();
        base.Dispose();
    }

    // The first Gleanvolt that takes the knock, and the request it answered with; no ticket when none did, and then
    // the first Gleanvolt that refused it as wrong (400), if one did.
    private async Task<KnockResult> KnockAsync(string secret, CancellationToken stoppingToken)
    {
        var knock = new PairingKnock(
            _hubPath,
            _options.ProviderId,
            _options.Name,
            _options.Version ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0",
            _options.PageUrl,
            PairingSecret.Hash(secret));
        Refusal? refused = null;

        await foreach (var candidate in _finder.CandidatesAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using var client = CreateClient();
                using var response = await client.PostAsJsonAsync(new Uri(candidate, PairingPaths.Knock), knock, Json, stoppingToken)
                    .ConfigureAwait(false);

                // An older Gleanvolt without pairing (404), one that does not serve this kind (400), or a source
                // over its rate limit (429): this one will not do, try the next.
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Gleanvolt at {Gleanvolt} did not take the pairing request ({Status}).", candidate, (int)response.StatusCode);
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        refused ??= new Refusal(candidate, (int)response.StatusCode);
                    }

                    continue;
                }

                if (await response.Content.ReadFromJsonAsync<PairingTicket>(Json, stoppingToken).ConfigureAwait(false) is { RequestId.Length: > 0 } ticket)
                {
                    return new KnockResult(new Ticket(candidate, ticket.RequestId), null);
                }
            }
            catch (Exception ex) when (IsFailure(ex, stoppingToken))
            {
                _logger.LogDebug("No Gleanvolt at {Gleanvolt} ({Reason}).", candidate, ex.Message);
            }
        }

        return new KnockResult(null, refused);
    }

    // The owner's answer; null when the token arrived another way, or the Gleanvolt asked could not be reached.
    private async Task<PairingAnswer?> AwaitAnswerAsync(Ticket ticket, string secret, CancellationToken stoppingToken)
    {
        var collect = new Uri(ticket.Gleanvolt, PairingPaths.Collect(Uri.EscapeDataString(ticket.RequestId)));

        while (true)
        {
            await WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false);
            if (_credentials.Token is not null)
            {
                return null;
            }

            try
            {
                using var client = CreateClient();
                using var response = await client.PostAsJsonAsync(collect, new PairingCollect(secret), Json, stoppingToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Gleanvolt at {Gleanvolt} did not answer the pairing poll ({Status}).", ticket.Gleanvolt, (int)response.StatusCode);
                    return null;
                }

                var answer = await response.Content.ReadFromJsonAsync<PairingAnswer>(Json, stoppingToken).ConfigureAwait(false);
                if (answer?.State is PairingState.Waiting)
                {
                    continue;
                }

                return answer ?? new PairingAnswer(PairingState.Unknown);
            }
            catch (Exception ex) when (IsFailure(ex, stoppingToken))
            {
                _logger.LogDebug("Could not ask Gleanvolt at {Gleanvolt} about the pairing ({Reason}).", ticket.Gleanvolt, ex.Message);
                return null;
            }
        }
    }

    private void Paired(Uri gleanvolt, string token)
    {
        if (!_credentials.Save(gleanvolt, token))
        {
            _logger.LogWarning("Paired with Gleanvolt at {Gleanvolt}, but could not keep the token: pair again after a restart.", gleanvolt);
        }
        else
        {
            _logger.LogInformation("Paired with Gleanvolt at {Gleanvolt}.", gleanvolt);
        }

        // Its job is done: a later pairing (the token refused, say) starts with a new secret and a new code.
        _store?.Delete(SecretFile);
        lock (_gate)
        {
            _secret = null;
            _code = string.Empty;
        }

        SetStatus(PairingPhase.NotNeeded, _credentials.Endpoint);
    }

    private string EnsureSecret()
    {
        lock (_gate)
        {
            if (_secret is not null)
            {
                return _secret;
            }

            var secret = _store?.Read(SecretFile)?.Trim();
            if (string.IsNullOrEmpty(secret))
            {
                secret = PairingSecret.Create();
                if (_store is not null && !_store.Write(SecretFile, secret))
                {
                    _logger.LogWarning("Could not keep the pairing secret: the pairing code will change when the provider restarts.");
                }
            }

            _secret = secret;
            _code = PairingSecret.Code(PairingSecret.Hash(secret));
            return secret;
        }
    }

    private void SetStatus(PairingPhase phase, Uri? gleanvolt, LogLevel level = LogLevel.Information)
    {
        PairingStatus status;
        lock (_gate)
        {
            status = new PairingStatus(phase, phase == PairingPhase.NotNeeded ? string.Empty : _code, gleanvolt);
            if (status == _status)
            {
                return;
            }

            _status = status;
        }

        switch (phase)
        {
            case PairingPhase.Searching:
                _logger.Log(level, "Not paired with Gleanvolt yet: looking for it to ask. The pairing code is {Code}.", status.Code);
                break;
            case PairingPhase.Waiting:
                _logger.Log(level, "Asked Gleanvolt at {Gleanvolt} to pair: accept it there, under Settings → Plugins. The pairing code is {Code}.",
                    gleanvolt, status.Code);
                break;
            case PairingPhase.Rejected:
                _logger.LogInformation("The owner rejected the pairing in Gleanvolt at {Gleanvolt}; asking again in {Delay}, or after a restart.",
                    gleanvolt, RejectedDelay);
                break;
        }

        // A subscriber is somebody else's code: one that throws must not stop the pairing, nor the others hearing.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception)
            {
                // The status changed whether or not it was shown.
            }
        }
    }

    // Information the first time, Debug after: a plugin installed before Gleanvolt may look for days.
    private void LogNotFound(Told told, Uri? lost)
    {
        var level = told.NotFound ? LogLevel.Debug : LogLevel.Information;
        told.NotFound = true;
        if (lost is null)
        {
            _logger.Log(level, "Found no Gleanvolt to pair with yet; still looking.");
        }
        else
        {
            _logger.Log(level, "Lost Gleanvolt at {Gleanvolt} while waiting for the owner; looking again.", lost);
        }
    }

    // A Gleanvolt that answers 400 found the knock wrong: it will refuse it every time, which is a plugin and a
    // Gleanvolt that do not agree, not a Gleanvolt that is missing. A Warning once, then Debug.
    private void LogRefused(Told told, Refusal refused)
    {
        var level = told.Refused ? LogLevel.Debug : LogLevel.Warning;
        told.Refused = true;
        _logger.Log(level, "Gleanvolt at {Gleanvolt} refused the pairing request ({Status}); is the plugin for a kind it serves? Still looking.",
            refused.Gleanvolt, refused.Status);
    }

    private Task NextCredentialsChange()
    {
        lock (_gate)
        {
            if (_credentialsChanged.Task.IsCompleted)
            {
                _credentialsChanged = NewSignal();
            }

            return _credentialsChanged.Task;
        }
    }

    private void OnCredentialsChanged()
    {
        lock (_gate)
        {
            _credentialsChanged.TrySetResult();
        }
    }

    private HttpClient CreateClient()
    {
        HttpClient client;
        if (_clients is not null)
        {
            client = _clients.CreateClient(nameof(ProviderPairing));
        }
        else
        {
            lock (_gate)
            {
                _ownHandler ??= new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
            }

            client = new HttpClient(_ownHandler, disposeHandler: false);
        }

        client.Timeout = RequestTimeout;
        return client;
    }

    private Task WaitAsync(TimeSpan delay, CancellationToken stoppingToken) => Task.Delay(delay, _time, stoppingToken);

    private TimeSpan Longer(TimeSpan delay) => TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));

    // A request that failed for its own reasons (refused, timed out, nonsense in reply), not because we are stopping.
    private static bool IsFailure(Exception ex, CancellationToken stoppingToken) =>
        ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Ticket(Uri Gleanvolt, string RequestId);

    private sealed record Refusal(Uri Gleanvolt, int Status);

    private sealed record KnockResult(Ticket? Ticket, Refusal? Refused);

    // What has been said at Information (or Warning) since the last real answer, so it is not said again.
    private sealed class Told
    {
        public bool NotFound { get; set; }

        public bool Unknown { get; set; }

        public bool Refused { get; set; }
    }
}

/// <summary>Registration of pairing, shared by every kind's <c>AddGleanvolt*Provider</c>.</summary>
internal static class ProviderPairingRegistration
{
    /// <summary>
    /// Runs <see cref="ProviderPairing"/> as a hosted service for the kind whose connection's hub path
    /// <paramref name="hubPath"/> reads, with the SDK's <see cref="GleanvoltFinder"/> unless another was registered.
    /// </summary>
    public static IServiceCollection AddGleanvoltPairing(this IServiceCollection services, Func<IServiceProvider, string> hubPath)
    {
        // One token per provider host, so one pairing: a second kind in the same host shares the first one's.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ProviderPairing)))
        {
            return services;
        }

        services.TryAddSingleton<IGleanvoltFinder, GleanvoltFinder>();
        services.AddSingleton(provider => new ProviderPairing(
            provider.GetRequiredService<IOptions<ProviderOptions>>().Value,
            provider.GetRequiredService<ProviderCredentials>(),
            provider.GetRequiredService<IGleanvoltFinder>(),
            provider.GetService<IProviderSecretStore>(),
            provider.GetService<IHttpClientFactory>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<ILogger<ProviderPairing>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderPairing>.Instance,
            hubPath(provider)));
        services.AddHostedService(provider => provider.GetRequiredService<ProviderPairing>());
        return services;
    }
}
