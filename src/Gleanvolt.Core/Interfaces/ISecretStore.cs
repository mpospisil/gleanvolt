namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Where the controller's bearer-equivalent secrets live (issue #215).
///
/// <para><b>One store, not three arrangements.</b> The volkswagen.de session, the MyŠkoda API key and
/// the vehicle account password are all the same kind of thing — hold one and you are the owner at
/// the other end — and before this seam each of them did its own file I/O with its own idea of what
/// protected it. One seam means one answer to "what protects these?", one place to change it, and one
/// sentence to say about it.</para>
///
/// <para><b>What it does not claim.</b> The controller must come back from a restart with nobody
/// present, so whatever key protects a stored secret has to be readable by this process, on this
/// machine, at boot, unattended. Nothing here is therefore proof against a local root; see
/// <see cref="Describe"/>, which returns what is true rather than the word <i>encrypted</i>.</para>
///
/// <para><b>Failure is never an exception out of this seam.</b> A secret that cannot be read — absent,
/// truncated, or sealed to a profile this process no longer has — comes back as null, which every
/// caller already handles as <i>sign in again</i>. A secret that cannot be written comes back as
/// false, and the caller says that a restart will ask for it again; the value in memory still works
/// until the process ends.</para>
/// </summary>
public interface ISecretStore
{
    /// <summary>The secret, or null when there is none to be had. Never throws.</summary>
    /// <param name="name">A name from <see cref="SecretNames"/>.</param>
    string? Read(string name);

    /// <summary>Stores <paramref name="value"/> under <paramref name="name"/>. Returns whether it stuck.</summary>
    bool Write(string name, string value);

    /// <summary>
    /// Removes the secret from the store. Deleting means deleting: nothing is left behind holding the
    /// value. Returns whether the store is now without it, which a secret that was never there also is.
    /// </summary>
    bool Delete(string name);

    /// <summary>
    /// What protects these secrets, in words that do not overclaim — <i>owner-only file</i>, <i>Windows
    /// DPAPI, this user</i>. Goes in the startup log and on <c>/health</c>, so that the question
    /// "is it safe to copy the data directory?" has an answer that is in front of the person copying it.
    /// </summary>
    string Describe();
}

/// <summary>
/// The secrets the controller keeps. Named here rather than spelled at each call site: the name is
/// also the file name under <c>FileSecretStore</c>, so a typo would silently start a second secret.
/// </summary>
public static class SecretNames
{
    /// <summary>The volkswagen.de cookie jar, including the <i>remember this browser</i> grant (#170).</summary>
    public const string VwWebsiteSession = "vw-website-session";

    /// <summary>The pasted MyŠkoda Public API key and what Škoda said about it (#193).</summary>
    public const string SkodaApiKey = "skoda-api-key";

    /// <summary>The vehicle account password, when it is entered in the web UI rather than in the environment (#214).</summary>
    public const string VehicleAccountPassword = "vehicle-account-password";
}
