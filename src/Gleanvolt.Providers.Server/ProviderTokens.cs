using System.Security.Cryptography;
using System.Text;
using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Providers.Server;

/// <summary>
/// The one token every external provider connects with (#406). It was one per kind (#386); with more
/// than one plugin, an owner had a token to make, copy and keep for each. The hub path still decides
/// the kind, so a provider reaches only the hub it connects to, but whoever holds the token may
/// connect to any of them.
/// </summary>
/// <remarks>
/// Made here and kept in the secret store as it is, like the API keys, so Settings can reveal it to a
/// signed-in owner who is setting up a provider. The store's own protection is what keeps it from the
/// network.
/// </remarks>
public sealed class ProviderTokens
{
    private const string Prefix = "gvp_";

    private readonly ISecretStore _secrets;

    /// <param name="secrets">Where the token is kept.</param>
    /// <param name="legacyKinds">
    /// The kinds that may still have a token of their own from before #406. When there is no shared
    /// token yet, the first of theirs found is adopted, so the plugin using it keeps connecting; then
    /// every kind's own token is removed.
    /// </param>
    public ProviderTokens(ISecretStore secrets, IEnumerable<string>? legacyKinds = null)
    {
        _secrets = secrets;
        Adopt(legacyKinds?.ToList() ?? []);
    }

    /// <summary>The kind whose own token was adopted at this start, or null when none was.</summary>
    public string? AdoptedFrom { get; private set; }

    /// <summary>Whether the token exists.</summary>
    public bool Exists => Read() is not null;

    /// <summary>The token, or null when there is none.</summary>
    public string? Read() => _secrets.Read(SecretNames.ProviderToken) is { Length: > 0 } token ? token : null;

    /// <summary>A new token, replacing the old one. Null when the store would not keep it.</summary>
    public string? Create()
    {
        var token = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return _secrets.Write(SecretNames.ProviderToken, token) ? token : null;
    }

    /// <summary>Whether <paramref name="token"/> is the token.</summary>
    public bool Matches(string? token) =>
        !string.IsNullOrEmpty(token) && Read() is { } stored &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(stored));

    private void Adopt(IReadOnlyList<string> kinds)
    {
        if (kinds.Count == 0)
        {
            return;
        }

        if (!Exists)
        {
            var legacy = kinds
                .Select(kind => (Kind: kind, Token: _secrets.Read(SecretNames.LegacyProviderToken(kind))))
                .FirstOrDefault(found => found.Token is { Length: > 0 });

            if (legacy.Token is not null)
            {
                // Not kept: leave the old tokens where they are, for the next start to try again.
                if (!_secrets.Write(SecretNames.ProviderToken, legacy.Token))
                {
                    return;
                }

                AdoptedFrom = legacy.Kind;
            }
        }

        foreach (var kind in kinds)
        {
            if (_secrets.Read(SecretNames.LegacyProviderToken(kind)) is not null)
            {
                _secrets.Delete(SecretNames.LegacyProviderToken(kind));
            }
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
