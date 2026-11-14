using System.Security.Cryptography;
using System.Text;
using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Providers.Server;

/// <summary>
/// One token per kind (#386), so a leaked token can publish only that kind's data. Made here, shown
/// once, and kept only as its SHA-256 hash in the secret store: the token is a long random string, so
/// a plain hash is as good as a slow one, and it makes a check cheap enough for every connection.
/// </summary>
public sealed class ProviderTokens(ISecretStore secrets)
{
    private const string Prefix = "gvp_";

    /// <summary>Whether <paramref name="kind"/> has a token.</summary>
    public bool Exists(string kind) => secrets.Read(SecretNames.ProviderToken(kind)) is { Length: > 0 };

    /// <summary>A new token for <paramref name="kind"/>, replacing the old one. Null when the store would not keep it.</summary>
    public string? Create(string kind)
    {
        var token = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return secrets.Write(SecretNames.ProviderToken(kind), Hash(token)) ? token : null;
    }

    /// <summary>Whether <paramref name="token"/> is <paramref name="kind"/>'s.</summary>
    public bool Matches(string kind, string? token)
    {
        if (string.IsNullOrEmpty(token) || secrets.Read(SecretNames.ProviderToken(kind)) is not { Length: > 0 } stored)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(stored));
    }

    private static string Hash(string token) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
