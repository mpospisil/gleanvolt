using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// A single forecast interval from a solar-forecast provider: the estimated PV power over a
/// fixed-length window ending at <see cref="PeriodEnd"/>.
/// </summary>
/// <param name="PeriodEnd">
/// The (exclusive) end of the window this estimate covers. Providers such as Solcast timestamp
/// each estimate by its period end in UTC; the window the estimate applies to is
/// <c>(PeriodEnd - Period, PeriodEnd]</c>.
/// </param>
/// <param name="Period">The length of the window, e.g. 30 minutes.</param>
/// <param name="EstimatedPowerWatts">
/// The average PV production expected over the window, in watts. Kept in watts to match the rest
/// of the codebase's <c>...Watts</c> convention (providers typically report kilowatts).
/// </param>
/// <param name="EstimatedPowerWattsP10">
/// The 10th-percentile ("almost certainly at least this much") estimate for the window, or null when
/// the provider didn't supply one. The day plan's guarantees are built on this band, not the median.
/// </param>
/// <param name="EstimatedPowerWattsP90">The 90th-percentile estimate, or null when not supplied.</param>
public sealed record SolarForecastPeriod(
    DateTimeOffset PeriodEnd,
    TimeSpan Period,
    double EstimatedPowerWatts,
    double? EstimatedPowerWattsP10 = null,
    double? EstimatedPowerWattsP90 = null)
{
    /// <summary>The (exclusive) start of the window this estimate covers.</summary>
    public DateTimeOffset PeriodStart => PeriodEnd - Period;

    /// <summary>
    /// The estimate for the requested confidence band, falling back to the median whenever the
    /// provider omitted that band — a missing p10 must not silently become a zero-power period, which
    /// would read as "no sun" and starve the car for the whole day.
    /// </summary>
    public double PowerWatts(ForecastConfidence confidence) => confidence switch
    {
        ForecastConfidence.P10 => EstimatedPowerWattsP10 ?? EstimatedPowerWatts,
        ForecastConfidence.P90 => EstimatedPowerWattsP90 ?? EstimatedPowerWatts,
        _ => EstimatedPowerWatts,
    };

    /// <summary>The energy this period represents at the requested confidence band, in watt-hours.</summary>
    public double EnergyWattHoursAt(ForecastConfidence confidence) => PowerWatts(confidence) * Period.TotalHours;

    /// <summary>
    /// Whether the given instant falls within this period's window <c>(PeriodStart, PeriodEnd]</c>.
    /// </summary>
    public bool Covers(DateTimeOffset instant) => instant > PeriodStart && instant <= PeriodEnd;

    /// <summary>The energy this period represents (average power over its duration), in watt-hours.</summary>
    public double EnergyWattHours => EstimatedPowerWatts * Period.TotalHours;
}
