using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Which solar forecast source is in use, chosen on Settings (#388): <c>SolarForecast:Source</c>. A
/// choice applies at once, without a restart, and is saved for the next start.
/// </summary>
public interface ISolarForecastSourceSelection
{
    /// <summary>The <see cref="SolarForecastSourceIds"/> value in use, or what is configured when it names no source.</summary>
    string Selected { get; }

    /// <summary>The sources this build has, in the order Settings offers them.</summary>
    IReadOnlyList<string> Available { get; }

    /// <summary>
    /// Saves <paramref name="sourceId"/> for the next start and uses it from now on. Refused, with
    /// nothing changed, for a source this build does not have and when the choice cannot be saved.
    /// </summary>
    SettingsSaveResult Select(string sourceId);

    /// <summary>Raised after the source in use changes.</summary>
    event Action? Changed;
}
