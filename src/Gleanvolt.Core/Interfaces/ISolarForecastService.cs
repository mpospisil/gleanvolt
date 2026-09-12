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

    /// <summary>
    /// The whole of one local calendar day, <b>elapsed periods included</b>, or <c>null</c> when
    /// nothing is held for that day.
    ///
    /// <para>Deliberately not the same thing as <see cref="GetForecastForToday"/>. Providers answer
    /// "what is still to come", so by the afternoon the live forecast can no longer say what the
    /// morning was predicted to bring; an implementation serves this from its own retained history
    /// instead. It is the analysis surface — what a finished session is read against — and nothing in
    /// charge control decides on it.</para>
    ///
    /// <para>Necessarily incomplete for a day the service was not running through: only periods it
    /// actually saw a forecast for are retained.</para>
    /// </summary>
    SolarForecast? GetDayForecast(DateOnly localDate);

    /// <summary>
    /// The forecast energy for one whole local calendar day in watt-hours — the total of
    /// <see cref="GetDayForecast"/>, elapsed periods included — or <c>null</c> when nothing is held for
    /// that day.
    ///
    /// <para><b>Cheap enough for a render.</b> The dashboard asks on every poll, and the answer changes
    /// only when a refresh lands, so an implementation that holds a forecast works the totals out then
    /// and serves them from there. The default sums the day's periods on each call: the same answer, and
    /// fine for a stand-in that holds nothing worth caching, but not what a real source should do.</para>
    /// </summary>
    double? GetDayEnergyWattHours(DateOnly localDate) => GetDayForecast(localDate)?.ExpectedEnergyWattHours;
}
