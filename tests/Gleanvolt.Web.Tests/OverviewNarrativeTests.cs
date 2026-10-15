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

        Assert.StartsWith("Charging the car at 4.1 kW in Forecasted mode, 0.7 kW of it lent by the home battery.", text);
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

        Assert.Contains("Forecasted mode is on and has paused the car.", text);
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
}
