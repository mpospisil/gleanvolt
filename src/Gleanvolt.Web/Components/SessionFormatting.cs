using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>Shared between the session list and session detail pages, so the two never drift.</summary>
internal static class SessionFormatting
{
    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{duration.Minutes}m";

    public static string FormatStrategy(this ChargingSession session) =>
        session.StartMode == session.EndMode
            ? session.StartMode.ToString()
            : $"{session.StartMode} → {session.EndMode}";

    /// <summary>
    /// One weather reading in a line: what it was like, how warm, and how much cloud. Cloud is the
    /// figure that explains a session, so it is never dropped even when the condition already implies it.
    /// </summary>
    public static string FormatWeather(WeatherObservation weather)
    {
        var condition = string.IsNullOrWhiteSpace(weather.ConditionDescription)
            ? weather.Condition
            : weather.ConditionDescription;

        var text = $"{weather.TemperatureCelsius:F1} °C, {weather.CloudsPercent:F0}% cloud";
        return string.IsNullOrWhiteSpace(condition) ? text : $"{condition}, {text}";
    }

    /// <summary>
    /// The day the session ran on, as the forecast saw it: total expected PV with the p10–p90 band
    /// behind it. The band is the point — it says whether a thin day was always going to be thin.
    /// </summary>
    public static string FormatDayForecast(SolarForecast forecast) =>
        $"{forecast.ExpectedEnergyWattHours / 1000:F1} kWh "
        + $"(p10 {forecast.EnergyWattHoursAt(ForecastConfidence.P10) / 1000:F1} – "
        + $"p90 {forecast.EnergyWattHoursAt(ForecastConfidence.P90) / 1000:F1})";
}
