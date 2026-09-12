namespace Gleanvolt.Core.Models;

/// <summary>
/// What the rest of today's forecast says about the one question the
/// <see cref="Enums.ChargeControlMode.SolarGrid"/> mode asks of it: is there still sun worth waiting
/// for? Built every poll by <see cref="Strategies.SolarGridOutlookBuilder"/>.
///
/// <para>Deliberately not a plan. The controller decides nothing from it while real sun is shining —
/// a live surplus above the minimum charges the car whatever the forecast thinks. The outlook only
/// settles what an idle car does: wait for sun still to come, or give up for the day.</para>
/// </summary>
/// <param name="MinSurplusWatts">
/// The minimum the forecast was measured against. Carried here rather than read again by the
/// controller, so the forecast verdict and the live gate cannot be judged against two different
/// figures in one cycle when the owner moves the number mid-poll.
/// </param>
/// <param name="Timestamp">When the outlook was built.</param>
/// <param name="ForecastUsable">
/// Whether the forecast could answer at all. False when none has been fetched, or when it is stale and
/// still shows sun to come — an old forecast is no basis for ending anything, so the mode then runs on
/// live surplus alone and never ends itself on the forecast's word.
/// </param>
/// <param name="NextSunAt">
/// The start of the first remaining stretch of today whose forecast surplus clears the minimum —
/// <see cref="Timestamp"/> itself when the current period does. Null when there is none.
/// </param>
/// <param name="SunUntil">The end of the last such stretch today. Null when there is none.</param>
/// <param name="Reason">One line saying what the forecast was read to mean, for the log and the UI.</param>
public sealed record SolarGridOutlook(
    double MinSurplusWatts,
    DateTimeOffset Timestamp,
    bool ForecastUsable,
    DateTimeOffset? NextSunAt,
    DateTimeOffset? SunUntil,
    string Reason)
{
    /// <summary>
    /// The forecast is usable and nothing left of today clears the minimum: the mode's cue to end
    /// itself once the live surplus agrees.
    /// </summary>
    public bool NoSunLeftToday => ForecastUsable && SunUntil is null;

    /// <summary>An outlook with no forecast behind it: the mode waits on live surplus and never ends itself on it.</summary>
    public static SolarGridOutlook Unavailable(double minSurplusWatts, DateTimeOffset timestamp, string reason) =>
        new(minSurplusWatts, timestamp, ForecastUsable: false, NextSunAt: null, SunUntil: null, reason);
}
