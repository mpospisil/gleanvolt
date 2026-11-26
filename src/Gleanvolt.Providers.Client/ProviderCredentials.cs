using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Client;

/// <summary>Where the token a provider connects with came from.</summary>
public enum CredentialSource
{
    /// <summary>There is no token yet: the provider waits for one.</summary>
    None,

    /// <summary>The provider's configuration (<see cref="ProviderOptions.Token"/>): a container set up from its environment.</summary>
    Configuration,

    /// <summary>Handed over by Gleanvolt when the owner accepted the pairing, and kept in the provider's secret store.</summary>
    Paired,
}

/// <summary>
/// The token and the address a provider connects to Gleanvolt with, which can arrive while the provider
/// runs: the owner accepts the pairing and <see cref="Save"/> is called. A configured token always wins over
/// a paired one, so setting the token by hand keeps meaning what it meant before pairing existed.
/// </summary>
public sealed class ProviderCredentials
{
    private const string TokenFile = "gleanvolt-token";
    private const string EndpointFile = "gleanvolt-endpoint";
    private static readonly Uri DefaultEndpoint = new("http://localhost:8090");

    private readonly Lock _gate = new();
    private readonly ProviderOptions _options;
    private readonly IProviderSecretStore? _store;
    private readonly string? _configuredToken;
    private string? _pairedToken;
    private Uri? _pairedEndpoint;
    private TaskCompletionSource _arrived = NewSignal();

    /// <param name="options">The provider's options: a configured token and endpoint.</param>
    /// <param name="store">Where a paired token is kept across restarts; null keeps it for this run only.</param>
    public ProviderCredentials(IOptions<ProviderOptions> options, IProviderSecretStore? store = null)
    {
        _options = options.Value;
        _store = store;
        _configuredToken = Clean(_options.Token);

        // A token is only worth having with the address it was accepted at: without it the provider would
        // knock on the default address for ever, and a connection error never forgets a token. So a token
        // whose endpoint is missing or garbled is dropped, files and all.
        var token = Clean(store?.Read(TokenFile));
        if (token is not null && Uri.TryCreate(Clean(store?.Read(EndpointFile)), UriKind.Absolute, out var endpoint))
        {
            _pairedToken = token;
            _pairedEndpoint = endpoint;
        }
        else if (store is not null && (token is not null || store.Read(EndpointFile) is not null))
        {
            store.Delete(TokenFile);
            store.Delete(EndpointFile);
        }

        if (Token is not null)
        {
            _arrived.TrySetResult();
        }
    }

    /// <summary>Gleanvolt's address: the paired one when paired, else the configured one, else <c>http://localhost:8090</c>.</summary>
    public Uri Endpoint
    {
        get
        {
            lock (_gate)
            {
                return Source == CredentialSource.Paired && _pairedEndpoint is not null
                    ? _pairedEndpoint
                    : _options.Endpoint ?? DefaultEndpoint;
            }
        }
    }

    /// <summary>The token in use, or null when there is none yet.</summary>
    public string? Token
    {
        get
        {
            lock (_gate)
            {
                return _configuredToken ?? _pairedToken;
            }
        }
    }

    /// <summary>Where <see cref="Token"/> came from.</summary>
    public CredentialSource Source
    {
        get
        {
            lock (_gate)
            {
                return _configuredToken is not null ? CredentialSource.Configuration
                    : _pairedToken is not null ? CredentialSource.Paired
                    : CredentialSource.None;
            }
        }
    }

    /// <summary>Raised when the token or the address in use changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Makes the paired token and address the ones in use and keeps them. False when they could not be kept
    /// (no store, or it would not write): they are in use either way, until the provider restarts.
    /// </summary>
    public bool Save(Uri endpoint, string token)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        bool written;

        lock (_gate)
        {
            _pairedToken = token.Trim();
            _pairedEndpoint = endpoint;
            // The endpoint first, the token last: a crash or a failed write between them leaves an endpoint
            // alone, which loads as nothing, never a token without its address.
            written = _store is not null && _store.Write(EndpointFile, endpoint.AbsoluteUri);
            if (written)
            {
                written = _store!.Write(TokenFile, _pairedToken);
            }
            else
            {
                _store?.Delete(TokenFile);
            }
            _arrived.TrySetResult();
        }

        RaiseChanged();
        return written;
    }

    /// <summary>
    /// Forgets the paired token and address, after Gleanvolt refused the token or the owner unpaired.
    /// Does nothing when the token is the configured one: that is the owner's own setting, not ours to drop.
    /// </summary>
    public void ForgetPaired()
    {
        lock (_gate)
        {
            if (_configuredToken is not null)
            {
                return;
            }

            _pairedToken = null;
            _pairedEndpoint = null;
            _store?.Delete(TokenFile);
            _store?.Delete(EndpointFile);

            if (_arrived.Task.IsCompleted)
            {
                _arrived = NewSignal();
            }
        }

        RaiseChanged();
    }

    /// <summary>Completes when there is a token: at once when there already is one.</summary>
    public Task WaitForTokenAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _arrived.Task.WaitAsync(cancellationToken);
        }
    }

    // A subscriber is somebody else's code, and this runs inside the connection loop: one that throws must not
    // stop the provider's host, nor keep the others from hearing.
    private void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception)
            {
                // Nothing to tell: the credentials changed whether or not it was handled.
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
