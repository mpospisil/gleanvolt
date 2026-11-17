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
    /// Whether <see cref="Selected"/> was chosen (on Settings, in the wizard or in the configuration), as
    /// opposed to worked out from whether an OpenWeatherMap key is configured.
    /// </summary>
    bool IsChosen { get; }

    /// <summary>
    /// Saves <paramref name="source"/> for the next start and uses it from now on. Refused, with nothing
    /// changed, when the choice cannot be saved.
    /// </summary>
    SettingsSaveResult Select(WeatherSource source);

    /// <summary>Raised after the source in use changes.</summary>
    event Action? Changed;
}
