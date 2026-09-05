using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gleanvolt.Infrastructure.Vehicles.VwWebsite;

/// <summary>
/// Keeps a signed-in volkswagen.de session across restarts (issue #170).
///
/// <para><b>The difference between usable and hateful.</b> A cold login always demands an email
/// one-time code — verified against the live account rather than assumed. The cookie jar carries the
/// "remember this browser" grant that stops it being asked again, so persisting it turns a code every
/// restart into a code rarely. Without this the feature would be switched off within a week.</para>
///
/// <para><b>Treated as a secret, because it is one.</b> These cookies are bearer-equivalent: whoever
/// holds them is signed in as the owner. The file is written owner-only, never logged, never
/// rendered, and a failure to save is not allowed to take the process down — the session in memory is
/// still good, and the cost of losing the file is one code next restart.</para>
/// </summary>
public sealed class VwWebsiteSessionStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path => path;

    /// <summary>Whether a saved session exists to try. Says nothing about whether it still works.</summary>
    public bool Exists => File.Exists(path);

    /// <summary>
    /// Loads the jar, or returns an empty one. A file we cannot read is not an error worth stopping
    /// for: it means a code will be wanted, which the caller already knows how to ask for.
    /// </summary>
    public CookieContainer Load()
    {
        var jar = new CookieContainer();

        if (!File.Exists(path))
        {
            return jar;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<StoredCookie[]>(File.ReadAllText(path), Json) ?? [];

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
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or CookieException or ArgumentException)
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
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var cookies = jar.GetAllCookies()
                .Select(cookie => new StoredCookie(
                    cookie.Name, cookie.Value, cookie.Domain, cookie.Path,
                    cookie.Expires == default ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()),
                    cookie.Secure, cookie.HttpOnly))
                .ToArray();

            File.WriteAllText(path, JsonSerializer.Serialize(cookies, Json));
            Restrict(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Forgets the session, so the next attempt starts cold.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do: the caller is already signing in again.
        }
    }

    /// <summary>Owner-only where the platform has the concept. A no-op on Windows, which does not.</summary>
    private static void Restrict(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort. A readable file is worse than an unreadable one and better than no session.
        }
    }

    private sealed record StoredCookie(
        string Name, string Value, string Domain, string Path,
        DateTimeOffset? Expires, bool Secure, bool HttpOnly);
}
