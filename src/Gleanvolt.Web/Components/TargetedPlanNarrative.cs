using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>
/// A <see cref="TargetedChargePlan"/> in words: what will happen, when, and why it is arranged that
/// way. The plan carries a dozen numbers and a list of blocks; almost none of that answers the
/// question the owner is actually asking at 22:00, which is "is my car going to be ready, and why is
/// nothing happening yet?".
///
/// <para>Pure and separate from the page on purpose — the wording is the part worth testing, and a
/// rendered component is a poor place to test a sentence.</para>
/// </summary>
public static class TargetedPlanNarrative
{
    /// <summary>
    /// The plan as a short series of paragraphs, in the order they should be read.
    /// </summary>
    /// <param name="plan">The plan to describe.</param>
    /// <param name="zone">The zone every time is rendered in — the site's, never the browser's.</param>
    public static IReadOnlyList<string> Describe(TargetedChargePlan plan, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(zone);

        var paragraphs = new List<string>();

        if (plan.Strategy == TargetedChargeStrategy.Complete)
        {
            paragraphs.Add(
                $"Target met: {Kwh(plan.DeliveredEnergyWh)} of the {Kwh(plan.RequiredEnergyWh)} asked for has "
                + $"reached the car. The charger has been released.");

            return paragraphs;
        }

        var headline = $"{Kwh(plan.RemainingEnergyWh)} by {At(plan.DepartBy, plan.Now, zone)}.";
        if (plan.DeliveredEnergyWh > 0)
        {
            headline += $" {Kwh(plan.DeliveredEnergyWh)} of the {Kwh(plan.RequiredEnergyWh)} asked for is already in the car.";
        }

        switch (plan.Strategy)
        {
            case TargetedChargeStrategy.Maximum:
                paragraphs.Add(
                    $"{Kwh(plan.RemainingEnergyWh)} by {At(plan.DepartBy, plan.Now, zone)} is more than the "
                    + $"{Duration(plan.TimeRemaining)} left can deliver. Charging flat out from now, the car will have "
                    + $"about {Kwh(plan.ExpectedEnergyWh)} — {Kwh(plan.ShortfallWh)} short."
                    + (plan.FeasibleDeparture is { } later
                        ? $" Leaving at {At(later, plan.Now, zone)} instead would cover the full amount."
                        : string.Empty));

                paragraphs.Add(
                    "The home battery is held out of it while the grid tops the car up, so the pack is not "
                    + "emptied into a charge that was already going to fall short.");
                break;

            case TargetedChargeStrategy.Solar:
                paragraphs.Add(headline);
                paragraphs.Add(
                    $"The forecast covers all of it: {Kwh(plan.SolarEnergyWh)} from surplus "
                    + $"{Between(plan, zone)}, and no grid import is planned at all.");
                break;

            default:
                paragraphs.Add(headline);
                paragraphs.Add(SolarPlusGrid(plan, zone));
                paragraphs.Add(
                    "The home battery keeps its priority; the discharge hold arms while the grid top-up runs "
                    + "so the pack never feeds the car. A sunnier afternoon than forecast will shrink the grid "
                    + "share before it is drawn.");
                break;
        }

        if (!plan.IsUsable)
        {
            paragraphs.Add(
                "There is no usable forecast for this window, so the plan assumes the grid will supply all of "
                + "it. Any surplus that does appear is used first, and the plan is rebuilt on the next poll.");
        }

        return paragraphs;
    }

    private static string SolarPlusGrid(TargetedChargePlan plan, TimeZoneInfo zone)
    {
        var grid = plan.GridStart is { } start
            ? $"{Kwh(plan.GridEnergyWh)} from the grid, starting {At(start, plan.Now, zone)}"
            : $"{Kwh(plan.GridEnergyWh)} from the grid";

        return plan.SolarEnergyWh > 0
            ? $"There is time to wait for the sun: {Kwh(plan.SolarEnergyWh)} should come from forecast surplus "
                + $"{Between(plan, zone)}, and {grid}."
            : $"No usable surplus is forecast before then, so the whole {grid} — placed as late as it can be and "
                + "still finish in time.";
    }

    private static string Between(TargetedChargePlan plan, TimeZoneInfo zone) =>
        plan.SolarStart is { } from && plan.SolarEnd is { } to
            ? $"between {At(from, plan.Now, zone)} and {At(to, plan.Now, zone)}"
            : "later today";

    /// <summary>
    /// A time the way somebody standing in the kitchen would say it: the clock alone today, "tomorrow"
    /// once it crosses midnight, and the weekday beyond that. The distinction is the whole point on
    /// this page — "04:35" and "04:35 tomorrow" are not the same promise.
    /// </summary>
    private static string At(DateTimeOffset instant, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var days = (local.Date - TimeZoneInfo.ConvertTime(now, zone).Date).Days;

        return days switch
        {
            <= 0 => $"{local:HH:mm}",
            1 => $"{local:HH:mm} tomorrow",
            _ => $"{local:ddd HH:mm}",
        };
    }

    private static string Duration(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "no time",
        { TotalHours: < 1 } => $"{span.TotalMinutes:F0} min",
        _ => $"{(int)span.TotalHours} h {span.Minutes} min",
    };

    private static string Kwh(double wattHours) => $"{wattHours / 1000:F1} kWh";
}
