using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Infrastructure.Vehicles.VwWebsite;

/// <summary>
/// Keeps a signed-in volkswagen.de session across restarts (issue #170), through
/// <see cref="ISecretStore"/>.
///
/// <para><b>The difference between usable and hateful.</b> A cold login always demands an email
/// one-time code — verified against the live account rather than assumed. The cookie jar carries the
/// "remember this browser" grant that stops it being asked again, so persisting it turns a code every
/// restart into a code rarely. Without this the feature would be switched off within a week.</para>
///
/// <para><b>Treated as a secret, because it is one.</b> These cookies are bearer-equivalent: whoever
/// holds them is signed in as the owner. They are never logged and never rendered, and a failure to
/// save is not allowed to take the process down — the session in memory is still good, and the cost of
/// losing it is one code next restart. <b>Where</b> they are kept and what protects them is the secret
/// store's business, not this type's (issue #215); this type knows about cookies.</para>
/// </summary>
public sealed class VwWebsiteSessionStore(ISecretStore secrets)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>What protects the saved session, for a sentence that has to mention it. Never the cookies.</summary>
    public string Protection => secrets.Describe();

    /// <summary>
    /// Whether a saved session exists to try. Says nothing about whether it still works — and a
    /// session that cannot be read back, for whatever reason, is one that does not exist.
    /// </summary>
    public bool Exists => secrets.Read(SecretNames.VwWebsiteSession) is not null;

    /// <summary>
    /// Loads the jar, or returns an empty one. A session we cannot read is not an error worth stopping
    /// for: it means a code will be wanted, which the caller already knows how to ask for.
    /// </summary>
    public CookieContainer Load()
    {
        var jar = new CookieContainer();

        if (secrets.Read(SecretNames.VwWebsiteSession) is not { } stored)
        {
            return jar;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<StoredCookie[]>(stored, Json) ?? [];

            foreach (var cookie in saved)
            {
                if (cookie.Expires is { } expires && expires <= DateTimeOffset.UtcNow)
                {
                    continue;
                }

                jar.Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
                {
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly,
                    Expires = cookie.Expires?.UtcDateTime ?? default,
                });
            }
        }
        catch (Exception ex) when (ex is JsonException or CookieException or ArgumentException)
        {
            return new CookieContainer();
        }

        return jar;
    }

    /// <summary>
    /// Saves the jar. Returns whether it stuck — the caller logs a miss rather than failing, because
    /// an unsaved session still works until the process ends.
    /// </summary>
    public bool Save(CookieContainer jar)
    {
        var cookies = jar.GetAllCookies()
            .Select(cookie => new StoredCookie(
                cookie.Name, cookie.Value, cookie.Domain, cookie.Path,
                cookie.Expires == default ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()),
                cookie.Secure, cookie.HttpOnly))
            .ToArray();

        return secrets.Write(SecretNames.VwWebsiteSession, JsonSerializer.Serialize(cookies, Json));
    }

    /// <summary>Forgets the session, so the next attempt starts cold.</summary>
    public void Clear() => secrets.Delete(SecretNames.VwWebsiteSession);

    private sealed record StoredCookie(
        string Name, string Value, string Domain, string Path,
        DateTimeOffset? Expires, bool Secure, bool HttpOnly);
}
