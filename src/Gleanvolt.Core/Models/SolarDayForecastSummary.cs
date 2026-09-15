using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// One local calendar day of forecast, reduced to the figures the dashboard's forecast table prints:
/// the median energy, the p10 and p90 bands either side of it, and the strongest period.
/// </summary>
/// <param name="ExpectedWh">The median estimate for the whole day, in watt-hours.</param>
/// <param name="LowWh">
/// The p10 estimate for the whole day, in watt-hours: a figure the day should beat about nine times in
/// ten. A period the provider sent no p10 for counts at its median, as
/// <see cref="SolarForecastPeriod.PowerWatts"/> does, so a partial response can't read as darkness.
/// </param>
/// <param name="HighWh">The p90 estimate for the whole day, in watt-hours, with the same fallback.</param>
/// <param name="PeakWatts">The highest median power of any period in the day, in watts.</param>
public sealed record SolarDayForecastSummary(double ExpectedWh, double LowWh, double HighWh, double PeakWatts)
{
    /// <summary>
    /// Summarises one day's periods. Walks every period once per figure, so it belongs where a forecast
    /// lands, not on a render.
    /// </summary>
    public static SolarDayForecastSummary Of(SolarForecast day)
    {
        ArgumentNullException.ThrowIfNull(day);

        return new SolarDayForecastSummary(
            day.ExpectedEnergyWattHours,
            day.EnergyWattHoursAt(ForecastConfidence.P10),
            day.EnergyWattHoursAt(ForecastConfidence.P90),
            day.PeakPowerWatts);
    }
}
