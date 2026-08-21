using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Phase 1 (#46) is the read-only telemetry stat-grid; phase 3 (#48) adds the controls section
/// below it, driving the same Core selector interfaces the MQTT worker uses. Follows the same
/// holder-subscription seam <see cref="HealthPageTests"/> already covers.
/// </summary>
public class DashboardPageTests : BunitContext
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly ChargeControlStatusHolder _holder = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero), Prague);
    private readonly FakeChargeControlModeSelector _mode = new();
    private readonly FakeChargeActions _actions;
    private readonly FakeBatteryHoldSelector _batteryHold = new();
    private readonly FakeForecastRuntimeSettings _forecast = new();

    // Vehicle telemetry (#73) is read through the same holder seam. Registered empty by default, so the
    // pre-existing tests below assert the page's behaviour with no car configured -- which is what an
    // install with Vehicle:Enabled=false looks like.
    private readonly VehicleStateHolder _vehicle = new();

    public DashboardPageTests()
    {
        _actions = new FakeChargeActions(_mode);

        Services.AddSingleton(_holder);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IChargeControlModeSelector>(_mode);
        Services.AddSingleton<IChargeActions>(_actions);
        Services.AddSingleton<IBatteryHoldSelector>(_batteryHold);
        Services.AddSingleton<IForecastRuntimeSettings>(_forecast);
        Services.AddSingleton<IVehicleTelemetry>(_vehicle);
        Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12)));
    }

    [Fact]
    public void Says_so_before_the_first_poll_has_landed()
    {
        var page = Render<Dashboard>();

        Assert.Contains("No poll has completed yet", page.Markup);
    }

    [Fact]
    public void Hides_the_vehicle_section_entirely_when_no_car_has_reported()
    {
        // Vehicle:Enabled=false must not leave an empty card on the dashboard of an install that has
        // no car configured at all.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.DoesNotContain("Car battery", page.Markup);
        Assert.DoesNotContain("Car reading age", page.Markup);
    }

    [Fact]
    public void Shows_the_cars_soc_alongside_the_age_of_the_reading()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(
            _time.Now.AddHours(-3.5),
            SocPercent: 28,
            ChargeState: VehicleChargeState.Idle,
            PlugState: VehiclePlugState.Disconnected,
            SourceId: "id4"));

        var page = Render<Dashboard>();

        Assert.Contains("Car battery", page.Markup);
        Assert.Contains("28%", page.Markup);
        // The age is a first-class value, not a hint: it is what decides whether the SOC means anything.
        Assert.Contains("3.5 h", page.Markup);
        Assert.Contains("id4", page.Markup);
        Assert.Contains("Disconnected", page.Markup);
    }

    [Fact]
    public void Marks_the_reading_stale_once_it_passes_max_age()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddHours(-30), SocPercent: 28));

        var page = Render<Dashboard>();

        Assert.Contains("stale", page.Markup);
    }

    [Fact]
    public void Does_not_call_a_recent_reading_stale()
    {
        // Hours old and parked is the normal case, not a fault -- a parked car's SOC does not drift.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddHours(-3.5), SocPercent: 28));

        var page = Render<Dashboard>();

        Assert.DoesNotContain("stale", page.Markup);
    }

    [Fact]
    public void Shows_a_dash_for_a_source_that_does_not_report_soc()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-20)));

        var page = Render<Dashboard>();

        Assert.Contains("Car battery", page.Markup);
        Assert.Contains("20 min", page.Markup);
        Assert.Contains("Unknown", page.Markup);
    }

    [Fact]
    public void Shows_the_core_telemetry_fields()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with
        {
            SolarPowerWatts = 3200,
            SurplusWatts = 1800,
            BatterySocPercent = 72,
            BatteryPowerWatts = 450,
            GridPowerWatts = -300,
            EvChargerPowerWatts = 2760,
            EvChargingCurrentAmps = 12,
            TargetCurrentAmps = 12,
            ActiveCurrentAmps = 12,
            ChargerStatus = EvChargerStatus.Charging,
            CarConnected = true,
        });

        var page = Render<Dashboard>();

        Assert.Contains("Solar", page.Markup);
        Assert.Contains("3200 W", page.Markup);
        Assert.Contains("1800 W", page.Markup);
        Assert.Contains("72%", page.Markup);
        Assert.Contains("450 W", page.Markup);
        Assert.Contains("-300 W", page.Markup);
        Assert.Contains("2760 W", page.Markup);
        Assert.Contains("12 A", page.Markup);
        Assert.Contains("Charging", page.Markup);
        Assert.Contains("Yes", page.Markup);
    }

    [Fact]
    public void Blanks_the_surplus_and_current_fields_when_the_mode_isnt_deciding_on_them()
    {
        _holder.Set(Statuses.Sample(_time.Now) with
        {
            SurplusWatts = null,
            TargetCurrentAmps = null,
            ActiveCurrentAmps = null,
        });

        var page = Render<Dashboard>();

        Assert.Contains("—", page.Markup);
    }

    [Fact]
    public void Follows_the_holder_instead_of_sampling_it_once()
    {
        var page = Render<Dashboard>();
        Assert.Contains("No poll has completed yet", page.Markup);

        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted));

        page.WaitForAssertion(() => Assert.Contains("Forecasted", page.Markup));
    }

    [Fact]
    public void Stops_following_the_holder_once_the_circuit_is_gone()
    {
        var page = Render<Dashboard>();
        page.Dispose();

        var exception = Record.Exception(() => _holder.Set(Statuses.Sample(_time.Now)));

        Assert.Null(exception);
    }

    [Fact]
    public void Shows_the_forecast_solar_power_beside_the_measured_one()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with
        {
            SolarPowerWatts = 3200,
            ForecastSolarPowerWatts = 4100,
        });

        var page = Render<Dashboard>();

        Assert.Contains("Forecast solar power", page.Markup);
        Assert.Contains("3200 W", page.Markup);
        Assert.Contains("4100 W", page.Markup);
    }

    [Fact]
    public void Shows_a_zero_forecast_when_none_covers_this_moment()
    {
        // No forecast fetched yet is not an error state and must not blank the tile -- the pair is
        // meant to be readable at a glance, including before the first fetch of the day lands.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with
        {
            SolarPowerWatts = 3200,
            ForecastSolarPowerWatts = 0,
        });

        var page = Render<Dashboard>();

        Assert.Contains("Forecast solar power", page.Markup);
        Assert.Contains("0 W", page.Markup);
    }

    [Fact]
    public void Offers_no_charge_mode_select_at_all()
    {
        // #89: the mode is what a button did, not a setting to pick.
        var page = Render<Dashboard>();

        Assert.Empty(page.FindAll("select#mode"));
        Assert.Empty(page.FindAll("select"));
    }

    [Fact]
    public void Reports_the_current_mode_as_a_read_only_line()
    {
        _mode.Set(ChargeControlMode.Forecasted, "test setup");

        var page = Render<Dashboard>();

        Assert.Equal("Forecasted", page.Find("#mode-state .mode").TextContent);
    }

    [Theory]
    [InlineData("#start-solar", ChargeControlMode.Solar)]
    [InlineData("#start-forecasted", ChargeControlMode.Forecasted)]
    [InlineData("#start-fast-no-battery", ChargeControlMode.FastNoBattery)]
    public void Each_button_starts_its_own_strategy_with_the_web_ui_as_source(string selector, ChargeControlMode expected)
    {
        var page = Render<Dashboard>();

        page.Find(selector).Click();

        Assert.Contains(_actions.Starts, s => s.Mode == expected && s.Source == "Web UI");
        Assert.Equal(expected, _mode.Mode);
    }

    [Fact]
    public void There_is_no_button_for_targeted_here()
    {
        // It needs an amount and a departure, so its press lives on /targeted where those are typed.
        var page = Render<Dashboard>();

        Assert.Empty(page.FindAll("#start-targeted"));
    }

    [Fact]
    public void The_off_button_stops_charging_whatever_was_running()
    {
        _mode.Set(ChargeControlMode.FastNoBattery, "test setup");
        var page = Render<Dashboard>();

        page.Find("#charge-off").Click();

        Assert.Contains("Web UI", _actions.Stops);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public void A_charger_that_refuses_the_use_mode_write_says_so_and_leaves_the_mode_alone()
    {
        _actions.Failure = "The charger did not accept Fast — it is still in Green.";
        var page = Render<Dashboard>();

        page.Find("#start-solar").Click();

        page.WaitForAssertion(() => Assert.Contains("did not accept Fast", page.Find("p.error").TextContent));
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public void Says_when_and_from_where_the_running_mode_was_started()
    {
        var page = Render<Dashboard>();

        page.Find("#start-solar").Click();

        // 10:00 UTC is noon in Prague, which is the clock this page formats in.
        page.WaitForAssertion(() =>
            Assert.Contains("started from the Web UI at 12:00", page.Find("#mode-state").TextContent));
    }

    [Fact]
    public void Reflects_a_mode_changing_underneath_it()
    {
        // FastNoBattery switches itself off when the car finishes -- the polling loop calls Set,
        // not this page, and the line still has to catch up.
        var page = Render<Dashboard>();

        _mode.Set(ChargeControlMode.FastNoBattery, "the polling loop");
        page.WaitForAssertion(() => Assert.Equal("FastNoBattery", page.Find("#mode-state .mode").TextContent));

        _mode.Set(ChargeControlMode.Off, "the polling loop (charging finished)");

        page.WaitForAssertion(() => Assert.Equal("Off", page.Find("#mode-state .mode").TextContent));
    }

    [Fact]
    public void A_mode_that_ends_itself_takes_this_pages_note_with_it()
    {
        // "started from the Web UI at 12:00" is true of the action that set it, and of nothing else.
        var page = Render<Dashboard>();
        page.Find("#start-fast-no-battery").Click();
        page.WaitForAssertion(() => Assert.Contains("started from the Web UI", page.Find("#mode-state").TextContent));

        _mode.Set(ChargeControlMode.Off, "the polling loop (charging finished)");

        page.WaitForAssertion(() =>
            Assert.DoesNotContain("started from the Web UI", page.Find("#mode-state").TextContent));
    }

    [Fact]
    public void Hides_the_battery_hold_switch_before_the_first_poll()
    {
        var page = Render<Dashboard>();

        Assert.Empty(page.FindAll("input[type=checkbox]"));
    }

    [Fact]
    public void Hides_the_battery_hold_switch_when_the_feature_is_disabled()
    {
        _holder.Set(Statuses.Sample(_time.Now) with { BatteryHoldEnabled = false });

        var page = Render<Dashboard>();

        Assert.Empty(page.FindAll("input[type=checkbox]"));
    }

    [Fact]
    public void Shows_the_battery_hold_switch_reflecting_what_was_actually_armed()
    {
        // Requested true but the write failed, so nothing is really held -- the switch must show
        // the device truth (BatteryHoldActive), not the request (BatteryHoldRequested).
        _batteryHold.Set(true, "test setup");
        _holder.Set(Statuses.Sample(_time.Now) with
        {
            BatteryHoldEnabled = true,
            BatteryHoldRequested = true,
            BatteryHoldActive = false,
        });

        var page = Render<Dashboard>();

        var checkbox = page.Find("input[type=checkbox]");
        Assert.Null(checkbox.GetAttribute("checked"));
    }

    [Fact]
    public void Toggling_the_battery_hold_switch_drives_the_selector()
    {
        _holder.Set(Statuses.Sample(_time.Now) with { BatteryHoldEnabled = true, BatteryHoldActive = false });
        var page = Render<Dashboard>();

        page.Find("input[type=checkbox]").Change(true);

        Assert.True(_batteryHold.Hold);
        Assert.Contains(_batteryHold.Sets, s => s.Hold && s.Source == "Web UI");
    }

    [Fact]
    public void A_failed_write_shows_the_battery_hold_switch_springing_back()
    {
        _holder.Set(Statuses.Sample(_time.Now) with { BatteryHoldEnabled = true, BatteryHoldActive = false });
        var page = Render<Dashboard>();
        page.Find("input[type=checkbox]").Change(true);

        // The next poll reports the write never actually landed on the inverter.
        _holder.Set(Statuses.Sample(_time.Now) with { BatteryHoldEnabled = true, BatteryHoldActive = false });

        page.WaitForAssertion(() => Assert.Null(page.Find("input[type=checkbox]").GetAttribute("checked")));
    }

    [Fact]
    public void Shows_the_runtime_numbers_from_the_forecast_settings()
    {
        _forecast.SetDailyEvTargetWh(12_000, "test setup");
        _forecast.SetSessionEnergyTargetWh(5_000, "test setup");
        _forecast.SetMinBatterySocFloorPercent(55, "test setup");
        _forecast.SetFloorResumeMarginPercent(6, "test setup");

        var page = Render<Dashboard>();

        Assert.Equal("12", page.Find("#daily-ev-target").GetAttribute("value"));
        Assert.Equal("5", page.Find("#session-energy-target").GetAttribute("value"));
        Assert.Equal("55", page.Find("#min-battery-soc").GetAttribute("value"));
        Assert.Equal("6", page.Find("#resume-margin").GetAttribute("value"));
    }

    [Fact]
    public void Changing_the_daily_ev_target_drives_the_settings_in_watt_hours()
    {
        var page = Render<Dashboard>();

        page.Find("#daily-ev-target").Change("18.5");

        Assert.Equal(18_500, _forecast.DailyEvTargetWh, precision: 3);
        Assert.Contains(_forecast.Sets, s => s.Setting == "DailyEvTargetWh" && s.Source == "Web UI");
    }

    [Fact]
    public void Changing_the_session_energy_target_drives_the_settings()
    {
        var page = Render<Dashboard>();

        page.Find("#session-energy-target").Change("0");

        Assert.Equal(0, _forecast.SessionEnergyTargetWh);
        Assert.Contains(_forecast.Sets, s => s.Setting == "SessionEnergyTargetWh" && s.Source == "Web UI");
    }

    [Fact]
    public void Changing_the_minimum_battery_soc_drives_the_settings()
    {
        var page = Render<Dashboard>();

        page.Find("#min-battery-soc").Change("40");

        Assert.Equal(40, _forecast.MinBatterySocFloorPercent);
        Assert.Contains(_forecast.Sets, s => s.Setting == "MinBatterySocFloorPercent" && s.Source == "Web UI");
    }

    [Fact]
    public void Changing_the_resume_margin_drives_the_settings()
    {
        var page = Render<Dashboard>();

        page.Find("#resume-margin").Change("8");

        Assert.Equal(8, _forecast.FloorResumeMarginPercent);
        Assert.Contains(_forecast.Sets, s => s.Setting == "FloorResumeMarginPercent" && s.Source == "Web UI");
    }

    [Fact]
    public void Picks_up_a_runtime_number_changed_by_another_surface()
    {
        var page = Render<Dashboard>();
        Assert.Equal("15", page.Find("#daily-ev-target").GetAttribute("value"));

        // Home Assistant (or another browser tab) changes it; the next poll should carry it here.
        _forecast.SetDailyEvTargetWh(9_000, "Home Assistant");
        _holder.Set(Statuses.Sample(_time.Now));

        page.WaitForAssertion(() => Assert.Equal("9", page.Find("#daily-ev-target").GetAttribute("value")));
    }
}
