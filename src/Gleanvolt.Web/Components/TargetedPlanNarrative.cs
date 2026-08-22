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
                    "The home battery keeps its priority throughout: the discharge hold arms whenever the car is "
                    + "drawing more than the roof is giving, so the pack never feeds it.");
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

    /// <summary>
    /// The split plan in words. Three cases, not two — and the third is the one that matters, because
    /// getting it wrong describes a sunny day as a dark one.
    ///
    /// <para>A zero solar share does <b>not</b> mean the window has no sun. It means no half-hour in it
    /// clears the charger's ~4.14 kW floor, so none of it can carry the car unaided. The plan's answer
    /// is to put the import on exactly those hours, where the roof pays for part of it — so the
    /// sentence has to say that the sun is there, say how much of it, and stop short of promising the
    /// grid figure will actually be bought.</para>
    /// </summary>
    private static string SolarPlusGrid(TargetedChargePlan plan, TimeZoneInfo zone)
    {
        var pace = $"{plan.RequiredPaceWatts / 1000:F1} kW";

        if (plan.SolarEnergyWh > 0 && plan.GridEnergyWh > 0)
        {
            return $"The sun should carry {Kwh(plan.SolarEnergyWh)} of it {Between(plan, zone)}, and the grid the "
                + $"remaining {Kwh(plan.GridEnergyWh)}. Rather than charge flat out and finish early, the car is "
                + $"paced at about {pace} across the window, so the charger draws close to what the roof is "
                + "producing and buys only the shortfall.";
        }

        if (plan.HasUnusableSurplus)
        {
            return $"{Kwh(plan.ForecastSurplusWh)} of surplus is forecast for this window, but it never climbs past "
                + "the charger's 6 A minimum, so the car cannot run on it unaided. It is still worth having: the "
                + $"charger is held at that minimum while the sun is up, the roof supplies what it has, and only the "
                + $"difference is bought — about {pace} of grid on average.";
        }

        return $"No surplus at all is forecast before then, so the whole {Kwh(plan.RemainingEnergyWh)} still needed "
            + $"comes from the grid, paced at about {pace} across the window. With no sun to wait for there is "
            + "nothing to be gained by putting it off, and spreading it leaves room to recover if anything drops out.";
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
