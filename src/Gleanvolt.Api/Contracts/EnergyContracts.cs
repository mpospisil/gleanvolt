using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Contracts;

/// <summary>
/// One window of the site's energy history — by default a quarter hour — recorded whether or not
/// anything was charging.
///
/// <para>Every figure is in kilowatt-hours over the window, and none of them are signed: import and
/// export, charge and discharge, are separate columns rather than one number that changes sign.</para>
/// </summary>
/// <param name="PeriodStart">Start of the window, inclusive.</param>
/// <param name="PeriodEnd">End of the window, exclusive.</param>
/// <param name="LocalDate">The local day this window belongs to, in the site's own zone.</param>
/// <param name="TimeZoneId">The zone that local day was computed in.</param>
/// <param name="SolarKwh">What the roof produced.</param>
/// <param name="ForecastSolarKwh">What the forecast said it would produce, or null when no forecast covered the window.</param>
/// <param name="GridImportKwh">Energy drawn from the grid. Export is not subtracted from it.</param>
/// <param name="GridExportKwh">Energy pushed to the grid, as a positive number.</param>
/// <param name="EvKwh">Energy delivered to the car, measured at the charger.</param>
/// <param name="BatteryChargeKwh">Energy into the home battery.</param>
/// <param name="BatteryDischargeKwh">Energy out of the home battery, as a positive number.</param>
/// <param name="HouseLoadKwh">Everything the house consumed, the car included — the residual of the energy balance.</param>
/// <param name="OtherLoadsKwh">House consumption with the car taken out.</param>
/// <param name="SocStartPercent">Home battery SOC at the start of the window.</param>
/// <param name="SocEndPercent">SOC at the last reading inside it.</param>
/// <param name="SocMinPercent">Lowest SOC observed.</param>
/// <param name="SocMaxPercent">Highest SOC observed.</param>
/// <param name="SocMeanPercent">Time-weighted mean SOC.</param>
/// <param name="Coverage">
/// How much of the window was actually observed, 0 to 1. Read this before trusting a row. Below
/// 1 the service was starting, stopping or had lost the inverter, and every energy figure above is
/// short by the same fraction — a restart at 09:07 is not the sun going out for seven minutes.
/// </param>
/// <param name="SampleCount">How many poll snapshots fed the window. Diagnostic only.</param>
public sealed record EnergyIntervalResponse(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateOnly LocalDate,
    string TimeZoneId,
    double SolarKwh,
    double? ForecastSolarKwh,
    double GridImportKwh,
    double GridExportKwh,
    double EvKwh,
    double BatteryChargeKwh,
    double BatteryDischargeKwh,
    double HouseLoadKwh,
    double OtherLoadsKwh,
    double SocStartPercent,
    double SocEndPercent,
    double SocMinPercent,
    double SocMaxPercent,
    double SocMeanPercent,
    double Coverage,
    int SampleCount)
{
    internal static EnergyIntervalResponse From(EnergyInterval interval) => new(
        interval.PeriodStart,
        interval.PeriodEnd,
        interval.LocalDate,
        interval.TimeZoneId,
        interval.SolarKwh,
        interval.ForecastSolarKwh,
        interval.GridImportKwh,
        interval.GridExportKwh,
        interval.EvKwh,
        interval.BatteryChargeKwh,
        interval.BatteryDischargeKwh,
        interval.HouseLoadKwh,
        interval.OtherLoadsKwh,
        interval.SocStartPercent,
        interval.SocEndPercent,
        interval.SocMinPercent,
        interval.SocMaxPercent,
        interval.SocMeanPercent,
        interval.Coverage,
        interval.SampleCount);
}

/// <summary>The interval series for a requested range, oldest first.</summary>
/// <param name="From">The start of the range that was queried, inclusive.</param>
/// <param name="To">The end of the range that was queried, exclusive.</param>
/// <param name="Count">How many windows came back.</param>
/// <param name="Intervals">The windows themselves.</param>
public sealed record EnergySeriesResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    int Count,
    IReadOnlyList<EnergyIntervalResponse> Intervals);

