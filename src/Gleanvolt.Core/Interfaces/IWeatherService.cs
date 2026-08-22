using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Current weather for the configured site, fetched on demand.
///
/// <para>Unlike <see cref="ISolarForecastService"/>, this has no cache and no refresh worker: it is
/// asked exactly twice per charging session — when one opens and when it closes — which is a handful
/// of calls a day. A background refresh would spend the provider's quota on hours nothing is
/// recording.</para>
///
/// <para>Callers must never be on a control path when they await this. Nothing in charge control reads
/// weather at all; the session recorder does, on its own loop.</para>
/// </summary>
public interface IWeatherService
{
    /// <summary>
    /// Whether a provider is configured at all. False means every call returns null without touching
    /// the network — the feature is simply off, and a session recorded without weather is the correct
    /// outcome rather than a failure.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The current conditions, or <c>null</c> when the service is unconfigured or the fetch failed for
    /// any reason — a bad key, a rate limit, a timeout, an outage.
    ///
    /// <para><b>Never throws for a provider-side problem.</b> This is decoration on a record that must
    /// be written whatever the weather API is doing, so the failure mode is a missing figure, not a
    /// lost session.</para>
    /// </summary>
    Task<WeatherReading?> GetCurrentAsync(CancellationToken cancellationToken = default);
}
