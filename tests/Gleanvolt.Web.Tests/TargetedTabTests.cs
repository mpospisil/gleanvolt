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
///
/// Since #99 the tab also reads the car and quotes the plan before it starts one, so most cases here
/// press two buttons rather than one: <c>#preview-target</c> builds the plan, <c>#activate-target</c>
/// commits it. A form the preview refused never renders the second, which is what the refusal cases
/// assert without having to say so.
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
    private readonly FakeTargetedChargePreview _preview = new(TestTargetedPlans.SolarPlusGrid(Now));

    // Registered empty, as an install with Vehicle:Enabled=false is: the cases below that want a car
    // set one, and everything else asserts the tab a car-less install actually sees.
    private readonly VehicleStateHolder _vehicle = new();

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
        Services.AddSingleton<Core.Interfaces.ITargetedChargePreview>(_preview);
        Services.AddSingleton<Core.Interfaces.IVehicleTelemetry>(_vehicle);
        Services.AddSingleton(new TargetedDisplayOptions(TimeSpan.FromHours(36)));

        // No pack size, so no SOC basis. A test that wants one registers its own before rendering —
        // the later registration wins, and nothing has resolved this yet.
        Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12)));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>An ID.4 Pro's usable pack, and the AC losses between the charger's meter and it.</summary>
    private void WithAKnownPack() =>
        Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12), BatteryCapacityKWh: 77, ChargeEfficiency: 0.9));

    private void CarReports(double? socPercent = 42, double? rangeKm = 176, TimeSpan? age = null) =>
        _vehicle.Set(new VehicleState(
            Now - (age ?? TimeSpan.FromMinutes(20)),
            SocPercent: socPercent,
            RangeKm: rangeKm,
            PlugState: VehiclePlugState.Connected,
            ChargeState: VehicleChargeState.Idle,
            SourceId: "id4"));

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

    // --- What the car says, and asking in its terms (#99) ---

    [Fact]
    public void Shows_nothing_about_a_car_that_has_never_reported()
    {
        // An install with Vehicle:Enabled=false: the form it has always had, and no empty card under it.
        var page = RenderTab();

        Assert.Empty(page.FindAll("#vehicle-card"));
        Assert.Empty(page.FindAll("#target-basis"));
        Assert.NotEmpty(page.FindAll("#target-energy"));
    }

    [Fact]
    public void Shows_the_battery_and_the_range_the_car_last_reported()
    {
        _holder.Set(Statuses.Sample(Now, ChargeControlMode.Off) with { CarConnected = true });
        CarReports(socPercent: 42, rangeKm: 176);

        var page = RenderTab();
        var card = page.Find("#vehicle-card").TextContent;

        Assert.Contains("42%", card);
        Assert.Contains("176 km", card);
        Assert.Contains("Yes at the charger, connected by the car", card);
        Assert.Contains("20 min ago", card);
        Assert.Contains("via id4", card);
    }

    [Fact]
    public void Marks_a_reading_too_old_to_plan_from_without_hiding_it()
    {
        // Stale is a dead feed, not a wrong number -- a parked car's SOC doesn't drift. So it is
        // flagged and the basis stays offered, rather than being withdrawn and converted in someone's
        // head from the very same figure.
        WithAKnownPack();
        CarReports(age: TimeSpan.FromHours(14));

        var page = RenderTab();

        Assert.Contains("Stale", page.Find("#vehicle-card").TextContent);
        Assert.NotEmpty(page.FindAll("#target-basis"));
    }

    [Fact]
    public void Offers_a_battery_target_only_when_the_pack_size_is_configured()
    {
        // Without Vehicle:BatteryCapacityKWh there is no honest conversion from 80% to kilowatt-hours,
        // and a guessed pack size would make every such target quietly wrong.
        CarReports();

        var page = RenderTab();

        Assert.Empty(page.FindAll("#target-basis"));
        Assert.NotEmpty(page.FindAll("#target-energy"));
    }

    [Fact]
    public void Offers_no_battery_target_for_a_car_that_reports_no_soc()
    {
        WithAKnownPack();
        CarReports(socPercent: null);

        var page = RenderTab();

        Assert.NotEmpty(page.FindAll("#vehicle-card"));
        Assert.Empty(page.FindAll("#target-basis"));
    }

    [Fact]
    public void Converts_a_battery_target_into_energy_at_the_charger()
    {
        WithAKnownPack();
        CarReports(socPercent: 42);

        var page = RenderTab();
        page.Find("#target-basis").Change("Soc");
        page.Find("#target-soc").Change("80");
        page.Find("#target-departure").Change("2026-08-11T07:00:00");
        Activate(page);

        // 38% of a 77 kWh pack is 29.26 kWh in the cells; at 90% it takes 32.5 kWh at the charger,
        // which is where the promise is metered.
        var (request, _) = Assert.Single(_target.Sets);
        Assert.Equal(32_511, request.RequiredEnergyWh, 0);

        // Recorded so the request can be read back in the terms it was asked in -- and never
        // re-derived from a later reading.
        Assert.Equal(80, request.TargetSocPercent);
        Assert.Equal(42, request.VehicleSocPercentAtRequest);
        Assert.True(request.IsSocBased);
    }

    [Fact]
    public void Says_what_a_battery_target_works_out_at_before_it_is_started()
    {
        WithAKnownPack();
        CarReports(socPercent: 42);

        var page = RenderTab();
        page.Find("#target-basis").Change("Soc");
        page.Find("#target-soc").Change("80");

        Assert.Contains("From 42% now, that is about 32.5 kWh", page.Markup);

        page.Find("#preview-target").Click();

        Assert.Contains("Charging to 80%, from the 42% the car last reported", page.Find("#target-preview").TextContent);
    }

    [Fact]
    public void Refuses_a_battery_target_the_car_is_already_past()
    {
        WithAKnownPack();
        CarReports(socPercent: 85);

        var page = RenderTab();
        page.Find("#target-basis").Change("Soc");
        page.Find("#target-soc").Change("80");
        Activate(page);

        Assert.Contains("already at 85%", page.Find("p.error").TextContent);
        Assert.Empty(_target.Sets);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public void Reads_a_running_battery_target_back_in_its_own_terms()
    {
        _target.Set(new TargetedChargeRequest(32_511, Now.AddHours(9), Now, TargetSocPercent: 80, VehicleSocPercentAtRequest: 42), "test");
        _holder.Set(Statuses.Sample(Now, ChargeControlMode.Targeted) with
        {
            TargetedPlan = TestTargetedPlans.SolarPlusGrid(Now),
        });

        var page = RenderTab();

        Assert.Contains("Charging to 80%, from the 42% the car last reported", page.Markup);
    }

    // --- The plan before the charger moves (#99) ---

    [Fact]
    public void Previewing_prices_the_request_without_making_it()
    {
        var page = RenderTab();

        page.Find("#target-energy").Change("22");
        page.Find("#target-departure").Change("2026-08-11T07:00:00");
        page.Find("#preview-target").Click();

        // Priced, and priced only: no request, no mode, nothing written.
        var priced = Assert.Single(_preview.Requests);
        Assert.Equal(22_000, priced.RequiredEnergyWh);
        Assert.Empty(_target.Sets);
        Assert.Null(_target.Request);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.Empty(_actions.Starts);

        // And the plan is on screen, in the same words the running one is shown in.
        var preview = page.Find("#target-preview").TextContent;
        Assert.Contains("The sun should carry", preview);
        Assert.Contains("Charge pace", preview);
    }

    [Fact]
    public void There_is_no_start_button_until_a_plan_has_been_seen()
    {
        var page = RenderTab();

        Assert.Empty(page.FindAll("#activate-target"));

        page.Find("#preview-target").Click();

        Assert.NotEmpty(page.FindAll("#activate-target"));
    }

    [Fact]
    public void Editing_the_form_drops_the_preview_rather_than_leaving_it_to_be_confirmed()
    {
        var page = RenderTab();
        page.Find("#preview-target").Click();
        Assert.NotEmpty(page.FindAll("#activate-target"));

        page.Find("#target-energy").Change("30");

        // Otherwise the button that says start sits under a plan describing figures that have changed.
        Assert.Empty(page.FindAll("#target-preview"));
        Assert.Empty(page.FindAll("#activate-target"));
    }

    [Fact]
    public void Discarding_the_preview_goes_back_to_the_form_and_makes_no_request()
    {
        var page = RenderTab();
        page.Find("#preview-target").Click();

        page.Find("#discard-preview").Click();

        Assert.Empty(page.FindAll("#target-preview"));
        Assert.Empty(_target.Sets);
        Assert.NotEmpty(page.FindAll("#target-energy"));
    }

    [Fact]
    public void A_target_can_still_be_started_before_the_first_poll_has_landed()
    {
        // Nothing to plan from is a sentence, not a locked button: a service that has just come up can
        // still be given a target, and the plan appears on the first poll.
        _preview.Plan = null;

        var page = RenderTab();
        page.Find("#preview-target").Click();

        Assert.Contains("nothing to build a plan from", page.Find("#target-preview").TextContent);

        page.Find("#activate-target").Click();

        Assert.Single(_target.Sets);
        Assert.Equal(ChargeControlMode.Targeted, _mode.Mode);
    }

    [Fact]
    public void The_preview_gives_way_to_the_running_plan_once_it_is_started()
    {
        var page = RenderTab();
        Activate(page);

        // Two plans on one tab, one of them a quote for a charge that has already started, is one
        // plan too many.
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#target-preview")));
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

    // --- Just in time (#101) ---

    [Fact]
    public void The_priority_switch_is_not_offered_without_a_way_to_find_the_rest_point()
    {
        // Same rule as the SOC basis: a rest point is a state of charge, and without a pack size and a
        // reading there is no honest way to know where 80% of this car is.
        CarReports();

        var page = RenderTab();

        Assert.Empty(page.FindAll("#charge-priority"));
    }

    [Fact]
    public void The_priority_switch_appears_with_a_pack_and_a_reading()
    {
        WithAKnownPack();
        CarReports();

        var page = RenderTab();

        Assert.NotEmpty(page.FindAll("#charge-priority"));

        // And the rest field only under the priority that uses it.
        Assert.Empty(page.FindAll("#rest-soc"));
        page.Find("#charge-priority").Change(nameof(TargetedChargePriority.JustInTime));
        Assert.NotEmpty(page.FindAll("#rest-soc"));
    }

    [Fact]
    public void Just_in_time_splits_the_request_into_a_tail_at_the_rest_point()
    {
        WithAKnownPack();
        CarReports(socPercent: 42);

        var page = RenderTab();
        page.Find("#target-basis").Change("Soc");
        page.Find("#target-soc").Change("100");
        page.Find("#charge-priority").Change(nameof(TargetedChargePriority.JustInTime));
        page.Find("#rest-soc").Change("80");
        page.Find("#target-departure").Change("2026-08-11T07:00:00");
        Activate(page);

        var (request, _) = Assert.Single(_target.Sets);

        Assert.Equal(TargetedChargePriority.JustInTime, request.Priority);
        Assert.Equal(80, request.RestSocPercent);

        // 80 -> 100 is 20% of 77 kWh in the cells; at 90% that is 17.1 kWh at the charger.
        Assert.Equal(17_111, request.TailEnergyWh, 0);
        Assert.True(request.HoldsTail);
    }

    [Fact]
    public void Cheapest_is_the_default_and_holds_nothing()
    {
        WithAKnownPack();
        CarReports(socPercent: 42);

        var page = RenderTab();
        page.Find("#target-departure").Change("2026-08-11T07:00:00");
        Activate(page);

        var (request, _) = Assert.Single(_target.Sets);

        Assert.Equal(TargetedChargePriority.Cheapest, request.Priority);
        Assert.Equal(0, request.TailEnergyWh);
        Assert.False(request.HoldsTail);
    }

    [Fact]
    public void A_car_already_past_the_rest_point_is_told_the_whole_charge_is_held()
    {
        WithAKnownPack();
        CarReports(socPercent: 85);

        var page = RenderTab();
        page.Find("#charge-priority").Change(nameof(TargetedChargePriority.JustInTime));

        Assert.Contains("already at 85%", page.Find("#rest-soc").ParentElement!.TextContent);
        Assert.Contains("the whole charge is the held stretch", page.Find("#rest-soc").ParentElement!.TextContent);
    }

    [Fact]
    public void A_target_that_never_reaches_the_rest_point_says_there_is_nothing_to_hold()
    {
        WithAKnownPack();
        CarReports(socPercent: 42);

        var page = RenderTab();
        page.Find("#target-basis").Change("Soc");
        page.Find("#target-soc").Change("70");
        page.Find("#charge-priority").Change(nameof(TargetedChargePriority.JustInTime));

        Assert.Contains("nothing above it to hold", page.Find("#rest-soc").ParentElement!.TextContent);
    }

    [Fact]
    public void The_narrative_of_a_planned_hold_says_what_is_held_and_until_when()
    {
        var plan = TestTargetedPlans.SolarPlusGrid(Now) with
        {
            TailEnergyWh = 6_000,
            HoldUntil = Now.AddHours(8),
        };

        var text = Narrate(plan);

        Assert.Contains("6.0 kWh", text);
        Assert.Contains("held back until", text);
        Assert.Contains("finishes just before departure", text);
    }

    [Fact]
    public void The_narrative_of_a_hold_in_force_says_the_idle_charger_is_deliberate()
    {
        // RemainingEnergyWh == TailEnergyWh: everything below the rest point is in the car, so the
        // charger is sitting still. This is the sentence that stops that reading as a fault.
        var plan = TestTargetedPlans.SolarPlusGrid(Now) with
        {
            RemainingEnergyWh = 6_000,
            TailEnergyWh = 6_000,
            HoldUntil = Now.AddHours(8),
        };

        var text = Narrate(plan);

        Assert.Contains("idle on purpose", text);
        Assert.Contains("turned down", text);
    }

    [Fact]
    public void The_narrative_says_nothing_about_holding_when_nothing_is_held()
    {
        var text = Narrate(TestTargetedPlans.SolarPlusGrid(Now));

        Assert.DoesNotContain("held back", text);
        Assert.DoesNotContain("idle on purpose", text);
    }

    private static string Narrate(TargetedChargePlan plan) =>
        string.Join(" ", TargetedPlanNarrative.Describe(plan, Prague));

    // Preview, then confirm (#99). The Start button only exists once a plan is on screen, so a form
    // the preview refused simply never reaches the second click -- which is the behaviour the refusal
    // cases are asserting, and the reason this doesn't insist the button is there.
    private static void Activate(IRenderedComponent<ChargingPlan> page)
    {
        page.Find("#preview-target").Click();

        var start = page.FindAll("#activate-target");
        if (start.Count > 0)
        {
            start[0].Click();
        }
    }
}
