namespace Solax.Infrastructure.Solcast;

/// <summary>
/// Configuration for the Solcast forecast integration. Bound from the <c>"Solcast"</c>
/// configuration section. The <see cref="ApiKey"/> is a secret and must come from outside the
/// repository (user-secrets in development, environment variables in deployment) -- never
/// committed to <c>appsettings.json</c>.
/// </summary>
public sealed class SolcastOptions
{
    public const string SectionName = "Solcast";

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
