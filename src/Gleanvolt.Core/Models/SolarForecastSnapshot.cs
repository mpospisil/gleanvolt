namespace Gleanvolt.Core.Models;

/// <summary>
/// Which source a held forecast came from (issue #387): its <see cref="SolarForecastSourceIds"/> id, so a
/// source can tell its own forecast from another's, and the name to show for it.
/// </summary>
public sealed record SolarForecastOrigin(string SourceId, string Name);

/// <summary>
/// The forecast the store holds, as one value replaced whole by a publish: the forecast, where it came
/// from, and when the store received it.
/// </summary>
public sealed record SolarForecastSnapshot(SolarForecast Forecast, SolarForecastOrigin Origin, DateTimeOffset ReceivedAt);

/// <summary>What <c>SolarForecast:Source</c> selects by.</summary>
public static class SolarForecastSourceIds
{
    /// <summary>The Solcast rooftop-sites API, and the default.</summary>
    public const string Solcast = "solcast";
}
