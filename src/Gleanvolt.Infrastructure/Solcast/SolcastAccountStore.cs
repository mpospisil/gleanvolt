using System.Text.Json;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Solcast;

/// <summary>
/// The Solcast account in use, shared by Settings and <see cref="SolcastForecastService"/> (issue #286).
///
/// <para><b>One holder, read on every fetch.</b> The page writes it and the service reads it each time
/// it asks Solcast, so an account saved on the page is used by the next fetch without a restart — which
/// matters more here than anywhere, because a restart itself spends one of the day's ~10 calls.
/// <see cref="Version"/> moves on every save and every clear, which is how the service tells "the key it
/// was refused" from "a new one the owner has just saved" without keeping a second copy.</para>
///
/// <para><b>The page wins over the environment.</b> <c>docker-compose.yml</c> always sets
/// <c>Solcast__ApiKey</c>, empty or not, so if the environment won, the page could never change it.
/// An account is the key and the site together: the page stores both, and the environment's is used
/// only when the page holds none. One record rather than a key here and a site in
/// <c>pv-system.json</c>, because that file is read at startup and the point is not to need one.</para>
///
/// <para><b>Treated as a secret.</b> The key is bearer-equivalent to the account's daily quota. It is
/// never logged, and a failure to save is not allowed to take the process down: the account in memory
/// still works until the process ends.</para>
/// </summary>
public sealed class SolcastAccountStore
{
    private readonly ISecretStore _secrets;
    private readonly SolcastAccount? _environment;
    private volatile SolcastAccount? _saved;
    private int _version;

    public SolcastAccountStore(ISecretStore secrets, IOptions<SolcastOptions> options)
    {
        _secrets = secrets;

        var configured = options.Value;
        var environment = new SolcastAccount(configured.ApiKey.Trim(), configured.ResourceId.Trim());
        _environment = environment.IsComplete ? environment : null;
        _saved = Load(secrets);
    }

    /// <summary>The account in use — the page's, else the environment's — or null when neither is complete.</summary>
    public SolcastAccount? Current => _saved ?? _environment;

    /// <summary>Where <see cref="Current"/> came from.</summary>
    public SolcastAccountSource Source => _saved is not null
        ? SolcastAccountSource.Page
        : _environment is not null ? SolcastAccountSource.Environment : SolcastAccountSource.None;

    /// <summary>Whether the environment holds a complete account, which is in use whenever the page holds none.</summary>
    public bool EnvironmentConfigured => _environment is not null;

    /// <summary>What protects the saved key, for a sentence that has to mention it. Never the key itself.</summary>
    public string Protection => _secrets.Describe();

    /// <summary>Moves on every save and clear, so a reader can tell a new account from the one it had.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Makes this the account in use and writes it to the secret store. Returns whether it stuck — the
    /// account is in use either way, and the caller says a restart will want it again when it did not.
    /// </summary>
    public bool Save(SolcastAccount account)
    {
        if (!account.IsComplete)
        {
            throw new ArgumentException("A Solcast account needs both a key and a rooftop site.", nameof(account));
        }

        _saved = account;
        Interlocked.Increment(ref _version);
        return _secrets.Write(SecretNames.SolcastAccount, JsonSerializer.Serialize(account));
    }

    /// <summary>Forgets the page's account, in memory and on disk. The environment's is in use again, if any.</summary>
    public void Clear()
    {
        _saved = null;
        Interlocked.Increment(ref _version);
        _secrets.Delete(SecretNames.SolcastAccount);
    }

    /// <summary>
    /// The saved account, or none. A stored value that cannot be read back is not worth stopping for: it
    /// means the page asks for the account again, and the environment's is used meanwhile.
    /// </summary>
    private static SolcastAccount? Load(ISecretStore secrets)
    {
        if (secrets.Read(SecretNames.SolcastAccount) is not { } stored)
        {
            return null;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<SolcastAccount>(stored);
            return saved is { IsComplete: true } ? saved : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A Solcast API key and the rooftop site it is asked about.</summary>
/// <param name="ApiKey">The secret. Never logged, never rendered without a login.</param>
/// <param name="ResourceId">The rooftop site's resource id, as Solcast's site page shows it.</param>
public sealed record SolcastAccount(string ApiKey, string ResourceId)
{
    /// <summary>A key and a site; either alone fetches nothing.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ResourceId);

    /// <summary>Kept out of <c>ToString</c>, which a log template or a debugger would otherwise print whole.</summary>
    public override string ToString() => $"SolcastAccount {{ ResourceId = {ResourceId} }}";
}
