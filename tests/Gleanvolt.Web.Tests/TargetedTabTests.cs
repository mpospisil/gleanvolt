using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Web;
using Gleanvolt.Web.Components;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The request and the plan in words (phase 3 of #80), now the Targeted tab of the charging-plan
/// page (#98) rather than a page of its own — so these tests render the page and ask for that tab.
/// Cancel and the action failures it reports belong to the page's common header now, which is
/// exactly what a tab test should be checking still works from inside a tab.
///
/// The narrative cases at the bottom go through <see cref="TargetedPlanNarrative"/> directly — the
/// wording is the part worth testing, and a rendered component is a poor place to test a sentence.
/// </summary>
public class TargetedTabTests : BunitContext
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    // 22:00 Prague on a Monday: the hour this mode exists for.
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 20, 0, 0, TimeSpan.Zero);

    private readonly ChargeControlStatusHolder _holder = new();
    private readonly FixedTimeProvider _time = new(Now, Prague);
    private readonly FakeChargeControlModeSelector _mode = new();
    private readonly FakeChargeActions _actions;
    private readonly FakeTargetedChargeSelector _target = new();
    private readonly FakeBatteryHoldSelector _batteryHold = new();
    private readonly FakeForecastRuntimeSettings _forecast = new();

    public TargetedTabTests()
    {
        _actions = new FakeChargeActions(_mode);

        Services.AddSingleton(_holder);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<Core.Interfaces.IChargeControlModeSelector>(_mode);
        Services.AddSingleton<Core.Interfaces.IChargeActions>(_actions);
        Services.AddSingleton<Core.Interfaces.ITargetedChargeSelector>(_target);
        Services.AddSingleton<Core.Interfaces.IBatteryHoldSelector>(_batteryHold);
        Services.AddSingleton<Core.Interfaces.IForecastRuntimeSettings>(_forecast);
        Services.AddSingleton(new TargetedDisplayOptions(TimeSpan.FromHours(36)));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ChargingPlan> RenderTab() =>
        Render<ChargingPlan>(parameters => parameters.Add(p => p.Tab, "targeted"));

    [Fact]
    public void Prefills_tomorrow_morning_when_nothing_has_been_requested()
    {
        var page = RenderTab();

        Assert.Equal("2026-08-11T07:00:00", page.Find("#target-departure").GetAttribute("value"));
    }

    [Fact]
    public void Shows_the_running_request_rather_than_its_own_defaults()
    {
        // A second browser must not offer to overwrite what is already being worked to.
        _target.Set(new TargetedChargeRequest(18_000, Now.AddHours(9), Now), "test");

        var page = RenderTab();

        Assert.Equal("18", page.Find("#target-energy").GetAttribute("value"));
    }

    [Fact]
    public void Activating_sets_the_request_and_then_starts_the_mode()
    {
        var page = RenderTab();

        page.Find("#target-energy").Change("22");
        page.Find("#target-departure").Change("2026-08-11T07:00:00");
        Activate(page);

        var (request, source) = Assert.Single(_target.Sets);
        Assert.Equal(22_000, request.RequiredEnergyWh);
        Assert.Equal("Web UI", source);

        // 07:00 Prague on 11 August is 05:00 UTC -- composed through the app's zone, never the
        // server's, so a build agent in another zone cannot make this pass by accident.
        Assert.Equal(new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero), request.DepartBy);

        // Through the action, not the selector: the charger is put into Fast before the mode moves.
        Assert.Contains(_actions.Starts, s => s.Mode == ChargeControlMode.Targeted && s.Source == "Web UI");
        Assert.Equal(ChargeControlMode.Targeted, _mode.Mode);
    }

    [Fact]
    public void A_charger_that_refuses_fast_leaves_no_request_looking_active()
    {
        _actions.Failure = "The charger did not accept Fast — it is still in Green.";
        var page = RenderTab();

        Activate(page);

        page.WaitForAssertion(() => Assert.Contains("did not accept Fast", page.Find("p.error").TextContent));
        Assert.Null(_target.Request);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public void Cancelling_clears_the_request_and_stops_the_charger()
    {
        _target.Set(new TargetedChargeRequest(22_000, Now.AddHours(9), Now), "test");
        _mode.Set(ChargeControlMode.Targeted, "test");

        var page = RenderTab();
        page.Find("#cancel-target").Click();

        Assert.NotEmpty(_target.Clears);
        Assert.Null(_target.Request);
        Assert.Contains("Web UI", _actions.Stops);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public void Cancelling_stops_the_charger_even_when_another_mode_was_running()
    {
        // Cancel is the Off action now, not "undo Targeted": the button says stop, so it stops.
        _mode.Set(ChargeControlMode.Solar, "test");
        var page = RenderTab();

        page.Find("#cancel-target").Click();

        Assert.Contains("Web UI", _actions.Stops);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public void Refuses_a_request_with_no_energy_in_it()
    {
        var page = RenderTab();

        page.Find("#target-energy").Change("0");
        Activate(page);

        Assert.Contains("how much energy", page.Markup);
        Assert.Empty(_target.Sets);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public void Refuses_a_departure_in_the_past()
    {
        var page = RenderTab();

        page.Find("#target-departure").Change("2026-08-10T21:00:00");   // an hour ago in Prague
        Activate(page);

        Assert.Contains("in the past", page.Markup);
        Assert.Empty(_target.Sets);
    }

    [Fact]
    public void Refuses_a_departure_beyond_the_horizon()
    {
        var page = RenderTab();

        page.Find("#target-departure").Change("2026-08-14T07:00:00");   // three days out
        Activate(page);

        Assert.Contains("36 hours away", page.Markup);
        Assert.Empty(_target.Sets);
    }

    [Fact]
    public void Shows_an_explicit_empty_state_when_a_different_mode_is_driving()
    {
        // TargetedPlan is null whenever Mode isn't Targeted (PollingService only publishes it for that
        // mode) -- the page must not show a stale target from whatever ran earlier.
        _holder.Set(Statuses.Sample(Now, ChargeControlMode.Solar));

        var page = RenderTab();

        Assert.Contains("only shown while", page.Markup);
        Assert.DoesNotContain("Charge pace", page.Markup);

        // The request form is not part of the plan: a target can be prepared before the mode is on.
        Assert.NotEmpty(page.FindAll("#target-energy"));
    }

    [Fact]
    public void Shows_the_plan_and_its_figures_while_targeted_is_driving()
    {
        _holder.Set(Statuses.Sample(Now, ChargeControlMode.Targeted) with
        {
            TargetedPlan = TestTargetedPlans.SolarPlusGrid(Now),
        });

        var page = RenderTab();

        Assert.Contains("Still needed", page.Markup);
        Assert.Contains("22.0 kWh", page.Markup);
        Assert.Contains("From sun", page.Markup);
        Assert.Contains("14.6 kWh", page.Markup);
        Assert.Contains("From grid", page.Markup);
        Assert.Contains("7.4 kWh", page.Markup);
        Assert.Contains("Charge pace", page.Markup);
        Assert.Contains("Charging from", page.Markup);
        Assert.Contains("Battery SOC floor", page.Markup);
        Assert.Contains("55%", page.Markup);
    }

    [Fact]
    public void Follows_the_status_holder_when_a_later_poll_lands()
    {
        var page = RenderTab();
        Assert.Contains("No poll has completed yet", page.Markup);

        _holder.Set(Statuses.Sample(Now, ChargeControlMode.Targeted) with
        {
            TargetedPlan = TestTargetedPlans.SolarPlusGrid(Now),
        });

        page.WaitForAssertion(() => Assert.Contains("The sun should carry", page.Markup));
    }

    // --- The narrative, one case per strategy ---

    [Fact]
    public void The_narrative_for_a_split_plan_names_both_shares_and_the_pace()
    {
        var text = Narrate(TestTargetedPlans.SolarPlusGrid(Now));

        Assert.Contains("22.0 kWh by 07:00 tomorrow", text);
        Assert.Contains("The sun should carry", text);
        Assert.Contains("The sun should carry 14.6 kWh", text);
        Assert.Contains("the grid the remaining 7.4 kWh", text);
        Assert.Contains("paced at about", text);
        Assert.Contains("draws close to what the roof is producing", text);
        Assert.Contains("discharge hold arms whenever the car is drawing more than the roof is giving", text);

        // The old design's vocabulary must not survive anywhere in the sentence.
        Assert.DoesNotContain("sunniest hours", text);
        Assert.DoesNotContain("grid block", text);
    }

    [Fact]
    public void The_narrative_for_a_weak_sun_day_names_the_surplus_instead_of_denying_it()
    {
        // Reported live (2026-08-22): a 43kWh Solcast day with the import placed at 11:00 was described
        // as "No usable surplus is forecast before then ... with no sun in the window to wait for".
        // A zero solar share means no half-hour clears the charger's floor, not that the sky is empty --
        // and the plan had just put the import under that very sun.
        var text = Narrate(TestTargetedPlans.WeakSun(Now));

        Assert.Contains("6.4 kWh of surplus is forecast for this window", text);
        Assert.Contains("never climbs past the charger's 6 A minimum", text);
        Assert.Contains("only the difference is bought", text);

        // The two claims that were wrong, and the overstatement the user caught.
        Assert.DoesNotContain("No usable surplus is forecast", text);
        Assert.DoesNotContain("no sun in the window", text);
        Assert.DoesNotContain("the whole", text);
    }

    [Fact]
    public void The_narrative_for_a_genuinely_dark_window_still_says_there_is_nothing_to_wait_for()
    {
        // The overnight case the old wording was written for, which must survive the fix: no surplus at
        // all, so starting at once really does cost nothing.
        var text = Narrate(TestTargetedPlans.DarkWindow(Now));

        Assert.Contains("No surplus at all is forecast before then", text);
        Assert.Contains("nothing to be gained by putting it off", text);
        Assert.DoesNotContain("of surplus is forecast for this window", text);
    }

    [Fact]
    public void The_narrative_for_a_sun_only_plan_says_no_import_is_planned()
    {
        var text = Narrate(TestTargetedPlans.Solar(Now));

        Assert.Contains("The forecast covers all of it", text);
        Assert.Contains("no grid import is planned at all", text);
        Assert.DoesNotContain("from the grid, starting", text);
    }

    [Fact]
    public void The_narrative_for_a_plan_that_cannot_be_met_says_how_short_and_by_when_it_could_be()
    {
        var text = Narrate(TestTargetedPlans.Maximum(Now));

        Assert.Contains("12.0 kWh by 00:10 tomorrow is more than the", text);
        Assert.Contains("the car will have about 8.3 kWh — 3.7 kWh short", text);
        Assert.Contains("Leaving at 01:05 tomorrow instead would cover the full amount", text);
    }

    [Fact]
    public void The_narrative_for_a_met_target_says_so_and_nothing_else()
    {
        var text = Narrate(TestTargetedPlans.Complete(Now));

        Assert.Contains("Target met", text);
        Assert.Contains("22.0 kWh", text);
        Assert.DoesNotContain("grid", text);
    }

    [Fact]
    public void The_narrative_says_when_it_is_planning_blind()
    {
        var plan = TestTargetedPlans.SolarPlusGrid(Now) with { IsUsable = false, SolarEnergyWh = 0 };

        var text = Narrate(plan);

        Assert.Contains("no usable forecast", text);
        Assert.Contains("Any surplus that does appear is used first", text);
    }

    private static string Narrate(TargetedChargePlan plan) =>
        string.Join(" ", TargetedPlanNarrative.Describe(plan, Prague));

    private static void Activate(IRenderedComponent<ChargingPlan> page) => page.Find("#activate-target").Click();
}
