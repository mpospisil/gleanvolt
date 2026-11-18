using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Which weather source is in use, chosen on Settings and in the setup wizard (#424):
/// <c>Weather:Source</c>. A choice applies at once, without a restart, and is saved for the next start.
/// </summary>
public interface IWeatherSourceSelection
{
    /// <summary>The source in use.</summary>
    WeatherSource Selected { get; }

    /// <summary>
    /// True when this installation used the built-in OpenWeather, which #425 removed, and no weather
    /// provider has connected since: Settings tells the owner that the weather now comes from the
    /// OpenWeather plugin and that its key is entered on the plugin's page.
    /// </summary>
    bool BuiltInRetired { get; }

    /// <summary>
    /// Saves <paramref name="source"/> for the next start and uses it from now on. Refused, with nothing
    /// changed, when the choice cannot be saved.
    /// </summary>
    SettingsSaveResult Select(WeatherSource source);

    /// <summary>Raised after the source in use changes, or the notice about the built-in goes.</summary>
    event Action? Changed;
}
