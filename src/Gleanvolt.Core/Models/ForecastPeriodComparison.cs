namespace Gleanvolt.Core.Models;

/// <summary>
/// One closed forecast period measured against what the panels actually did. Produced by
/// <see cref="Strategies.ForecastAccuracyTracker"/> for the reconciliation log — the record that later
/// explains why the car was paused at 14:00 three weeks ago.
/// </summary>
/// <param name="PeriodStart">Start of the period just closed.</param>
/// <param name="PeriodEnd">End of the period just closed.</param>
/// <param name="ForecastWattHours">What the forecast said this period would produce (median band).</param>
/// <param name="ActualWattHours">What was actually produced over it.</param>
/// <param name="CumulativeForecastWattHours">Forecast energy accumulated since first light today.</param>
/// <param name="CumulativeActualWattHours">Actual energy accumulated since first light today.</param>
/// <param name="BiasFactor">The realised bias after this period closed (cumulative actual ÷ forecast, clamped).</param>
public sealed record ForecastPeriodComparison(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    double ForecastWattHours,
    double ActualWattHours,
    double CumulativeForecastWattHours,
    double CumulativeActualWattHours,
    double BiasFactor)
{
    /// <summary>Actual minus forecast for this period, in watt-hours (positive = over-produced).</summary>
    public double DeltaWattHours => ActualWattHours - ForecastWattHours;

    /// <summary>The delta as a fraction of the forecast, or null when the forecast was ~zero.</summary>
    public double? DeltaFraction => ForecastWattHours > 1 ? DeltaWattHours / ForecastWattHours : null;
}
