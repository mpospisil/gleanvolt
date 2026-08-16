using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Provides locally-cached solar-generation forecasts for the configured site. Implementations
/// fetch from an external provider on a background schedule and serve the last-known forecast
/// synchronously and cheaply, so callers on hot paths (e.g. the polling loop) never do I/O.
/// </summary>
public interface ISolarForecastService
{
    /// <summary>
    /// The forecast periods for the current local day, or <c>null</c> if no forecast has been
    /// fetched yet.
    /// </summary>
    SolarForecast? GetForecastForToday();

    /// <summary>
    /// The forecast periods overlapping the window <c>(from, to]</c> (intended to be within
    /// today), or <c>null</c> if no forecast has been fetched yet.
    /// </summary>
    SolarForecast? GetForecast(DateTimeOffset from, DateTimeOffset to);
}
