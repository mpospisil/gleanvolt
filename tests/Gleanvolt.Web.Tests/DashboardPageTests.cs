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
    private readonly FakeBatteryHoldSelector _batteryHold = new();
    private readonly FakeForecastRuntimeSettings _forecast = new();

    public DashboardPageTests()
    {
        Services.AddSingleton(_holder);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IChargeControlModeSelector>(_mode);
        Services.AddSingleton<IBatteryHoldSelector>(_batteryHold);
        Services.AddSingleton<IForecastRuntimeSettings>(_forecast);
    }

    [Fact]
    public void Says_so_before_the_first_poll_has_landed()
    {
        var page = Render<Dashboard>();

        Assert.Contains("No poll has completed yet", page.Markup);
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
    public void Shows_the_charge_mode_select_with_the_current_mode_chosen()
    {
        _mode.Set(ChargeControlMode.Forecasted, "test setup");

        var page = Render<Dashboard>();

        var select = page.Find("#mode");
        Assert.Equal("Forecasted", select.GetAttribute("value"));

        var options = select.Children.Select(o => o.GetAttribute("value")).ToList();
        Assert.Equal(["Off", "Solar", "Forecasted", "FastNoBattery"], options);
    }

    [Fact]
    public void Selecting_a_mode_drives_the_selector_with_the_web_ui_as_source()
    {
        var page = Render<Dashboard>();

        page.Find("#mode").Change("Solar");

        Assert.Equal(ChargeControlMode.Solar, _mode.Mode);
        Assert.Contains(_mode.Sets, s => s.Mode == ChargeControlMode.Solar && s.Source == "Web UI");
    }

    [Fact]
    public void Reflects_a_mode_changing_underneath_it()
    {
        // FastNoBattery switches itself off when the car finishes -- the polling loop calls Set,
        // not this page, and the select still has to catch up.
        var page = Render<Dashboard>();

        _mode.Set(ChargeControlMode.FastNoBattery, "the polling loop");
        page.WaitForAssertion(() => Assert.Equal("FastNoBattery", page.Find("#mode").GetAttribute("value")));

        _mode.Set(ChargeControlMode.Off, "the polling loop (charging finished)");

        page.WaitForAssertion(() => Assert.Equal("Off", page.Find("#mode").GetAttribute("value")));
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

        var page = Render<Dashboard>();

        Assert.Equal("12", page.Find("#daily-ev-target").GetAttribute("value"));
        Assert.Equal("5", page.Find("#session-energy-target").GetAttribute("value"));
        Assert.Equal("55", page.Find("#min-battery-soc").GetAttribute("value"));
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
