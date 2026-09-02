namespace Gleanvolt.Infrastructure.Solcast;

/// <summary>
/// Configuration for the Solcast forecast integration. Bound from the <c>"Solcast"</c>
/// configuration section. The <see cref="ApiKey"/> is a secret and must come from outside the
/// repository (user-secrets in development, environment variables in deployment) -- never
/// committed to <c>appsettings.json</c>.
/// </summary>
public sealed class SolcastOptions
{
    public const string SectionName = "Solcast";

    /// <summary>
    /// Whether the forecast is fetched at all. On by default, and the only reason to turn it off is
    /// the one this exists for: <b>the free tier's quota belongs to a site, not to a machine.</b>
    ///
    /// <para>Ten calls a day, and the controller on the roof spends about five of them. A developer
    /// running the same configuration on a workstation spends the same site's quota — one call per
    /// <c>dotnet run</c> — and the symptom is not a local error but a <c>429</c> on the Pi hours
    /// later, which reads as a bug in whatever was deployed most recently. That is a genuinely
    /// expensive hour to lose, and it is lost by the person least able to explain it.</para>
    ///
    /// <para>Checked <b>before</b> <see cref="ApiKey"/> rather than instead of it, so a workstation
    /// keeps a working key in its <c>.env</c> and still makes no call — the alternative, deleting the
    /// key, turns a deliberate choice into something indistinguishable from a misconfiguration, warns
    /// about it on every refresh, and has to be undone before the key can ever be used.</para>
    ///
    /// <para>Off in <c>appsettings.Development.json</c>; nothing changes for a deployment.</para>
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Base address of the Solcast API.</summary>
    public string BaseUrl { get; init; } = "https://api.solcast.com.au/";

    /// <summary>Solcast API key. Secret -- supply via user-secrets or an environment variable.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The Solcast rooftop-site (resource) id to fetch the forecast for.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>
    /// How often the cached forecast is refreshed from Solcast <b>during daylight</b>. Three hours by
    /// default: the forecast-driven charge strategy plans against a deadline, and a 12-hour-old
    /// forecast cannot steer an afternoon. Refreshes are skipped overnight (see
    /// <see cref="DaylightThresholdWatts"/>), so this works out at roughly five calls a day — inside
    /// the free hobbyist tier's ten.
    /// </summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromHours(3);

    /// <summary>
    /// Expected PV power above which the sun counts as "up" for refresh scheduling. Below it the
    /// worker sleeps until the forecast's next daylight period instead of spending API calls on the
    /// dark, where the forecast cannot change anything we do.
    /// </summary>
    public double DaylightThresholdWatts { get; init; } = 100;

    /// <summary>
    /// Longest the refresh loop will sleep through the night before fetching again regardless. Bounds
    /// the damage if the cached forecast's daylight periods are missing or wrong.
    /// </summary>
    public TimeSpan MaxNightSleep { get; init; } = TimeSpan.FromHours(6);
}
