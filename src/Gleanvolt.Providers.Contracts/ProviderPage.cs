using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Gleanvolt.Providers.Contracts;

/// <summary>
/// A provider's own page, shown in Gleanvolt (#410). The owner's browser reaches the provider directly, so
/// anyone on the network could open its URL; and on a page with a <i>Sign in</i> button, pressing it over
/// and over is how a car account gets locked. Gleanvolt therefore opens the page with a <b>ticket</b>: an
/// expiry and the provider's id, signed with HMAC-SHA256 under the provider token both sides already
/// hold. The provider checks it and answers 403 without one, so its page needs no login of its own.
///
/// <para>Gleanvolt also adds <see cref="ThemeParameter"/> and <see cref="CultureParameter"/>, so the page
/// can match the look and the language.</para>
/// </summary>
public static class ProviderPage
{
    /// <summary>The query parameter that carries the ticket.</summary>
    public const string TicketParameter = "gvticket";

    /// <summary>
    /// The query parameter that carries the theme: <c>auto</c> (follow the browser's
    /// <c>prefers-color-scheme</c>, as Gleanvolt does), <c>light</c> or <c>dark</c>.
    /// </summary>
    public const string ThemeParameter = "theme";

    /// <summary>The query parameter that carries the viewer's culture, e.g. <c>en</c> or <c>cs</c>.</summary>
    public const string CultureParameter = "culture";

    /// <summary>How long a ticket Gleanvolt issues is good for: it is used at once, by a redirect.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);

    /// <summary>The furthest ahead an expiry is believed: a ticket is short-lived by design.</summary>
    public static readonly TimeSpan MaxTicketLifetime = TimeSpan.FromDays(1);

    private const string TicketPurpose = "gleanvolt-page-ticket/v1";

    /// <summary>A ticket for <paramref name="providerId"/>'s page, good until <paramref name="expires"/>.</summary>
    /// <param name="token">The provider token.</param>
    /// <param name="providerId">The id the provider said hello with: a ticket for one provider opens no other's page.</param>
    /// <param name="expires">When it stops being accepted.</param>
    public static string CreateTicket(string token, string providerId, DateTimeOffset expires) =>
        Sign(TicketPurpose, token, providerId, expires);

    /// <summary>Whether <paramref name="ticket"/> was made with <paramref name="token"/> for <paramref name="providerId"/> and has not expired.</summary>
    public static bool VerifyTicket(string? ticket, string token, string providerId, DateTimeOffset now) =>
        Verify(TicketPurpose, ticket, token, providerId, now);

    /// <summary>
    /// Signs <paramref name="expires"/> for <paramref name="purpose"/>: <c>&lt;unix seconds&gt;.&lt;base64url HMAC&gt;</c>.
    /// The purpose keeps a value signed for one use (a ticket) from being accepted as another (an SDK cookie).
    /// </summary>
    public static string Sign(string purpose, string token, string providerId, DateTimeOffset expires)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(providerId);
        var seconds = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return seconds + "." + Mac(purpose, token, providerId, seconds);
    }

    /// <summary>Whether <paramref name="value"/> is a signature from <see cref="Sign"/> for the same purpose, token and provider, and not expired.</summary>
    public static bool Verify(string purpose, string? value, string token, string providerId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(token) || providerId is null)
        {
            return false;
        }

        var dot = value.IndexOf('.', StringComparison.Ordinal);

        if (dot <= 0 || !long.TryParse(value.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        DateTimeOffset expires;

        try
        {
            expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (expires <= now || expires > now + MaxTicketLifetime)
        {
            return false;
        }

        var expected = Mac(purpose, token, providerId, value[..dot]);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(value[(dot + 1)..]));
    }

    private static string Mac(string purpose, string token, string providerId, string seconds)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes($"{purpose}\n{providerId}\n{seconds}"));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
