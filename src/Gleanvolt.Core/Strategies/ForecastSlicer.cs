using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// One prorated forecast period: the part of a <see cref="SolarForecastPeriod"/> that falls inside the
/// window being planned, with the house's expected draw already taken out of it.
///
/// <para>Mutable on purpose — <see cref="ForecastSlicer.Reserve"/> writes the home battery's booking
/// back into it, and every planner reads what is left through <see cref="AvailableWh"/>.</para>
/// </summary>
public sealed class ForecastSlice
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required double Hours { get; init; }

    /// <summary>PV minus expected house load over this slice, in watts. Negative when the house outruns the roof.</summary>
    public required double SurplusWatts { get; init; }

    public required double PvWh { get; init; }
    public required double HouseWh { get; init; }

    /// <summary>The surplus as energy, floored at zero: a house deficit is the grid's problem, not a negative budget.</summary>
    public required double SurplusWh { get; init; }

    /// <summary>Whether the surplus clears the charger's minimum power — the shoulder/plateau line.</summary>
    public required bool IsPlateau { get; init; }

    /// <summary>How much of this slice's surplus the home battery has booked.</summary>
    public double ReservedWh { get; set; }

    /// <summary>What is left for the car after the battery's booking.</summary>
    public double AvailableWh => Math.Max(0, SurplusWh - ReservedWh);

    /// <summary>The available energy expressed as power over the slice, in watts.</summary>
    public double AvailableWatts => Hours > 0 ? AvailableWh / Hours : 0;

    /// <summary>
    /// Whether the car could actually charge here: the power left after the battery's booking has to
    /// clear <paramref name="minPowerWatts"/>, because the car cannot sip a budget slowly.
    /// </summary>
    public bool IsChargeable(double minPowerWatts) =>
        Hours > 0 && AvailableWh > 0 && AvailableWatts >= Math.Max(0, minPowerWatts);
}

/// <summary>
/// The two pieces of forecast arithmetic every day-planner needs before it can decide anything: cut
/// the forecast down to the window actually being planned, and book the home battery's need against
/// the latest production in it.
///
/// <para>Extracted from <see cref="SolarDayPlanner"/> when <see cref="TargetedChargePlanner"/> turned
/// out to need exactly the same two passes. A second copy would have drifted, and the drift would
/// have been silent: both planners would still have produced plausible numbers.</para>
/// </summary>
public static class ForecastSlicer
{
    /// <summary>
    /// Cuts the forecast into the part of each period that falls inside <c>(from, to]</c>. Periods
    /// straddling either edge are prorated, so a plan built at 12:47 doesn't count the whole
    /// 12:30-13:00 period as still to come.
    /// </summary>
    /// <param name="forecast">The forecast to slice.</param>
    /// <param name="from">Start of the window (exclusive).</param>
    /// <param name="to">End of the window (inclusive).</param>
    /// <param name="houseLoad">Expected household load, asked per slice rather than once for the window.</param>
    /// <param name="biasFactor">Realised forecast bias to scale the forecast by (1.0 = trust it as-is).</param>
    /// <param name="minChargePowerWatts">The charger's minimum viable power, which sets <see cref="ForecastSlice.IsPlateau"/>.</param>
    /// <param name="confidence">Which forecast band to plan on.</param>
    public static List<ForecastSlice> Slice(
        SolarForecast forecast,
        DateTimeOffset from,
        DateTimeOffset to,
        IHouseLoadProfile houseLoad,
        double biasFactor,
        double minChargePowerWatts,
        ForecastConfidence confidence)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(houseLoad);

        var slices = new List<ForecastSlice>();

        foreach (var period in forecast.Periods.OrderBy(p => p.PeriodEnd))
        {
            var start = period.PeriodStart > from ? period.PeriodStart : from;
            var end = period.PeriodEnd < to ? period.PeriodEnd : to;
            var hours = (end - start).TotalHours;
            if (hours <= 0)
            {
                continue;
            }

            var pvWatts = Math.Max(0, period.PowerWatts(confidence) * biasFactor);

            // Per slice, not one figure for the whole day: the house has a shape, and charging the car
            // is decided by what the house will draw *then*, not by what it happens to draw now.
            var houseWatts = Math.Max(0, houseLoad.ExpectedWattsAt(start));
            var surplusWatts = pvWatts - houseWatts;

            slices.Add(new ForecastSlice
            {
                Start = start,
                End = end,
                Hours = hours,
                SurplusWatts = surplusWatts,
                PvWh = pvWatts * hours,
                HouseWh = houseWatts * hours,
                SurplusWh = Math.Max(0, surplusWatts) * hours,
                IsPlateau = surplusWatts >= minChargePowerWatts,
            });
        }

        return slices;
    }

    /// <summary>
    /// <see cref="Slice"/>, with every gap in the window filled by a zero-PV slice so the returned
    /// list covers <c>(from, to]</c> without a hole in it.
    ///
    /// <para>This is what a planner whose window runs past the forecast needs — an overnight target
    /// reaches well beyond Solcast's last period, and the hours after it are not "unplannable", they
    /// are simply dark hours in which the grid can still deliver at full power. Slicing alone would
    /// silently shorten the window and understate what the charger can do in it.</para>
    /// </summary>
    public static List<ForecastSlice> SliceContiguous(
        SolarForecast? forecast,
        DateTimeOffset from,
        DateTimeOffset to,
        IHouseLoadProfile houseLoad,
        double biasFactor,
        double minChargePowerWatts,
        ForecastConfidence confidence)
    {
        ArgumentNullException.ThrowIfNull(houseLoad);

        var sliced = forecast is null || forecast.Periods.Count == 0
            ? []
            : Slice(forecast, from, to, houseLoad, biasFactor, minChargePowerWatts, confidence);

        var filled = new List<ForecastSlice>(sliced.Count + 2);
        var cursor = from;

        foreach (var slice in sliced)
        {
            if (slice.Start > cursor)
            {
                filled.Add(Dark(cursor, slice.Start, houseLoad));
            }

            filled.Add(slice);
            cursor = slice.End;
        }

        if (cursor < to)
        {
            filled.Add(Dark(cursor, to, houseLoad));
        }

        return filled;
    }

    /// <summary>
    /// Books <paramref name="needWh"/> against the <b>latest</b> production first. That is what "100%
    /// by evening, not by lunchtime" means: the battery takes the afternoon shoulder and, only if it
    /// must, the tail of the plateau — leaving the car the earliest chargeable weather, when it is most
    /// likely to be plugged in and when a forecast error still has the rest of the day to correct itself.
    /// </summary>
    public static void Reserve(IReadOnlyList<ForecastSlice> slices, double needWh)
    {
        ArgumentNullException.ThrowIfNull(slices);

        var remaining = needWh;

        for (var i = slices.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var take = Math.Min(remaining, slices[i].SurplusWh);
            slices[i].ReservedWh = take;
            remaining -= take;
        }
    }

    // A stretch the forecast says nothing about: no PV, so no surplus and no plateau. The house load is
    // still recorded, because "what did the plan think the house was doing" is worth being able to read.
    private static ForecastSlice Dark(DateTimeOffset start, DateTimeOffset end, IHouseLoadProfile houseLoad)
    {
        var hours = (end - start).TotalHours;
        var houseWatts = Math.Max(0, houseLoad.ExpectedWattsAt(start));

        return new ForecastSlice
        {
            Start = start,
            End = end,
            Hours = hours,
            SurplusWatts = -houseWatts,
            PvWh = 0,
            HouseWh = houseWatts * hours,
            SurplusWh = 0,
            IsPlateau = false,
        };
    }
}
