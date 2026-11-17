namespace Gleanvolt.Plugin.OpenWeather.OpenWeather;

/// <summary>
/// The plugin's settings, from the <c>OpenWeather</c> section (#424). The API key is not here: it is
/// entered on the plugin's page, or given as <c>OpenWeather:ApiKey</c> (<see cref="ApiKeyStore"/>).
/// </summary>
public sealed class OpenWeatherOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "OpenWeather";

    /// <summary>Where the key and the page's own keys are kept. Give it a volume of its own in a container.</summary>
    public string DataDirectory { get; init; } = "data";

    /// <summary>Base address of the OpenWeatherMap API.</summary>
    public string BaseUrl { get; init; } = "https://api.openweathermap.org/";

    /// <summary>
    /// How long one call may take. Gleanvolt waits 15 seconds for an answer, so this stays well under it.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A request sooner than this after the last read answers with that reading. OpenWeatherMap updates
    /// its current conditions about every 10 minutes, so asking sooner only spends a call.
    /// </summary>
    public TimeSpan MinimumTimeBetweenReads { get; init; } = TimeSpan.FromMinutes(10);
}
