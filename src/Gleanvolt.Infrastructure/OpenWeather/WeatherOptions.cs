namespace Gleanvolt.Infrastructure.OpenWeather;

/// <summary>
/// Configuration for the weather integration, bound from the <c>"Weather"</c> section. The
/// <see cref="ApiKey"/> is a secret and must come from outside the repository — user-secrets in
/// development, an environment variable in deployment (<c>Weather__ApiKey</c>) — never from a
/// committed <c>appsettings.json</c>.
///
/// <para>Which site the weather is fetched for is <b>not here</b>: the coordinates belong to the
/// installation and live in the <c>Pv</c> section (issue #111). This section is about the provider —
/// its address, its key, its units and how long it may take.</para>
///
/// <para>With no key, or a site with no coordinates, the feature is simply off: no HTTP calls are made
/// and sessions are recorded without weather. That is a configuration state, not a failure.</para>
/// </summary>
public sealed class WeatherOptions
{
    public const string SectionName = "Weather";

    /// <summary>Base address of the OpenWeatherMap API.</summary>
    public string BaseUrl { get; init; } = "https://api.openweathermap.org/";

    /// <summary>OpenWeatherMap API key. Secret — supply via user-secrets or an environment variable.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// The provider's unit system. <c>metric</c> gives °C and metres per second, which is what every
    /// stored figure is documented in; changing it changes what the numbers in the database mean.
    /// </summary>
    public string Units { get; init; } = "metric";

    /// <summary>
    /// How long a single fetch may take before it is abandoned. Deliberately short: this call sits
    /// between a session ending and its totals being written, and no weather figure is worth delaying
    /// that. A timeout costs one null column.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
