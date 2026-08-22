namespace Gleanvolt.Core.Models;

/// <summary>
/// The weather at one instant, as a provider reported it.
///
/// <para><b>Why a session needs this.</b> The forecast curve on a session says what the sun was
/// <em>expected</em> to do (see <see cref="ChargingSession.DayForecast"/>). Nothing said what the day
/// was actually like — and cloud that rolled in, a cold morning, and snow on the panels all look
/// identical afterwards in a record that has only PV watts in it.</para>
///
/// <para>Purely descriptive: nothing in charge control reads any of this, and it is recorded twice per
/// session rather than sampled, so it is a note in the margin rather than a series.</para>
/// </summary>
/// <param name="ObservedAt">
/// When the <em>provider</em> stamped the reading, not when we asked for it. The same argument as
/// <see cref="ChargingSessionSample.VehicleSocCapturedAt"/>: a reading without its own timestamp cannot
/// be told apart from a live one, and these are typically a few minutes old.
/// </param>
/// <param name="TemperatureCelsius">Air temperature, °C.</param>
/// <param name="PressureHpa">Atmospheric pressure at station level, hPa.</param>
/// <param name="HumidityPercent">Relative humidity, %.</param>
/// <param name="CloudsPercent">
/// Cloud cover, %. The single most useful figure here: against the day's forecast curve it separates
/// "the forecast was wrong" from "the weather was not what was forecast".
/// </param>
/// <param name="VisibilityMetres">
/// Visibility in metres, capped by the provider at 10 km. Null when it wasn't reported — fog and heavy
/// precipitation are what move it, so a missing value must not read as a clear 0.
/// </param>
/// <param name="Condition">The provider's short group for the conditions, e.g. <c>Clear</c>, <c>Rain</c>, <c>Snow</c>.</param>
/// <param name="ConditionDescription">Its longer form, e.g. <c>light rain</c>, <c>broken clouds</c>.</param>
public sealed record WeatherObservation(
    DateTimeOffset ObservedAt,
    double TemperatureCelsius,
    double PressureHpa,
    double HumidityPercent,
    double CloudsPercent,
    double? VisibilityMetres,
    string Condition,
    string ConditionDescription);
