using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Resources;

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
                Localized.Format(PlanText.TargetMet, Units.Energy(plan.DeliveredEnergyWh), Units.Energy(plan.RequiredEnergyWh)));

            return paragraphs;
        }

        var headline = Localized.Format(PlanText.Headline, Units.Energy(plan.RemainingEnergyWh), At(plan.DepartBy, plan.Now, zone));
        if (plan.DeliveredEnergyWh > 0)
        {
            headline += " " + Localized.Format(PlanText.AlreadyInCar, Units.Energy(plan.DeliveredEnergyWh), Units.Energy(plan.RequiredEnergyWh));
        }

        switch (plan.Strategy)
        {
            case TargetedChargeStrategy.Maximum:
                paragraphs.Add(
                    Localized.Format(PlanText.MoreThanCanDeliver, Units.Energy(plan.RemainingEnergyWh), At(plan.DepartBy, plan.Now, zone), Duration(plan.TimeRemaining), Units.Energy(plan.ExpectedEnergyWh), Units.Energy(plan.ShortfallWh))
                    + (plan.FeasibleDeparture is { } later
                        ? " " + Localized.Format(PlanText.LeavingInstead, At(later, plan.Now, zone))
                        : string.Empty));

                paragraphs.Add(
                    PlanText.BatteryHeldOut);
                break;

            case TargetedChargeStrategy.Solar:
                paragraphs.Add(headline);
                paragraphs.Add(
                    Localized.Format(PlanText.ForecastCoversAll, Units.Energy(plan.SolarEnergyWh), Between(plan, zone)));
                break;

            default:
                paragraphs.Add(headline);
                paragraphs.Add(SolarPlusGrid(plan, zone));
                paragraphs.Add(
                    PlanText.BatteryKeepsPriority);
                break;
        }

        if (Held(plan, zone) is { } held)
        {
            paragraphs.Add(held);
        }

        if (!plan.IsUsable)
        {
            paragraphs.Add(
                PlanText.NoUsableForecastTargeted);
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
        var pace = Units.Power(plan.RequiredPaceWatts);

        if (plan.SolarEnergyWh > 0 && plan.GridEnergyWh > 0)
        {
            return Localized.Format(PlanText.SunAndGrid, Units.Energy(plan.SolarEnergyWh), Between(plan, zone), Units.Energy(plan.GridEnergyWh), pace);
        }

        if (plan.HasUnusableSurplus)
        {
            return Localized.Format(PlanText.UnusableSurplus, Units.Energy(plan.ForecastSurplusWh), pace);
        }

        return Localized.Format(PlanText.AllFromGrid, Units.Energy(plan.RemainingEnergyWh), pace);
    }

    /// <summary>
    /// The held tail in words, or null when nothing is being held.
    ///
    /// <para>Two sentences, and which one comes back matters more than either. A plan that is
    /// <em>going</em> to hold is still charging, and saying "waiting" about a running charger is
    /// confusing; a plan that <em>is</em> holding has a charger sitting visibly idle with hours to go,
    /// which is the single state most likely to be read as a fault. This is the paragraph that says it
    /// is not.</para>
    /// </summary>
    private static string? Held(TargetedChargePlan plan, TimeZoneInfo zone)
    {
        if (plan.HoldUntil is not { } release)
        {
            return null;
        }

        var tail = Units.Energy(plan.TailEnergyWh);

        return plan.IsHoldingAt(plan.Now)
            ? Localized.Format(PlanText.HoldingNow, tail, At(release, plan.Now, zone))
            : Localized.Format(PlanText.HoldingLater, tail, At(release, plan.Now, zone));
    }

    private static string Between(TargetedChargePlan plan, TimeZoneInfo zone) =>
        plan.SolarStart is { } from && plan.SolarEnd is { } to
            ? Localized.Format(PlanText.Between, At(from, plan.Now, zone), At(to, plan.Now, zone))
            : PlanText.LaterToday;

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
            1 => Localized.Format(PlanText.AtTomorrow, local),
            _ => Localized.Format(PlanText.AtDay, local),
        };
    }

    private static string Duration(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? PlanText.NoTime : Units.Duration(span);
}
