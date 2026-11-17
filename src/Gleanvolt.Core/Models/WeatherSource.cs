namespace Gleanvolt.Core.Models;

/// <summary>
/// Where the weather a charging session is recorded with comes from (#424): <c>Weather:Source</c>,
/// chosen on Settings and applied without a restart.
///
/// <para>An installation that never chose keeps what it had before the choice existed: OpenWeather while
/// a key is configured, nothing otherwise.</para>
/// </summary>
public enum WeatherSource
{
    /// <summary>No weather: sessions are recorded without it. A finished choice, not a missing setting.</summary>
    NotProvided,

    /// <summary>OpenWeatherMap, built in, with the key on Settings or in <c>Weather__ApiKey</c>.</summary>
    OpenWeather,

    /// <summary>An external provider, publishing on the weather hub (<c>/providers/weather</c>).</summary>
    External,
}
