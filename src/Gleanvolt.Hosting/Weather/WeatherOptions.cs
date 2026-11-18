namespace Gleanvolt.Hosting.Weather;

/// <summary>
/// The weather, bound from the <c>"Weather"</c> section: where it comes from, and how long Gleanvolt
/// waits for an external provider. Since #425 Gleanvolt calls no weather service itself; OpenWeatherMap is
/// the OpenWeather plugin's (<c>plugins/Gleanvolt.Plugin.OpenWeather</c>).
///
/// <para>Which site the weather is for is <b>not here</b>: the coordinates belong to the installation and
/// live in the <c>Pv</c> section (issue #111), and Gleanvolt sends them to the provider.</para>
/// </summary>
public sealed class WeatherOptions
{
    public const string SectionName = "Weather";

    /// <summary>
    /// Where the weather comes from: <c>NotProvided</c> or <c>External</c>, in any case. Settings and the
    /// setup wizard write it to the overrides file. Unset is <c>NotProvided</c>. <c>OpenWeather</c>, the
    /// built-in removed in #425, is read once as <c>External</c> and rewritten (see
    /// <see cref="WeatherSourceSelection"/>). Any other value is logged and treated as unset.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// How long Gleanvolt waits for an external provider to answer a request for the weather (#424). The
    /// provider makes its own call to its weather service before it can answer.
    /// </summary>
    public TimeSpan ExternalRequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How old a reading an external provider sent may be and still be used without asking again (#424).
    /// A session that opens a few minutes after the provider published takes that reading.
    /// </summary>
    public TimeSpan ExternalReadingMaxAge { get; init; } = TimeSpan.FromMinutes(10);
}
