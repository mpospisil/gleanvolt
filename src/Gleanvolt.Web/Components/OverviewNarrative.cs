using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>
/// The overview's headline (#255): what the controller is doing right now and why, as prose, ahead of
/// the figures that say the same thing one number at a time.
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

        sentences.Add(Charging(status));

        if (Why(status, zone) is { } why)
        {
            sentences.Add(why);
        }

        sentences.Add(House(status));

        return sentences;
    }

    // The car first: it is what the controller is for, and the one part of the picture that changes
    // because of a decision rather than because of the weather.
    private static string Charging(ChargeControlStatus status)
    {
        var mode = ChargeModeTabs.Label(status.Mode);

        if (status.EvChargerPowerWatts > 0)
        {
            if (status.Mode == ChargeControlMode.Off)
            {
                // The charger starts a car in Fast the moment it is plugged in, before any mode is
                // picked; this is that, and it must not read as a mode the owner chose.
                return $"The car is drawing {Kw(status.EvChargerPowerWatts)} with no mode driving the charger — "
                    + "the charger started it on its own.";
            }

            var loan = status.LoanPowerWatts > 0
                ? $", {Kw(status.LoanPowerWatts)} of it lent by the home battery"
                : "";

            return $"Charging the car at {Kw(status.EvChargerPowerWatts)} in {mode} mode{loan}.";
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

        return null;
    }

    // The house second: true whatever is plugged in.
    private static string House(ChargeControlStatus status)
    {
        var roof = status.SolarPowerWatts > 0
            ? $"The roof is making {Kw(status.SolarPowerWatts)}"
            : "The roof is making nothing";

        var battery = status.BatteryPowerWatts switch
        {
            > 0 => $"the home battery is at {status.BatterySocPercent:F0}% and charging at {Kw(status.BatteryPowerWatts)}",
            < 0 => $"the home battery is at {status.BatterySocPercent:F0}% and supplying {Kw(-status.BatteryPowerWatts)}",
            _ => $"the home battery is at {status.BatterySocPercent:F0}%",
        };

        var grid = status.GridPowerWatts switch
        {
            > 0 => $", and {Kw(status.GridPowerWatts)} is coming in from the grid",
            < 0 => $", and {Kw(-status.GridPowerWatts)} is going out to the grid",
            _ => "",
        };

        return $"{roof}; {battery}{grid}.";
    }

    private static string Kw(double watts) => $"{watts / 1000:0.0} kW";
}
