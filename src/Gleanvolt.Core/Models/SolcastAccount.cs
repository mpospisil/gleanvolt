namespace Gleanvolt.Core.Models;

/// <summary>Where the Solcast account in use came from (issue #286).</summary>
public enum SolcastAccountSource
{
    /// <summary>Neither the page nor the environment holds a key and a site.</summary>
    None,

    /// <summary><c>Solcast:ApiKey</c> / <c>Solcast:ResourceId</c>: <c>.env</c>, user-secrets or appsettings.</summary>
    Environment,

    /// <summary>Entered on Settings and kept in the secret store. Wins over the environment.</summary>
    Page,
}

/// <summary>Why the last forecast fetch failed, in the terms the owner has to act on.</summary>
public enum SolcastFetchFailure
{
    None,

    /// <summary>401 or 403: the key is wrong, revoked, or not this account's. Not retried until a new one is saved.</summary>
    KeyRefused,

    /// <summary>404: the key works but has no rooftop site with this id. Not retried until the account changes.</summary>
    SiteNotFound,

    /// <summary>429: the day's calls are spent. Retried on the normal schedule; the quota resets daily.</summary>
    QuotaExhausted,

    /// <summary>Anything else — the network, a 5xx, an answer that is not a forecast.</summary>
    Other,
}

/// <summary>How the last fetch went. Times are UTC instants; the page shows them in local time.</summary>
/// <param name="FetchedAt">When the forecast held now was fetched, or null when none is held.</param>
/// <param name="FailedAt">When the latest fetch failed, or null when the latest one succeeded or none was tried.</param>
/// <param name="Failure">What that failure was.</param>
/// <param name="HttpStatus">The status Solcast answered with, when it answered at all.</param>
public sealed record SolcastFetchStatus(
    DateTimeOffset? FetchedAt,
    DateTimeOffset? FailedAt,
    SolcastFetchFailure Failure,
    int? HttpStatus)
{
    public static SolcastFetchStatus Never { get; } = new(null, null, SolcastFetchFailure.None, null);

    /// <summary>True when the latest attempt is a failure the owner has to fix: no new fetch will cure it.</summary>
    public bool IsRefused => Failure is SolcastFetchFailure.KeyRefused or SolcastFetchFailure.SiteNotFound;
}

/// <summary>
/// The Solcast account as Settings shows it.
/// </summary>
/// <param name="Source">Where the account in use came from.</param>
/// <param name="ApiKey">The key in use, or empty. A secret: shown only behind a login, never logged.</param>
/// <param name="ResourceId">The rooftop site in use, or empty.</param>
/// <param name="EnvironmentConfigured">Whether the environment holds a complete account, which takes over on Remove.</param>
/// <param name="SecretProtection">What protects the stored key, in the secret store's own words.</param>
/// <param name="Status">How the last fetch went.</param>
public sealed record SolcastAccountSettings(
    SolcastAccountSource Source,
    string ApiKey,
    string ResourceId,
    bool EnvironmentConfigured,
    string SecretProtection,
    SolcastFetchStatus Status)
{
    /// <summary>A key and a site: what a fetch needs.</summary>
    public bool IsConfigured => Source != SolcastAccountSource.None;

    /// <summary>Kept out of <c>ToString</c>, which a log template or a debugger would otherwise print whole.</summary>
    public override string ToString() =>
        $"SolcastAccountSettings {{ Source = {Source}, ResourceId = {ResourceId}, Status = {Status} }}";
}

/// <summary>What a save on Settings came to.</summary>
/// <param name="Errors">Why nothing was saved; empty when the account was saved.</param>
/// <param name="Persisted">False when the account is in use but the secret store would not keep it, so a restart forgets it.</param>
public sealed record SolcastAccountSaveResult(IReadOnlyList<string> Errors, bool Persisted)
{
    public bool Saved => Errors.Count == 0;
}
