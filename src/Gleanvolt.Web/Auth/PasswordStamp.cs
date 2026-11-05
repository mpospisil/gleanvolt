using System.Security.Cryptography;
using System.Text;

namespace Gleanvolt.Web.Auth;

/// <summary>
/// A short fingerprint of the password hash in force, carried in the sign-in cookie (#301).
///
/// <para>The cookie used to prove only that somebody once knew <em>a</em> password. A reset has to
/// sign everybody out -- the point of resetting a password that leaked is that whoever has it stops
/// being signed in -- and with one shared account there is no user record to bump a counter on. The
/// password hash is the only state there is, so the stamp is derived from it: a new hash, a new stamp,
/// and every older cookie is refused on its next request.</para>
///
/// <para>Derived from the hash, not the password, and only 16 characters of a SHA-256: it identifies
/// which password a cookie was issued under and is useless for anything else.</para>
/// </summary>
public static class PasswordStamp
{
    /// <summary>The claim type the stamp travels under.</summary>
    public const string ClaimType = "gleanvolt:password-stamp";

    /// <summary>The stamp of <paramref name="passwordHash"/>; a fixed value for "no password".</summary>
    public static string Of(string passwordHash)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash ?? ""));
        return Convert.ToHexString(digest, 0, 8);
    }
}
