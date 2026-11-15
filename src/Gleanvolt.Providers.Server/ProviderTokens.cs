using System.Security.Cryptography;
using System.Text;
using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Providers.Server;

/// <summary>
/// One token per kind (#386), so a leaked token can publish only that kind's data. Made here and kept
/// in the secret store as it is, like the API keys, so Settings can reveal it to a signed-in owner
/// who is setting up a provider. The store's own protection is what keeps it from the network.
/// </summary>
public sealed class ProviderTokens(ISecretStore secrets)
{
    private const string Prefix = "gvp_";

    /// <summary>Whether <paramref name="kind"/> has a token.</summary>
    public bool Exists(string kind) => Read(kind) is not null;

    /// <summary><paramref name="kind"/>'s token, or null when it has none.</summary>
    public string? Read(string kind) => secrets.Read(SecretNames.ProviderToken(kind)) is { Length: > 0 } token ? token : null;

    /// <summary>A new token for <paramref name="kind"/>, replacing the old one. Null when the store would not keep it.</summary>
    public string? Create(string kind)
    {
        var token = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return secrets.Write(SecretNames.ProviderToken(kind), token) ? token : null;
    }

    /// <summary>Whether <paramref name="token"/> is <paramref name="kind"/>'s.</summary>
    public bool Matches(string kind, string? token) =>
        !string.IsNullOrEmpty(token) && Read(kind) is { } stored &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(stored));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
