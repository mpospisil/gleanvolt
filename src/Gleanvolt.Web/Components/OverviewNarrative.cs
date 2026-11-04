using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Resources;

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
            sentences.Add(DashboardText.DryRun);
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

        // Until full (#279): one session over several days, so which day it is belongs with the mode.
        var named = status.UntilFull is { Day: var day } ? Localized.Format(DashboardText.ModeNamedUntilFull, mode, day) : Localized.Format(DashboardText.ModeNamed, mode);

        if (status.EvChargerPowerWatts > 0)
        {
            if (status.Mode == ChargeControlMode.Off)
            {
                // The charger starts a car in Fast the moment it is plugged in, before any mode is
                // picked; this is that, and it must not read as a mode the owner chose.
                return Localized.Format(DashboardText.DrawingNoMode, Units.Power(status.EvChargerPowerWatts));
            }

            var loan = status.LoanPowerWatts > 0
                ? Localized.Format(DashboardText.LentByBattery, Units.Power(status.LoanPowerWatts))
                : "";

            return Localized.Format(DashboardText.ChargingInMode, Units.Power(status.EvChargerPowerWatts), named, loan);
        }

        if (status.Mode == ChargeControlMode.Off)
        {
            return status.CarConnected
                ? DashboardText.PluggedNotCharging
                : DashboardText.NothingCharging;
        }

        if (!status.CarConnected)
        {
            return Localized.Format(DashboardText.WaitingForCar, named);
        }

        return status.State switch
        {
            // Stood down (#280): the charger is in Stop for a wait, which the owner would otherwise
            // read as the charger having been taken off the mode.
            ChargeControlState.Paused when status.ChargerStoodDown => status.WaitUntil is { } until
                ? Localized.Format(DashboardText.StoodDownUntil, named, StoodDownUntil(status, until, zone))
                : status.UntilFull is not null
                    ? Localized.Format(DashboardText.StoodDownTomorrow, named)
                    : Localized.Format(DashboardText.StoodDownUntilCan, named),
            ChargeControlState.Paused => Localized.Format(DashboardText.PausedCar, named),
            ChargeControlState.Idle => Localized.Format(DashboardText.NotActing, named),
            _ => Localized.Format(DashboardText.NotDrawing, named),
        };
    }

    // A night of until full waits for the next day's first window, which is the forecast's estimate
    // rather than an appointment: "~09:40 (tomorrow's first window)".
    private static string StoodDownUntil(ChargeControlStatus status, DateTimeOffset until, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(until, zone);
        var tomorrow = local.Date > TimeZoneInfo.ConvertTime(status.Timestamp, zone).Date;

        return status.UntilFull is not null && tomorrow ? Localized.Format(DashboardText.TomorrowsFirstWindow, local) : $"{local:HH:mm}";
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
                Localized.Format(DashboardText.HighTariffUntil, TimeZoneInfo.ConvertTime(until, zone)),
            { Waiting: true } => DashboardText.HighTariffWaiting,
            { Ignored: true } =>
                DashboardText.LowTariffIgnored,
            _ => null,
        };
    }
}
