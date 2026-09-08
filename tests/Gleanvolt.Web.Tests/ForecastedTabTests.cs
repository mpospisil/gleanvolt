using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The forecast plan as one coherent view instead of a dozen loosely related entities (#50), now the
/// Forecasted tab of the charging-plan page (#98) rather than a page of its own — so these tests
/// render the page and ask for that tab, which is also what exercises the wiring between them.
/// The four runtime numbers the plan reads moved here from the dashboard with it.
///
/// JSInterop is Loose: the timeline chart is rendered by vendored JS (uPlot) this suite cannot see,
/// so these tests cover the data and markup around it, not the rendered pixels.
/// </summary>
public class ForecastedTabTests : PageTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly ChargeControlStatusHolder _holder = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero), Prague);
    private readonly FakeChargeControlModeSelector _mode = new();
    private readonly FakeChargeActions _actions;
    private readonly FakeBatteryHoldSelector _batteryHold = new();
    private readonly FakeForecastRuntimeSettings _forecast = new();

    public ForecastedTabTests()
    {
        _actions = new FakeChargeActions(_mode);

        Services.AddSingleton(_holder);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IChargeControlModeSelector>(_mode);
        Services.AddSingleton<IChargeActions>(_actions);
        Services.AddSingleton<IBatteryHoldSelector>(_batteryHold);
        Services.AddSingleton<IForecastRuntimeSettings>(_forecast);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ChargingPlan> RenderTab() =>
        Render<ChargingPlan>(parameters => parameters.Add(p => p.Tab, "forecasted"));

    [Fact]
    public void Says_so_before_the_first_poll_has_landed()
    {
        var page = RenderTab();

        Assert.Contains("No poll has completed yet", page.Markup);
    }

    [Fact]
    public void Shows_an_explicit_empty_state_when_a_different_mode_is_driving()
    {
        // Plan is null whenever Mode isn't Forecasted (PollingService only publishes it for that
        // mode) -- the page must not show a stale plan from whatever ran earlier.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with { Plan = null });

        var page = RenderTab();

        Assert.Contains("only shown while", page.Markup);
        Assert.Contains("Solar", page.Markup);

        // The stat grid, not the words in it: the controls above now name the figures they feed
        // ("it sets Day outlook"), so a text search would find the label without a plan behind it.
        Assert.Empty(page.FindAll(".stat-grid"));

        // The controls the mode reads are not part of the plan, and stay reachable whatever is driving.
        Assert.NotEmpty(page.FindAll("#daily-ev-target"));
    }

    [Fact]
    public void Shows_all_eleven_plan_figures_when_forecasted_is_driving()
    {
        var plan = TestPlans.Usable(_time.Now, outlook: DayOutlook.Tight);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with
        {
            Plan = plan,
            TomorrowForecastWh = 12_000,
            LoanedTodayWh = 800,
        });

        var page = RenderTab();

        Assert.Contains("Day outlook", page.Markup);
        Assert.Contains("Tight", page.Markup);
        Assert.Contains("Plan state", page.Markup);
        Assert.Contains(plan.Reason, page.Markup);
        Assert.Contains("Charge window", page.Markup);
        Assert.Contains("EV energy budget", page.Markup);
        Assert.Contains("4.5 kWh", page.Markup);
        Assert.Contains("EV energy expected today", page.Markup);
        Assert.Contains("6.0 kWh", page.Markup);
        Assert.Contains("Projected shortfall", page.Markup);
        Assert.Contains("1.0 kWh", page.Markup);
        Assert.Contains("Required SOC floor", page.Markup);
        Assert.Contains("62%", page.Markup);
        Assert.Contains("Forecast remaining today", page.Markup);
        Assert.Contains("9.0 kWh", page.Markup);
        Assert.Contains("Tomorrow forecast", page.Markup);
        Assert.Contains("12.0 kWh", page.Markup);
        Assert.Contains("Forecast accuracy", page.Markup);
        Assert.Contains("97%", page.Markup);
        Assert.Contains("Battery loaned today", page.Markup);
        Assert.Contains("0.8 kWh", page.Markup);
    }

    [Fact]
    public void Formats_the_charge_window_in_local_time()
    {
        var window = (
            new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
        var plan = TestPlans.Usable(_time.Now, window: window);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = plan });

        var page = RenderTab();

        // 10:00-12:00 UTC is 12:00-14:00 in Prague in August.
        Assert.Contains("12:00", page.Markup);
        Assert.Contains("14:00", page.Markup);
    }

    [Fact]
    public void Reports_no_window_as_none_rather_than_blank()
    {
        var plan = TestPlans.Usable(_time.Now) with { NextFeasibleWindow = null };
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = plan });

        var page = RenderTab();

        Assert.Contains("none", page.Markup);
    }

    [Fact]
    public void Shows_the_timeline_chart_container_when_the_plan_has_one()
    {
        var plan = TestPlans.Usable(_time.Now);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = plan });

        var page = RenderTab();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#plan-chart")));
    }

    [Fact]
    public void Explains_a_forecasted_mode_with_no_usable_plan_yet_instead_of_hiding_the_page()
    {
        // Forecasted is selected but no forecast has arrived: Plan is not null (unlike the
        // wrong-mode case), it's just IsUsable=false. The reason explains the degraded state, and
        // the chart is skipped since there's nothing to plot.
        var plan = TestPlans.Unavailable(_time.Now);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = plan });

        var page = RenderTab();

        Assert.Contains("no forecast fetched yet", page.Markup);
        Assert.Contains("No forecast data yet", page.Markup);
        Assert.Empty(page.FindAll("#plan-chart"));
    }

    [Fact]
    public void Reports_no_tomorrow_forecast_as_a_dash()
    {
        var plan = TestPlans.Usable(_time.Now);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with
        {
            Plan = plan,
            TomorrowForecastWh = null,
        });

        var page = RenderTab();

        Assert.Contains("—", page.Markup);
    }

    [Fact]
    public void Follows_the_holder_instead_of_sampling_it_once()
    {
        var page = RenderTab();
        Assert.Contains("No poll has completed yet", page.Markup);

        var plan = TestPlans.Usable(_time.Now);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = plan });

        page.WaitForAssertion(() => Assert.Contains("Day outlook", page.Markup));
    }

    [Fact]
    public void Stops_following_the_holder_once_the_circuit_is_gone()
    {
        var page = RenderTab();
        page.Dispose();

        var exception = Record.Exception(() => _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted)));

        Assert.Null(exception);
    }

    [Fact]
    public void Shows_the_runtime_numbers_from_the_forecast_settings()
    {
        _forecast.SetDailyEvTargetWh(12_000, "test setup");
        _forecast.SetSessionEnergyTargetWh(5_000, "test setup");
        _forecast.SetMinBatterySocFloorPercent(55, "test setup");
        _forecast.SetFloorResumeMarginPercent(6, "test setup");

        var page = RenderTab();

        Assert.Equal("12", page.Find("#daily-ev-target").GetAttribute("value"));
        Assert.Equal("5", page.Find("#session-energy-target").GetAttribute("value"));
        Assert.Equal("55", page.Find("#min-battery-soc").GetAttribute("value"));
        Assert.Equal("6", page.Find("#resume-margin").GetAttribute("value"));
    }

    [Fact]
    public void Changing_the_daily_ev_target_drives_the_settings_in_watt_hours()
    {
        var page = RenderTab();

        page.Find("#daily-ev-target").Change("18.5");

        Assert.Equal(18_500, _forecast.DailyEvTargetWh, precision: 3);
        Assert.Contains(_forecast.Sets, s => s.Setting == "DailyEvTargetWh" && s.Source == "Web UI");
    }

    [Fact]
    public void Changing_the_session_energy_target_drives_the_settings()
    {
        var page = RenderTab();

        page.Find("#session-energy-target").Change("0");

        Assert.Equal(0, _forecast.SessionEnergyTargetWh);
        Assert.Contains(_forecast.Sets, s => s.Setting == "SessionEnergyTargetWh" && s.Source == "Web UI");
    }

    [Fact]
    public void Changing_the_minimum_battery_soc_drives_the_settings()
    {
        var page = RenderTab();

        page.Find("#min-battery-soc").Change("40");

        Assert.Equal(40, _forecast.MinBatterySocFloorPercent);
        Assert.Contains(_forecast.Sets, s => s.Setting == "MinBatterySocFloorPercent" && s.Source == "Web UI");
    }

    [Fact]
    public void Changing_the_resume_margin_drives_the_settings()
    {
        var page = RenderTab();

        page.Find("#resume-margin").Change("8");

        Assert.Equal(8, _forecast.FloorResumeMarginPercent);
        Assert.Contains(_forecast.Sets, s => s.Setting == "FloorResumeMarginPercent" && s.Source == "Web UI");
    }

    [Fact]
    public void Picks_up_a_runtime_number_changed_by_another_surface()
    {
        var page = RenderTab();
        Assert.Equal("15", page.Find("#daily-ev-target").GetAttribute("value"));

        // Home Assistant (or another browser tab) changes it; the next poll should carry it here.
        _forecast.SetDailyEvTargetWh(9_000, "Home Assistant");
        _holder.Set(Statuses.Sample(_time.Now));

        page.WaitForAssertion(() => Assert.Equal("9", page.Find("#daily-ev-target").GetAttribute("value")));
    }

    // The two energy numbers read alike and behave nothing alike (#173): the daily target is a
    // yardstick the plan reports against, the session target is the only hard stop. The verdict is
    // what the daily target buys -- one sentence instead of three tiles -- and the session line is
    // what the ceiling has left.
    [Fact]
    public void Says_in_one_sentence_how_today_stands_against_what_the_owner_drives()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with
        {
            Plan = TestPlans.Usable(_time.Now, outlook: DayOutlook.Tight),
        });

        var page = RenderTab();

        // The sample plan expects 6 kWh into the car against a 15 kWh target.
        Assert.Contains("Today covers 6.0 kWh of the 15.0 kWh you drive — 9.0 kWh short", page.Markup);
    }

    [Fact]
    public void Calls_a_day_that_reaches_the_target_covered_rather_than_short()
    {
        var plan = TestPlans.Usable(_time.Now, outlook: DayOutlook.Surplus) with
        {
            EvExpectedTodayWh = 15_000,
            EvTargetWh = 15_000,
        };
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = plan });

        var page = RenderTab();

        Assert.Contains("Today covers the full 15.0 kWh you drive", page.Markup);
        Assert.DoesNotContain("kWh short", page.Markup);
    }

    [Fact]
    public void Says_the_car_gets_nothing_on_a_day_with_no_window()
    {
        var plan = TestPlans.Usable(_time.Now, outlook: DayOutlook.NoChargeToday) with { NextFeasibleWindow = null };
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = plan });

        var page = RenderTab();

        Assert.Contains("no chargeable window", page.Markup);
        Assert.Contains("the battery keeps priority", page.Markup);
    }

    [Fact]
    public void Judges_nothing_when_no_daily_target_is_set()
    {
        // 0 makes the outlook permanently Surplus, so a verdict phrased against the target would be
        // a lie dressed as good news.
        var plan = TestPlans.Usable(_time.Now, outlook: DayOutlook.Surplus) with { EvTargetWh = 0 };
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = plan });

        var page = RenderTab();

        Assert.Contains("No daily target set", page.Markup);
    }

    [Fact]
    public void Counts_the_session_off_against_its_ceiling()
    {
        _forecast.SetSessionEnergyTargetWh(10_000, "test setup");
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { SessionEnergyWh = 4_000 });

        var page = RenderTab();

        Assert.Contains("4.0 kWh of 10.0 kWh in this session", page.Markup);
    }

    [Fact]
    public void Says_the_ceiling_is_what_is_holding_the_car_once_it_is_reached()
    {
        _forecast.SetSessionEnergyTargetWh(10_000, "test setup");
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { SessionEnergyWh = 10_400 });

        var page = RenderTab();

        Assert.Contains("reached, so the car stays paused until you unplug it", page.Markup);
    }

    [Fact]
    public void Reports_a_zero_session_target_as_no_limit_rather_than_a_0_kwh_one()
    {
        // 0 is unlimited, so "0.0 kWh of 0.0 kWh" would read as a charge that can never run.
        _forecast.SetSessionEnergyTargetWh(0, "test setup");
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { SessionEnergyWh = 3_000 });

        var page = RenderTab();

        Assert.Contains("No limit set. 3.0 kWh into the car since it was plugged in", page.Markup);
    }
}
