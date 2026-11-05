using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The OpenWeatherMap key on Settings and in the setup wizard. Like the Solcast account, a save applies
/// at once: the weather service reads the key on every call, so there is nothing to restart.
/// </summary>
public interface IWeatherKeyEditor
{
    /// <summary>The key in use and where it came from.</summary>
    WeatherKeySettings Read();

    /// <summary>
    /// Stores the key and asks OpenWeatherMap once with it, which is the check. The free plan allows a
    /// thousand calls a day, so unlike Solcast the check costs nothing worth counting.
    /// </summary>
    Task<WeatherKeySaveResult> SaveAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>Forgets the key the page stored. The environment's, when there is one, is in use again.</summary>
    void Remove();
}
