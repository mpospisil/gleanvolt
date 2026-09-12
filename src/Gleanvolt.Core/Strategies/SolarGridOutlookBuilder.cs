using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Reads the rest of today's forecast for the <see cref="ChargeControlMode.SolarGrid"/> mode: which
/// stretches of it are forecast to give a surplus — PV minus the house's expected draw, the same
/// quantity the live gate measures — at or above the owner's minimum.
///
/// <para>Pure, and built on <see cref="ForecastSlicer.Slice"/> so the forecast surplus here and the one
/// the day planners use are the same arithmetic over the same house-load profile.</para>
/// </summary>
public static class SolarGridOutlookBuilder
{
    /// <param name="forecast">Today's forecast, or null when none has been fetched.</param>
    /// <param name="now">The current instant; only periods after it are read.</param>
    /// <param name="endOfDay">Local midnight: "the rest of the day" ends here.</param>
    /// <param name="houseLoad">The house's expected draw, taken out of the forecast PV per period.</param>
    /// <param name="minSurplusWatts">The owner's minimum surplus.</param>
    /// <param name="confidence">Which forecast band to read.</param>
    /// <param name="staleAfter">How old a forecast may be before it is not believed about sun still to come.</param>
    public static SolarGridOutlook Build(
        SolarForecast? forecast,
        DateTimeOffset now,
        DateTimeOffset endOfDay,
        IHouseLoadProfile houseLoad,
        double minSurplusWatts,
        ForecastConfidence confidence,
        TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(houseLoad);

        var minimum = Math.Max(0, minSurplusWatts);

        if (forecast is null || forecast.Periods.Count == 0)
        {
            return SolarGridOutlook.Unavailable(minimum, now, "no forecast fetched yet");
        }

        var age = now - forecast.RetrievedAt;
        if (age > staleAfter)
        {
            // Staleness is about sun still to come, and an evening is exactly when the forecast stops
            // being refreshed: there is nothing left worth spending a provider call on. A forecast from
            // 15:00 that shows no PV at all for the rest of the day -- not even in the optimistic band --
            // is as right at 21:00 as it was when it was fetched. Without this the mode could never
            // end itself after dark, which is the one time it is sure to be right.
            if (RemainingPvWh(forecast, now, endOfDay, houseLoad) <= 0)
            {
                return new SolarGridOutlook(
                    minimum, now, ForecastUsable: true, NextSunAt: null, SunUntil: null,
                    $"no PV left in today's forecast (fetched {age.TotalHours:F1}h ago)");
            }

            return SolarGridOutlook.Unavailable(
                minimum, now, $"forecast is {age.TotalHours:F1}h old (stale after {staleAfter.TotalHours:F0}h)");
        }

        // Positive as well as at-or-above: with a minimum of zero, a dark period over an idle house
        // would otherwise count as sun.
        var useful = ForecastSlicer
            .Slice(forecast, now, endOfDay, houseLoad, biasFactor: 1.0, minChargePowerWatts: minimum, confidence)
            .Where(slice => slice.SurplusWatts > 0 && slice.SurplusWatts >= minimum)
            .ToList();

        if (useful.Count == 0)
        {
            return new SolarGridOutlook(
                minimum, now, ForecastUsable: true, NextSunAt: null, SunUntil: null,
                $"no forecast period left today clears {minimum:F0}W of surplus");
        }

        var from = useful[0].Start;
        var until = useful[^1].End;

        return new SolarGridOutlook(
            minimum, now, ForecastUsable: true, from, until,
            $"forecast surplus clears {minimum:F0}W from {from.LocalDateTime:HH:mm} until {until.LocalDateTime:HH:mm}");
    }

    // The optimistic band, on purpose: "the sun has set" is only safe to conclude from an old forecast
    // when even the best case it offered has nothing left.
    private static double RemainingPvWh(
        SolarForecast forecast, DateTimeOffset now, DateTimeOffset endOfDay, IHouseLoadProfile houseLoad) =>
        ForecastSlicer
            .Slice(forecast, now, endOfDay, houseLoad, biasFactor: 1.0, minChargePowerWatts: 0, ForecastConfidence.P90)
            .Sum(slice => slice.PvWh);
}