/// <summary>
/// A whole local day added up, so "how was Tuesday?" does not mean fetching ninety-six rows and summing
/// them. The same columns as an interval, over a day measured in the site's own zone — so the
/// clocks-change days are the 23 and 25 hours they really are.
/// </summary>
/// <param name="Date">The local day.</param>
/// <param name="TimeZoneId">The zone it was measured in.</param>
/// <param name="From">The instant the day started.</param>
/// <param name="To">The instant it ended.</param>
/// <param name="SolarKwh">What the roof produced.</param>
/// <param name="ForecastSolarKwh">What the forecast said it would, summed over the windows that had one.</param>
/// <param name="GridImportKwh">Energy drawn from the grid.</param>
/// <param name="GridExportKwh">Energy pushed to the grid.</param>
/// <param name="EvKwh">Energy delivered to the car.</param>
/// <param name="BatteryChargeKwh">Energy into the home battery.</param>
/// <param name="BatteryDischargeKwh">Energy out of the home battery.</param>
/// <param name="HouseLoadKwh">Everything the house consumed, the car included.</param>
/// <param name="OtherLoadsKwh">House consumption with the car taken out.</param>
/// <param name="SocStartPercent">Home battery SOC at the first window of the day, or null when the day has no rows.</param>
/// <param name="SocEndPercent">SOC at the last window, or null when the day has no rows.</param>
/// <param name="SocMinPercent">Lowest SOC observed across the day.</param>
/// <param name="SocMaxPercent">Highest SOC observed across the day.</param>
/// <param name="IntervalCount">How many windows were recorded. A complete quarter-hourly day is 96.</param>
/// <param name="Coverage">
/// The mean observed fraction across the day's windows, 0 to 1. Below 1 the totals above are short by
/// roughly that much — the service was not running for all of it.
/// </param>
public sealed record EnergyDayResponse(
    DateOnly Date,
    string TimeZoneId,
    DateTimeOffset From,
    DateTimeOffset To,
    double SolarKwh,
    double? ForecastSolarKwh,
    double GridImportKwh,
    double GridExportKwh,
    double EvKwh,
    double BatteryChargeKwh,
    double BatteryDischargeKwh,
    double HouseLoadKwh,
    double OtherLoadsKwh,
    double? SocStartPercent,
    double? SocEndPercent,
    double? SocMinPercent,
    double? SocMaxPercent,
    int IntervalCount,
    double Coverage)
{
    internal static EnergyDayResponse Aggregate(
        DateOnly date,
        string timeZoneId,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<EnergyInterval> intervals)
    {
        var ordered = intervals.OrderBy(i => i.PeriodStart).ToList();

        return new EnergyDayResponse(
            date,
            timeZoneId,
            from,
            to,
            ordered.Sum(i => i.SolarKwh),
            // Null rather than zero when nothing forecast the day at all: a summed zero would read as
            // "the forecast expected darkness", which is a different claim from "there was no forecast".
            ordered.Any(i => i.ForecastSolarKwh is not null) ? ordered.Sum(i => i.ForecastSolarKwh ?? 0) : null,
            ordered.Sum(i => i.GridImportKwh),
            ordered.Sum(i => i.GridExportKwh),
            ordered.Sum(i => i.EvKwh),
            ordered.Sum(i => i.BatteryChargeKwh),
            ordered.Sum(i => i.BatteryDischargeKwh),
            ordered.Sum(i => i.HouseLoadKwh),
            ordered.Sum(i => i.OtherLoadsKwh),
            ordered.Count > 0 ? ordered[0].SocStartPercent : null,
            ordered.Count > 0 ? ordered[^1].SocEndPercent : null,
            ordered.Count > 0 ? ordered.Min(i => i.SocMinPercent) : null,
            ordered.Count > 0 ? ordered.Max(i => i.SocMaxPercent) : null,
            ordered.Count,
            ordered.Count > 0 ? ordered.Average(i => i.Coverage) : 0);
    }
}
