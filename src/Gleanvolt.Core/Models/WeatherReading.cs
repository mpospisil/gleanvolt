namespace Gleanvolt.Core.Models;

/// <summary>
/// One fetch from a weather provider: the conditions, plus the day's daylight bounds that came back
/// with them.
///
/// <para>The two are kept apart because they belong to different things. The
/// <see cref="Observation"/> is about a <em>moment</em> and is recorded twice per session — once when
/// it opens, once when it closes — while sunrise and sunset are about the <em>day</em>, are identical
/// in both fetches, and are therefore stored once.</para>
/// </summary>
/// <param name="Sunrise">Sunrise for the site's day, or null when the provider didn't report it.</param>
/// <param name="Sunset">
/// Sunset. With <paramref name="Sunrise"/> it bounds the daylight the roof had to work with, which is
/// what makes an 8 kWh December session comparable with an 8 kWh June one — and what says whether a
/// session ran out of sun or merely out of surplus.
/// </param>
public sealed record WeatherReading(
    WeatherObservation Observation,
    DateTimeOffset? Sunrise,
    DateTimeOffset? Sunset);
