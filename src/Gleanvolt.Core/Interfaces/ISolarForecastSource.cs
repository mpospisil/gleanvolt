namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Somewhere a solar forecast comes from (issue #387): Solcast now, an external provider next (#388).
/// A source fetches, or asks for, a forecast and publishes it to the store that every reader of
/// <see cref="ISolarForecastService"/> is served from; it holds no forecast of its own.
///
/// <para>One is active at a time, selected by <c>SolarForecast:Source</c> against <see cref="Id"/>, and
/// the refresh worker asks only that one. Sources that are not selected are never asked.</para>
/// </summary>
public interface ISolarForecastSource
{
    /// <summary>What <c>SolarForecast:Source</c> selects by: a <see cref="Models.SolarForecastSourceIds"/> value.</summary>
    string Id { get; }

    /// <summary>
    /// Fetches a new forecast and publishes it, or asks for one to be published.
    ///
    /// <para><b>Does not throw for a failed fetch.</b> The store keeps whatever was last published — a
    /// transient outage or a rate limit must not blank a good forecast — so a caller pacing this in a
    /// loop needs no error handling of its own, and a bad fetch does not stop later ones.</para>
    ///
    /// <para>A source that only sends a request may return before the answer is published; the answer
    /// then arrives as a publish of its own.</para>
    /// </summary>
    Task RequestRefreshAsync(CancellationToken cancellationToken = default);
}
