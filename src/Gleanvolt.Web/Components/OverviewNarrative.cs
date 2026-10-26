using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>
/// The overview's headline (#255): what the controller is doing about the car right now and why, as
/// prose, ahead of the figures.
///
/// <para>Only what the status actually carries. It never guesses at a reason the controller did not
/// give: a paused mode says it is waiting, and the why comes from the plan the mode itself published
/// (Forecasted's day plan, Targeted's plan), or not at all.</para>
///
/// <para>Pure and separate from the page, like <see cref="TargetedPlanNarrative"/>: the wording is
/// the part worth testing.</para>
/// </summary>
public static class OverviewNarrative
{
    /// <summary>The headline as a short series of sentences, in the order they should be read.</summary>
    /// <param name="status">The last poll.</param>
    /// <param name="zone">The zone any time is rendered in -- the site's, never the browser's.</param>
    public static IReadOnlyList<string> Describe(ChargeControlStatus status, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(zone);

        var sentences = new List<string>();

        if (status.DryRun)
        {
            sentences.Add("Dry run: decisions are logged, nothing is written to the charger.");
        }

        sentences.Add(Charging(status, zone));

        if (Why(status, zone) is { } why)
        {
            sentences.Add(why);
        }

        // The house is not in it: the figures right under the headline already say what the roof,
        // the battery and the grid are doing, and saying it twice made the headline five lines long.
        return sentences;
    }

    // The car first: it is what the controller is for, and the one part of the picture that changes
    // because of a decision rather than because of the weather.
    private static string Charging(ChargeControlStatus status, TimeZoneInfo zone)
    {
        var mode = ChargeModeTabs.Label(status.Mode);

        if (status.EvChargerPowerWatts > 0)
        {
            if (status.Mode == ChargeControlMode.Off)
            {
                // The charger starts a car in Fast the moment it is plugged in, before any mode is
                // picked; this is that, and it must not read as a mode the owner chose.
                return $"The car is drawing {Units.Power(status.EvChargerPowerWatts)} with no mode driving the charger — "
                    + "the charger started it on its own.";
            }

            var loan = status.LoanPowerWatts > 0
                ? $", {Units.Power(status.LoanPowerWatts)} of it lent by the home battery"
                : "";

            return $"Charging the car at {Units.Power(status.EvChargerPowerWatts)} in {mode} mode{loan}.";
        }

        if (status.Mode == ChargeControlMode.Off)
        {
            return status.CarConnected
                ? "A car is plugged in, but nothing is charging: no mode is driving the charger."
                : "Nothing is charging, and no car is plugged in.";
        }

        if (!status.CarConnected)
        {
            return $"{mode} mode is on, waiting for a car to be plugged in.";
        }

        return status.State switch
        {
            // Stood down (#280): the charger is in Stop for a wait, which the owner would otherwise
            // read as the charger having been taken off the mode.
            ChargeControlState.Paused when status.ChargerStoodDown => status.WaitUntil is { } until
                ? $"{mode} mode is on and has stood the charger down until {TimeZoneInfo.ConvertTime(until, zone):HH:mm}."
                : $"{mode} mode is on and has stood the charger down until it can charge.",
            ChargeControlState.Paused => $"{mode} mode is on and has paused the car.",
            ChargeControlState.Idle => $"{mode} mode is on but not acting on the charger.",
            _ => $"{mode} mode is on; the car is not drawing power.",
        };
    }

    // The reason, where the mode published one. Forecasted's day plan and Targeted's plan are the two
    // that carry a sentence of their own; the others are explained by their figures.
    private static string? Why(ChargeControlStatus status, TimeZoneInfo zone)
    {
        if (status.Mode == ChargeControlMode.Forecasted && status.Plan is { } plan)
        {
            return SolarDayPlanNarrative.Headline(plan, zone);
        }

        if (status.Mode == ChargeControlMode.Targeted && status.TargetedPlan is { } targeted)
        {
            return TargetedPlanNarrative.Describe(targeted, zone).FirstOrDefault();
        }

        // Low tariff only (#274): the status carries it only for the mode driving, so this is about
        // the mode the sentence before it named.
        return status.LowTariff switch
        {
            { Waiting: true, Until: { } until } =>
                $"High tariff until {TimeZoneInfo.ConvertTime(until, zone):HH:mm}; it is waiting for low tariff (low tariff only).",
            { Waiting: true } => "High tariff; it is waiting for low tariff (low tariff only).",
            { Ignored: true } =>
                "Low tariff only is set, but there is no tariff to wait for, so it charges whatever the hour.",
            _ => null,
        };
    }
}
