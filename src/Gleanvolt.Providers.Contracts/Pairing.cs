using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Gleanvolt.Providers.Contracts;

/// <summary>
/// The paths of pairing: a plugin that starts without a token knocks on Gleanvolt and later collects the
/// provider token once the owner has accepted it.
/// </summary>
public static class PairingPaths
{
    /// <summary>Where a plugin knocks (POST a <see cref="PairingKnock"/>, answered with a <see cref="PairingTicket"/>).</summary>
    public const string Knock = "/providers/pairing";

    /// <summary>Where a plugin collects the answer to its knock (POST a <see cref="PairingCollect"/>).</summary>
    /// <param name="requestId">The <see cref="PairingTicket.RequestId"/> the knock was answered with.</param>
    public static string Collect(string requestId) => $"/providers/pairing/{requestId}/collect";
}

/// <summary>A plugin without a token asking to be paired.</summary>
/// <param name="HubPath">
/// The hub path the plugin serves (e.g. <c>/providers/charger</c>). It names the kind, as the hub URL
/// already does; the server resolves it with <c>ExternalProviderRegistry.KindAt</c>.
/// </param>
/// <param name="ProviderId">The plugin's stable id, as it will say it in its hello.</param>
/// <param name="Name">What Gleanvolt shows the owner.</param>
/// <param name="Version">The plugin's own version.</param>
/// <param name="PageUrl">The plugin's own web page (see <see cref="ProviderHello.PageUrl"/>), or null.</param>
/// <param name="SecretHash">
/// <see cref="PairingSecret.Hash"/> of the secret the plugin keeps to itself. Both sides derive the code the
/// owner compares from it (<see cref="PairingSecret.Code"/>).
/// </param>
public sealed record PairingKnock(
    string HubPath,
    string ProviderId,
    string Name,
    string Version,
    Uri? PageUrl,
    string SecretHash);

/// <summary>The answer to a <see cref="PairingKnock"/>.</summary>
/// <param name="RequestId">Names the pairing request when the plugin collects.</param>
public sealed record PairingTicket(string RequestId);

/// <summary>What a plugin sends to collect: the secret whose hash it knocked with.</summary>
/// <param name="Secret">The secret itself, proving it is the plugin that knocked.</param>
public sealed record PairingCollect(string Secret);

/// <summary>Where a pairing request stands.</summary>
public enum PairingState
{
    /// <summary>The owner has not decided yet.</summary>
    Waiting,

    /// <summary>The owner accepted; the answer carries the token.</summary>
    Accepted,

    /// <summary>The owner rejected it.</summary>
    Rejected,

    /// <summary>No such request, or it has expired or was already collected.</summary>
    Unknown
}

/// <summary>The answer to a <see cref="PairingCollect"/>.</summary>
/// <param name="State">Where the request stands.</param>
/// <param name="Token">The provider token, only when <paramref name="State"/> is <see cref="PairingState.Accepted"/>.</param>
public sealed record PairingAnswer(PairingState State, string? Token = null);

/// <summary>
/// The secret a plugin pairs with and the code derived from it. Only the hash travels in the knock, so
/// someone who sees the knock cannot collect; the code lets the owner check that the request on screen is the
/// plugin's own.
/// </summary>
public static class PairingSecret
{
    /// <summary>A new secret: 32 random bytes, base64url without padding.</summary>
    public static string Create() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The hash a knock carries: SHA-256 of the UTF-8 secret, base64url without padding.</summary>
    public static string Hash(string secret) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// The six digits, in two groups of three, that Gleanvolt and the plugin both show for the owner to compare.
    /// </summary>
    public static string Code(string secretHash)
    {
        var bytes = FromBase64Url(secretHash);
        var digits = (BinaryPrimitives.ReadUInt32BigEndian(bytes) % 1_000_000).ToString("D6");
        return $"{digits[..3]} {digits[3..]}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
