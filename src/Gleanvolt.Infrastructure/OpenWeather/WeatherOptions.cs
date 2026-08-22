namespace Gleanvolt.Infrastructure.OpenWeather;

/// <summary>
/// Configuration for the weather integration, bound from the <c>"Weather"</c> section. The
/// <see cref="ApiKey"/> is a secret and must come from outside the repository — user-secrets in
/// development, an environment variable in deployment (<c>Weather__ApiKey</c>) — never from a
/// committed <c>appsettings.json</c>.
///
/// <para>With no key, or no coordinates, the feature is simply off: no HTTP calls are made and
/// sessions are recorded without weather. That is a configuration state, not a failure.</para>
/// </summary>
public sealed class WeatherOptions
{
    public const string SectionName = "Weather";

    /// <summary>Base address of the OpenWeatherMap API.</summary>
    public string BaseUrl { get; init; } = "https://api.openweathermap.org/";

    /// <summary>OpenWeatherMap API key. Secret — supply via user-secrets or an environment variable.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// The site's latitude in decimal degrees. Null rather than 0 when unset: 0,0 is a real place in
    /// the Atlantic, and a defaulted coordinate would silently record the weather in the Gulf of
    /// Guinea rather than saying nothing.
    /// </summary>
    public double? Latitude { get; init; }

    /// <summary>The site's longitude in decimal degrees. Null when unset — see <see cref="Latitude"/>.</summary>
    public double? Longitude { get; init; }

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
