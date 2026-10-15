using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>
/// A <see cref="SolarDayPlan"/> in the one sentence it is worth. Shared by the Forecasted tab and the
/// overview's Today's plan card (#255), so the two can never word the same plan differently.
/// </summary>
public static class SolarDayPlanNarrative
{
    /// <summary>
    /// What the mode is entitled to say: a forecast of what today gives the car and when, never a
    /// verdict on whether that is enough. The tiles on the Forecasted tab carry the same two figures
    /// apart -- budget there, window here -- and nobody reads two tiles to answer "is it worth plugging
    /// in this morning?".
    /// </summary>
    /// <param name="plan">The plan to describe.</param>
    /// <param name="zone">The zone the window is rendered in -- the site's, never the browser's.</param>
    public static string Headline(SolarDayPlan plan, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(zone);

        if (!plan.IsUsable)
        {
            return "No usable forecast, so no plan for today; the mode is charging on live surplus instead.";
        }

        if (plan.NextFeasibleWindow is not { } window || plan.FeasibleEvEnergyWh <= 0)
        {
            return "Today has no chargeable window: the sun never clears the charger's floor for long enough, "
                + "or the home battery needs all of it. The car waits.";
        }

        // "from 10:20 to 15:40" rather than the tile's "10:20–15:40": this is a sentence, and a dash
        // reads as a range inside one.
        var start = TimeZoneInfo.ConvertTime(window.Start, zone);
        var end = TimeZoneInfo.ConvertTime(window.End, zone);

        return $"Today gives the car about {plan.FeasibleEvEnergyWh / 1000:F1} kWh, in a window from {start:HH:mm} to {end:HH:mm}.";
    }
}
