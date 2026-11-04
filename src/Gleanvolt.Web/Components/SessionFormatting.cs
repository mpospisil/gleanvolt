using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Resources;

namespace Gleanvolt.Web.Components;

/// <summary>Shared between the session list and session detail pages, so the two never drift.</summary>
internal static class SessionFormatting
{
    public static string FormatStrategy(this ChargingSession session) =>
        session.StartMode == session.EndMode
            ? ChargeModeTabs.Label(session.StartMode)
            : Localized.Format(SharedText.ModeChange, ChargeModeTabs.Label(session.StartMode), ChargeModeTabs.Label(session.EndMode));

    /// <summary>
    /// One weather reading in a line: what it was like, how warm, and how much cloud. Cloud is the
    /// figure that explains a session, so it is never dropped even when the condition already implies it.
    /// </summary>
    public static string FormatWeather(WeatherObservation weather)
    {
        var condition = string.IsNullOrWhiteSpace(weather.ConditionDescription)
            ? weather.Condition
            : weather.ConditionDescription;

        // The condition is the weather provider's own words, and stays in its language.
        var temperature = Units.Temperature(weather.TemperatureCelsius);
        var clouds = Units.Percent(weather.CloudsPercent);
        return string.IsNullOrWhiteSpace(condition)
            ? Localized.Format(SharedText.WeatherLine, temperature, clouds)
            : Localized.Format(SharedText.WeatherLineWithCondition, condition, temperature, clouds);
    }

    /// <summary>
    /// The day the session ran on, as the forecast saw it: total expected PV with the p10–p90 band
    /// behind it. The band is the point — it says whether a thin day was always going to be thin.
    /// </summary>
    public static string FormatDayForecast(SolarForecast forecast) =>
        Localized.Format(
            SharedText.DayForecastLine,
            Units.Kwh(forecast.ExpectedEnergyWattHours),
            Units.Kwh(forecast.EnergyWattHoursAt(ForecastConfidence.P10), unit: false),
            Units.Kwh(forecast.EnergyWattHoursAt(ForecastConfidence.P90), unit: false));
}
