using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The Fast tab since it grew an amount (#119). The tab is the third door onto the same factory, so
/// what is worth asserting here is the door rather than the arithmetic: which controls are offered on
/// which installation, what the button does with them and in what order, and that an installation that
/// ignores the whole feature sees the tab it always saw.
/// </summary>
public class FastTabTests : PageTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 20, 0, 0, TimeSpan.Zero);

    private readonly ChargeControlStatusHolder _holder = new();
    private readonly FixedTimeProvider _time = new(Now, Prague);
    private readonly FakeChargeControlModeSelector _mode = new();
    private readonly FakeChargeActions _actions;
    private readonly FakeTargetedChargeSelector _target = new();
    private readonly FakeFastChargeSelector _fast = new();
    private readonly FakeBatteryHoldSelector _batteryHold = new();
    private readonly FakeForecastRuntimeSettings _forecast = new();

    // Registered empty, as an install with Vehicle:Enabled=false is.
    private readonly VehicleStateHolder _vehicle = new();

    public FastTabTests()
    {
        _actions = new FakeChargeActions(_mode);

        Services.AddSingleton(_holder);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<Core.Interfaces.IChargeControlModeSelector>(_mode);
        Services.AddSingleton<Core.Interfaces.IChargeActions>(_actions);
        Services.AddSingleton<Core.Interfaces.ITargetedChargeSelector>(_target);
        Services.AddSingleton<Core.Interfaces.IFastChargeSelector>(_fast);
        Services.AddSingleton<Core.Interfaces.IBatteryHoldSelector>(_batteryHold);
        Services.AddSingleton<Core.Interfaces.IForecastRuntimeSettings>(_forecast);
        Services.AddSingleton<Core.Interfaces.ITargetedChargePreview>(new FakeTargetedChargePreview());
        Services.AddSingleton<Core.Interfaces.IVehicleTelemetry>(_vehicle);
        Services.AddSingleton(new TargetedDisplayOptions(TimeSpan.FromHours(36)));

        // No pack size, so no SOC basis. A test that wants one registers its own before rendering.
        Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12)));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private void WithAKnownPack() =>
        Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12), BatteryCapacityKWh: 77, ChargeEfficiency: 0.9));

    private void CarReports(double? socPercent = 42) =>
        _vehicle.Set(new VehicleState(Now.AddMinutes(-20), SocPercent: socPercent, SourceId: "id4"));

    private IRenderedComponent<ChargingPlan> RenderTab() =>
        Render<ChargingPlan>(parameters => parameters.Add(p => p.Tab, "fast"));

    [Fact]
    public void Presses_through_to_the_mode_with_no_limit_by_default()
    {
        // The whole "nothing changes for anyone who does not ask" promise, in one case.
        var page = RenderTab();

        page.Find("#start-fast-no-battery").Click();

        Assert.Equal(ChargeControlMode.FastNoBattery, Assert.Single(_actions.Starts).Mode);
        Assert.Empty(_fast.Sets);
    }

    [Fact]
    public void Offers_full_and_energy_but_not_a_battery_target_without_a_pack()
    {
        var page = RenderTab();

        var options = page.Find("#fast-basis").QuerySelectorAll("option").Select(o => o.TextContent.Trim());

        Assert.Equal(["Until the car stops", "Energy to add (kWh)"], options);
    }

    [Fact]
    public void Offers_the_battery_target_once_a_pack_and_a_reading_are_there()
    {
        WithAKnownPack();
        CarReports();

        var page = RenderTab();

        var options = page.Find("#fast-basis").QuerySelectorAll("option").Select(o => o.TextContent.Trim());

        Assert.Contains("Battery target (%)", options);
    }

    [Fact]
    public void Withholds_the_battery_target_when_the_car_has_reported_nothing()
    {
        // A configured pack is only half of it: without a reading there is nothing to measure from.
        WithAKnownPack();

        var page = RenderTab();

        var options = page.Find("#fast-basis").QuerySelectorAll("option").Select(o => o.TextContent.Trim());

        Assert.DoesNotContain("Battery target (%)", options);
    }

    [Fact]
    public void Sets_an_energy_limit_and_then_starts_the_mode()
    {
        var page = RenderTab();

        page.Find("#fast-basis").Change(nameof(FastChargeBasis.Energy));
        page.Find("#fast-energy").Change("20");
        page.Find("#start-fast-no-battery").Click();

        var (limit, source) = Assert.Single(_fast.Sets);
        Assert.Equal(20_000, limit.RequiredEnergyWh);
        Assert.Equal("Web UI", source);
        Assert.Equal(ChargeControlMode.FastNoBattery, Assert.Single(_actions.Starts).Mode);
    }

    [Fact]
    public void Converts_a_battery_target_once_when_the_button_is_pressed()
    {
        WithAKnownPack();
        CarReports(socPercent: 42);

        var page = RenderTab();

        page.Find("#fast-basis").Change(nameof(FastChargeBasis.Soc));
        page.Find("#fast-soc").Change("60");
        page.Find("#start-fast-no-battery").Click();

        var (limit, _) = Assert.Single(_fast.Sets);

        // (60 - 42) / 100 * 77000 / 0.9
        Assert.Equal(15_400, limit.RequiredEnergyWh, 0);
        Assert.Equal(60, limit.TargetSocPercent);
        Assert.Equal(42, limit.VehicleSocPercentAtRequest);
    }

    [Fact]
    public void Shows_what_a_battery_target_comes_to_before_it_is_pressed()
    {
        WithAKnownPack();
        CarReports(socPercent: 42);

        var page = RenderTab();
        page.Find("#fast-basis").Change(nameof(FastChargeBasis.Soc));
        page.Find("#fast-soc").Change("60");

        // (60 - 42) / 100 * 77000 / 0.9, said before anything is committed.
        Assert.Contains("15.4 kWh at the charger", page.Markup);
    }

    [Fact]
    public void Refuses_a_car_already_past_the_target_without_starting_anything()
    {
        WithAKnownPack();
        CarReports(socPercent: 64);

        var page = RenderTab();

        page.Find("#fast-basis").Change(nameof(FastChargeBasis.Soc));
        page.Find("#fast-soc").Change("60");
        page.Find("#start-fast-no-battery").Click();

        Assert.Contains("already at 64%", page.Find("#fast-error").TextContent);
        Assert.Empty(_actions.Starts);
        Assert.Empty(_fast.Sets);
    }

    [Fact]
    public void Refuses_an_energy_basis_with_nothing_in_the_box()
    {
        var page = RenderTab();

        page.Find("#fast-basis").Change(nameof(FastChargeBasis.Energy));
        page.Find("#fast-energy").Change("0");
        page.Find("#start-fast-no-battery").Click();

        Assert.Contains("how much energy", page.Find("#fast-error").TextContent);
        Assert.Empty(_actions.Starts);
    }

    [Fact]
    public void Going_back_to_full_clears_a_limit_from_an_earlier_charge()
    {
        _fast.Set(new FastChargeLimit(20_000, Now.AddHours(-3)), "earlier");

        var page = RenderTab();

        page.Find("#fast-basis").Change(nameof(FastChargeBasis.Full));
        page.Find("#start-fast-no-battery").Click();

        Assert.Contains("Web UI", _fast.Clears);
        Assert.Single(_actions.Starts);
    }

    [Fact]
    public void Shows_the_running_limit_rather_than_its_own_defaults()
    {
        // A second browser, or a limit set from Home Assistant, must not be offered to be overwritten.
        _fast.Set(new FastChargeLimit(18_000, Now), "Home Assistant");

        var page = RenderTab();

        Assert.Equal("18", page.Find("#fast-energy").GetAttribute("value"));
    }

    [Fact]
    public void A_refused_charger_leaves_no_limit_standing()
    {
        _actions.Failure = "The charger did not accept Fast — it is still in Green.";

        var page = RenderTab();

        page.Find("#fast-basis").Change(nameof(FastChargeBasis.Energy));
        page.Find("#fast-energy").Change("20");
        page.Find("#start-fast-no-battery").Click();

        page.WaitForAssertion(() => Assert.Contains("Web UI", _fast.Clears));
    }

    [Fact]
    public void Reports_progress_only_while_there_is_something_to_count_towards()
    {
        var page = RenderTab();
        Assert.Empty(page.FindAll("#fast-progress"));

        _holder.Set(Status(new FastChargeProgress(new FastChargeLimit(20_000, Now), 8_000)));

        page.WaitForAssertion(() =>
        {
            var narrative = page.Find("#fast-progress").TextContent;
            Assert.Contains("8.0 kWh of 20.0 kWh", narrative);
            Assert.Contains("12.0 kWh to go", narrative);
        });
    }

    [Fact]
    public void Says_a_met_limit_is_stopping_rather_than_still_counting()
    {
        var page = RenderTab();

        _holder.Set(Status(new FastChargeProgress(new FastChargeLimit(20_000, Now), 20_100)));

        page.WaitForAssertion(() =>
            Assert.Contains("has been delivered", page.Find("#fast-progress").TextContent));
    }

    // -- The departure (#122).

    [Fact]
    public void Offers_no_departure_box_under_full()
    {
        // There is nothing to time, so the factory would refuse it -- and a box that is only ever
        // refused is worse than a box that is not there.
        var page = RenderTab();

        Assert.Empty(page.FindAll("#fast-departure"));
    }

    [Fact]
    public void Offers_the_departure_box_once_an_amount_is_chosen()
    {
        var page = RenderTab();

        page.Find("#fast-basis").Change(nameof(FastChargeBasis.Energy));

        Assert.Single(page.FindAll("#fast-departure"));
    }

    [Fact]
    public void Carries_the_departure_into_the_limit()
    {
        var page = RenderTab();

        page.Find("#fast-basis").Change(nameof(FastChargeBasis.Energy));
        page.Find("#fast-energy").Change("30");
        page.Find("#fast-departure").Change("2026-08-11T07:00:00");
        page.Find("#start-fast-no-battery").Click();

        var (limit, _) = Assert.Single(_fast.Sets);
        Assert.True(limit.IsDeferred);
        Assert.Equal(7, TimeZoneInfo.ConvertTime(limit.DepartBy!.Value, Prague).Hour);
    }

    [Fact]
    public void An_empty_departure_charges_now()
    {
        var page = RenderTab();

        page.Find("#fast-basis").Change(nameof(FastChargeBasis.Energy));
        page.Find("#fast-energy").Change("30");
        page.Find("#start-fast-no-battery").Click();

        var (limit, _) = Assert.Single(_fast.Sets);
        Assert.False(limit.IsDeferred);
    }

    [Fact]
    public void Refuses_a_departure_in_the_past_without_starting_anything()
    {
        var page = RenderTab();

        page.Find("#fast-basis").Change(nameof(FastChargeBasis.Energy));
        page.Find("#fast-energy").Change("30");
        page.Find("#fast-departure").Change("2026-08-10T21:00:00");
        page.Find("#start-fast-no-battery").Click();

        Assert.Contains("in the past", page.Find("#fast-error").TextContent);
        Assert.Empty(_actions.Starts);
    }

    [Fact]
    public void Says_what_it_is_waiting_for()
    {
        var page = RenderTab();

        _holder.Set(Status(Deferred(deliveredWh: 0)));

        page.WaitForAssertion(() =>
        {
            var narrative = page.Find("#fast-schedule").TextContent;
            Assert.Contains("Waiting until", narrative);
            Assert.Contains("the car has not drawn yet", narrative);
        });
    }

    [Fact]
    public void Says_when_the_time_does_not_fit()
    {
        var page = RenderTab();

        // 30 kWh wanted in an hour: it does not fit, and saying so beats a plan that looks punctual.
        _holder.Set(Status(Deferred(departIn: TimeSpan.FromHours(1))));

        page.WaitForAssertion(() =>
            Assert.Contains("Not enough time", page.Find("#fast-schedule").TextContent));
    }

    private static FastChargeProgress Deferred(double deliveredWh = 0, TimeSpan? departIn = null)
    {
        var limit = new FastChargeLimit(30_000, Now, DepartBy: Now + (departIn ?? TimeSpan.FromHours(9)));

        return new FastChargeProgress(
            limit,
            deliveredWh,
            FastChargePlanner.Plan(limit, deliveredWh, null, 11_040, TimeSpan.FromMinutes(15), Now));
    }

    private static ChargeControlStatus Status(FastChargeProgress progress) => new(
        Mode: ChargeControlMode.FastNoBattery,
        DryRun: false,
        HoldingControl: true,
        State: ChargeControlState.Charging,
        SurplusWatts: 0,
        TargetCurrentAmps: 16,
        ActiveCurrentAmps: 16,
        BatterySocPercent: 60,
        ChargerStatus: EvChargerStatus.Charging,
        CarConnected: true,
        SolarPowerWatts: 0,
        ForecastSolarPowerWatts: 0,
        EvChargerPowerWatts: 11_040,
        EvChargingCurrentAmps: 16,
        BatteryPowerWatts: 0,
        GridPowerWatts: 11_040,
        BatteryHoldEnabled: true,
        BatteryHoldRequested: true,
        BatteryHoldActive: true,
        BatteryHoldTargetWatts: 0,
        Plan: null,
        LoanPowerWatts: 0,
        SessionEnergyWh: 0,
        LoanedTodayWh: 0,
        TomorrowForecastWh: null,
        Timestamp: Now,
        FastCharge: progress);
}