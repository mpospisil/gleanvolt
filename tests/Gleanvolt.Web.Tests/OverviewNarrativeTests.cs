using Gleanvolt.Core.Enums;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The overview's headline (#255): what the controller is doing and why, in words, from nothing but
/// what the status carries.
/// </summary>
public class OverviewNarrativeTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

    private static string Describe(Gleanvolt.Core.Models.ChargeControlStatus status) =>
        string.Join(" ", OverviewNarrative.Describe(status, Prague));

    [Fact]
    public void Says_nothing_is_charging_when_off_with_no_car()
    {
        var text = Describe(Statuses.Sample(Now));

        Assert.StartsWith("Nothing is charging, and no car is plugged in.", text);
    }

    [Fact]
    public void Does_not_pass_a_car_the_charger_started_off_as_a_chosen_mode()
    {
        // The charger starts a car in Fast on plug-in, before any mode is picked.
        var text = Describe(Statuses.Sample(Now) with { CarConnected = true, EvChargerPowerWatts = 11_000 });

        Assert.StartsWith("The car is drawing 11.0 kW with no mode driving the charger", text);
    }

    [Fact]
    public void Names_the_mode_and_the_battery_loan_while_charging()
    {
        var text = Describe(Statuses.Sample(Now, ChargeControlMode.Forecasted) with
        {
            CarConnected = true,
            EvChargerPowerWatts = 4_140,
            LoanPowerWatts = 700,
        });

        Assert.StartsWith("Charging the car at 4.1 kW in Forecasted mode, 700 W of it lent by the home battery.", text);
    }

    [Fact]
    public void Gives_the_forecasted_plan_as_the_reason()
    {
        var text = Describe(Statuses.Sample(Now, ChargeControlMode.Forecasted) with
        {
            CarConnected = true,
            State = ChargeControlState.Paused,
            Plan = TestPlans.Usable(Now),
        });

        Assert.Contains("Forecasted mode is on and has paused charging.", text);
        Assert.Contains("Today gives the car about 4.5 kWh, in a window from 13:00 to 15:00.", text);
    }

    [Fact]
    public void Waits_for_a_car_without_inventing_a_reason()
    {
        var text = Describe(Statuses.Sample(Now, ChargeControlMode.Solar));

        Assert.StartsWith("Solar mode is on, waiting for a car to be plugged in.", text);
    }

    [Fact]
    public void Leaves_the_house_to_the_figures()
    {
        // The strip under the headline carries the roof, the battery and the grid; the headline is
        // only about the car.
        var text = Describe(Statuses.Sample(Now));

        Assert.Equal("Nothing is charging, and no car is plugged in.", text);
    }

    [Fact]
    public void Says_so_first_when_it_is_a_dry_run()
    {
        var text = Describe(Statuses.Sample(Now) with { DryRun = true });

        Assert.StartsWith("Dry run:", text);
    }

    [Fact]
    public void Says_when_a_stood_down_charger_is_due_back()
    {
        // #280: in Stop for a long wait, which the owner would otherwise read as the charger taken off the mode.
        var text = Describe(Statuses.Sample(Now, ChargeControlMode.Solar) with
        {
            CarConnected = true,
            State = ChargeControlState.Paused,
            ChargerStoodDown = true,
            WaitUntil = new DateTimeOffset(2026, 8, 12, 7, 40, 0, TimeSpan.Zero),
        });

        Assert.Contains("mode is on and has paused the charger until 09:40.", text);
    }

    [Fact]
    public void Says_an_until_full_night_waits_for_tomorrows_first_window_and_names_the_day()
    {
        // #279: the next day's window is the forecast's estimate, not an appointment.
        var text = Describe(Statuses.Sample(Now, ChargeControlMode.Forecasted) with
        {
            Plan = null,
            CarConnected = true,
            State = ChargeControlState.Paused,
            ChargerStoodDown = true,
            WaitUntil = new DateTimeOffset(2026, 8, 13, 7, 40, 0, TimeSpan.Zero),
            UntilFull = new Core.Models.UntilFullStatus(Now.AddDays(-1), Day: 2),
        });

        Assert.Contains("(until full, day 2) is on and has paused the charger until ~09:40 (tomorrow's first window).", text);
    }

        [Fact]
    public void Says_a_stood_down_charger_waits_without_inventing_a_time()
    {
        var text = Describe(Statuses.Sample(Now, ChargeControlMode.Solar) with
        {
            CarConnected = true,
            State = ChargeControlState.Paused,
            ChargerStoodDown = true,
        });

        Assert.Contains("has paused the charger until it can charge.", text);
    }

    [Fact]
    public void Names_the_tariff_when_a_low_tariff_only_mode_is_waiting()
    {
        var text = Describe(Statuses.Sample(Now, ChargeControlMode.FastNoBattery) with
        {
            CarConnected = true,
            State = Gleanvolt.Core.Enums.ChargeControlState.Paused,
            LowTariff = new Gleanvolt.Core.Models.LowTariffStatus(Waiting: true, Until: new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero), Ignored: false),
        });

        Assert.Contains("has paused charging", text);
        Assert.Contains("High tariff until 22:00; charging waits for low tariff (Low tariff only is on).", text);
    }

    [Fact]
    public void Says_when_low_tariff_only_has_nothing_to_wait_for()
    {
        var text = Describe(Statuses.Sample(Now, ChargeControlMode.SolarGrid) with
        {
            LowTariff = new Gleanvolt.Core.Models.LowTariffStatus(Waiting: false, Until: null, Ignored: true),
        });

        Assert.Contains("no tariff is configured, so it charges at any hour", text);
    }

    [Fact]
    public void Says_a_restart_resumed_the_mode_in_the_site_zone()
    {
        // 09:49 UTC is 11:49 in Prague in summer: the site's zone, not the server's.
        var text = Describe(Statuses.Sample(Now, ChargeControlMode.FastNoBattery) with
        {
            ResumedAt = new DateTimeOffset(2026, 8, 12, 9, 49, 0, TimeSpan.Zero),
        });

        Assert.EndsWith("Resumed after Gleanvolt restarted at 11:49, as if selected again.", text);
    }

    [Fact]
    public void Says_nothing_of_a_restart_for_a_mode_somebody_selected() =>
        Assert.DoesNotContain("restarted", Describe(Statuses.Sample(Now, ChargeControlMode.FastNoBattery)));

    [Fact]
    public void A_suspended_mode_says_since_when_and_why_and_that_the_charger_is_unmanaged()
    {
        // 09:50 UTC is 11:50 in Prague in summer.
        var text = Describe(Statuses.Sample(Now, ChargeControlMode.FastNoBattery) with
        {
            State = ChargeControlState.Suspended,
            SuspendedSince = new DateTimeOffset(2026, 8, 12, 9, 50, 0, TimeSpan.Zero),
            Charger = new Gleanvolt.Core.Models.ChargerAvailability(
                Gleanvolt.Core.Models.ChargerAvailabilityState.NoPlugin, "No charger plugin is connected.",
                Gleanvolt.Core.Models.Phrase.Of("Charger.NoPlugin"), new DateTimeOffset(2026, 8, 12, 9, 48, 0, TimeSpan.Zero)),
        });

        Assert.StartsWith("Fast (no battery) has been suspended since 11:50 (10 min): Gleanvolt can't control the charger. No charger plugin is connected.", text);
        Assert.Contains("follows its own settings, so any charging now is unmanaged", text);
    }

    [Fact]
    public void An_unavailable_charger_with_no_mode_running_is_said()
    {
        var text = Describe(Statuses.Sample(Now) with
        {
            Charger = new Gleanvolt.Core.Models.ChargerAvailability(
                Gleanvolt.Core.Models.ChargerAvailabilityState.Error, "Gleanvolt can't read the charger: timeout",
                Since: new DateTimeOffset(2026, 8, 12, 9, 48, 0, TimeSpan.Zero)),
        });

        Assert.EndsWith("Gleanvolt can't control the charger since 11:48: Gleanvolt can't read the charger: timeout", text);
    }
}
