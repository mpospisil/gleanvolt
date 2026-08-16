using Microsoft.AspNetCore.Identity;

namespace Gleanvolt.Web.Auth;

/// <summary>
/// Hashes and verifies the single shared password behind <see cref="WebOptions.PasswordHash"/>.
///
/// <para>A thin wrapper over <see cref="PasswordHasher{TUser}"/> rather than a hand-rolled scheme:
/// PBKDF2 with a random salt and a version-tagged output is exactly what this needs, and it already
/// ships in the shared framework. The generic parameter is unused here — there is one password, not
/// one per user — so a placeholder type stands in for it.</para>
/// </summary>
public static class WebPasswordHasher
{
    // A placeholder for PasswordHasher<TUser>'s generic parameter: there is one shared password, not
    // one per user, and the static class itself cannot fill that slot.
    private sealed class NoUser;

    private static readonly PasswordHasher<NoUser> Hasher = new();

    /// <summary>Hashes <paramref name="password"/> for storage in <see cref="WebOptions.PasswordHash"/>.</summary>
    public static string Hash(string password) => Hasher.HashPassword(null!, password);

    /// <summary>
    /// Whether <paramref name="password"/> matches <paramref name="hash"/>. False for an empty hash
    /// or an empty password, so a misconfigured (blank) hash can never be satisfied by a blank
    /// submission.
    /// </summary>
    public static bool Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        return Hasher.VerifyHashedPassword(null!, hash, password) != PasswordVerificationResult.Failed;
    }
}
