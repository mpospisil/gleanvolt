namespace Gleanvolt.Core.Models;

/// <summary>
/// Where the weather a charging session is recorded with comes from (#424): <c>Weather:Source</c>,
/// chosen on Settings and applied without a restart.
///
/// <para>The built-in OpenWeatherMap integration was the third choice until #425. OpenWeatherMap now comes
/// through the OpenWeather plugin, an external provider; an installation that used the built-in is moved
/// to <see cref="External"/> on its first start.</para>
/// </summary>
public enum WeatherSource
{
    /// <summary>No weather: sessions are recorded without it. A finished choice, not a missing setting.</summary>
    NotProvided,

    /// <summary>An external provider, publishing on the weather hub (<c>/providers/weather</c>), such as the OpenWeather plugin.</summary>
    External,
}
