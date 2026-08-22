using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// One recorded charging session: the header row that a session's samples and events hang off.
///
/// <para><b>Which "session" this is.</b> The word is already used in this codebase for the span since
/// the car was plugged in — that is what <see cref="ChargeControlStatus.SessionEnergyWh"/> and the
/// forecast mode's session energy target mean. This type is the <em>controlled</em> span: it opens
/// when a controlling mode is driving a connected car and closes when that stops being true, so one
/// plugged-in car can produce several of these (or none at all).</para>
///
/// <para>A session is mutable exactly once — while open, <see cref="EndedAt"/> and the totals are
/// unset — and is immutable afterwards. That is what makes a closed session safe to publish as a
/// single document to object storage without any later reconciliation.</para>
/// </summary>
/// <param name="Id">
/// A time-ordered UUIDv7 generated locally. Globally unique without a server, sorts chronologically as
/// an object key, and indexes well — chosen so the eventual cloud upload needs no id remapping.
/// </param>
/// <param name="StartedAt">When the session opened (UTC offset preserved).</param>
/// <param name="EndedAt">When it closed, or null while it is still open.</param>
/// <param name="TimeZoneId">
/// The IANA zone the controller was running in, stored alongside the absolute timestamps so a viewer
/// can bucket sessions by <em>local</em> day without guessing — "Tuesday evening" is the question
/// people actually ask, and it isn't answerable from a UTC instant alone.
/// </param>
/// <param name="StartMode">The mode that opened the session.</param>
/// <param name="EndMode">The mode in force when it closed (equal to <paramref name="StartMode"/> unless it was switched).</param>
/// <param name="EndReason">Why it closed, or null while open.</param>
/// <param name="StartBatterySocPercent">Home battery SOC when the session opened.</param>
/// <param name="EndBatterySocPercent">Home battery SOC when it closed, or null while open.</param>
/// <param name="EnergyDeliveredWh">Total energy delivered to the car during the session.</param>
/// <param name="FromSolarWh">Of that, attributed to surplus PV — see <see cref="Strategies.ChargingSourceAttribution"/>.</param>
/// <param name="FromGridWh">Of that, attributed to grid import.</param>
/// <param name="FromBatteryWh">
/// Of that, attributed to home-battery discharge. This is <em>measured</em>, and is deliberately not
/// the same number as <paramref name="LoanedWh"/>.
/// </param>
/// <param name="LoanedWh">
/// What the forecast-driven mode <em>commanded</em> the battery to lend. Kept apart from
/// <paramref name="FromBatteryWh"/> because the two answer different questions — what we decided
/// versus what the hardware actually did — and the gap between them is worth being able to see.
/// </param>
/// <param name="PeakChargingPowerWatts">The highest charger draw observed during the session.</param>
/// <param name="StartPlan">
/// The forecast-driven day plan as it stood when the session opened, so the outcome can be read
/// against what was predicted. Null when the opening mode wasn't <see cref="ChargeControlMode.Forecasted"/>,
/// since the poll loop only publishes a plan for that mode.
/// </param>
/// <param name="ForecastRemainingAtStartWh">Forecast PV still to come today when the session opened, if a forecast was available.</param>
/// <param name="DayForecast">
/// The <b>whole day's</b> forecast curve — every period of the local day the session started on,
/// including the hours before it opened and the hours after it closed, with the p10/p90 bands each
/// period carried.
///
/// <para>Carried on the session so the document is self-contained: a session read a year from now can
/// be put against the day it happened on without the reader needing a forecast archive of their own.
/// <see cref="ForecastRemainingAtStartWh"/> answers "how much sun was left?" in one number; this is
/// the shape behind it, and the only thing that can say whether a poor session met a poor day or a
/// good one.</para>
///
/// <para>Written when the session opens and rewritten when it closes, so what is stored is the fullest
/// version of the day known by then — the provider only forecasts forward, so the curve grows a
/// morning as the day goes on. Null when no forecast was ever fetched, and short of a full day when
/// the service was not running for all of it.</para>
/// </param>
/// <param name="WeatherAtStart">
/// The weather when the session opened, or null when no weather provider is configured (the default)
/// or the fetch failed. Recorded because the day's forecast curve says what the sun was <em>expected</em>
/// to do and nothing said what the sky actually did.
/// </param>
/// <param name="WeatherAtEnd">
/// The weather when it closed. Null while the session is open, and null when that fetch failed — which
/// is not an error.
///
/// <para>Two readings rather than one because a six-hour session can finish in entirely different
/// weather from the one it started in, and only the <em>pair</em> can say so. The same reason
/// <paramref name="StartBatterySocPercent"/> has an end alongside it.</para>
/// </param>
/// <param name="Sunrise">
/// Sunrise on the day the session started, from the opening weather reading. Null when no weather was
/// recorded.
/// </param>
/// <param name="Sunset">
/// Sunset on that day. With <paramref name="Sunrise"/> it bounds the daylight the roof had to work
/// with — what makes an 8 kWh December session comparable with an 8 kWh June one, and what says
/// whether a session ran out of sun or merely out of surplus.
///
/// <para>Stored once rather than on each reading: they belong to the day, not to the moment. A session
/// running across midnight carries the <em>starting</em> day's pair, the same rule
/// <paramref name="StartedAt"/> and <paramref name="DayForecast"/> already follow.</para>
/// </param>
/// <param name="Controlled">
/// False for a session recorded while no mode was driving the charger (the opt-in
/// <c>RecordUncontrolledSessions</c> baseline). Always true otherwise.
/// </param>
public sealed record ChargingSession(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string TimeZoneId,
    ChargeControlMode StartMode,
    ChargeControlMode EndMode,
    ChargingSessionEndReason? EndReason,
    double StartBatterySocPercent,
    double? EndBatterySocPercent,
    double EnergyDeliveredWh,
    double FromSolarWh,
    double FromGridWh,
    double FromBatteryWh,
    double LoanedWh,
    double PeakChargingPowerWatts,
    SolarDayPlan? StartPlan,
    double? ForecastRemainingAtStartWh,
    SolarForecast? DayForecast,
    WeatherObservation? WeatherAtStart,
    WeatherObservation? WeatherAtEnd,
    DateTimeOffset? Sunrise,
    DateTimeOffset? Sunset,
    bool Controlled)
{
    /// <summary>
    /// How long the sun was up on the day the session started, or null when no weather was recorded.
    /// The denominator behind "did this session use the day it had?".
    /// </summary>
    public TimeSpan? DaylightLength => Sunrise is { } rise && Sunset is { } set && set > rise ? set - rise : null;

    /// <summary>Whether the session is still running.</summary>
    public bool IsOpen => EndedAt is null;

    /// <summary>How long the session ran, or has been running so far when measured against <paramref name="now"/>.</summary>
    public TimeSpan Duration(DateTimeOffset now) => (EndedAt ?? now) - StartedAt;

    /// <summary>
    /// The share of the delivered energy that came from surplus PV, 0–1, or null when nothing was
    /// delivered. The headline number for "was this a good session?".
    /// </summary>
    public double? SolarFraction => EnergyDeliveredWh > 0 ? FromSolarWh / EnergyDeliveredWh : null;
}
