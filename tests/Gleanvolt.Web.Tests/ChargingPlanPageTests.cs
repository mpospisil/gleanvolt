using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The page that starts, stops and shapes a charge (#98): the header common to every mode, the tab
/// strip, and the two tabs that are nothing but a button and some figures. The forecast tab's plan
/// and the targeted tab's request have suites of their own —
/// <see cref="ForecastedTabTests"/> and <see cref="TargetedTabTests"/> — because they were pages of
/// their own until this one absorbed them.
///
/// JSInterop is Loose throughout: the forecast tab draws its timeline with vendored JS (uPlot) that
/// this suite cannot see, and it is reachable from here by a tab click.
/// </summary>
public class ChargingPlanPageTests : PageTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly ChargeControlStatusHolder _holder = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero), Prague);
    private readonly FakeChargeControlModeSelector _mode = new();
    private readonly FakeChargeActions _actions;
    private readonly FakeBatteryHoldSelector _batteryHold = new();
    private readonly FakeForecastRuntimeSettings _forecast = new();
    private readonly FakeTargetedChargeSelector _target = new();

    public ChargingPlanPageTests()
    {
        _actions = new FakeChargeActions(_mode);

        Services.AddSingleton(_holder);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IChargeControlModeSelector>(_mode);
        Services.AddSingleton<IChargeActions>(_actions);
        Services.AddSingleton<IBatteryHoldSelector>(_batteryHold);
        Services.AddSingleton<IForecastRuntimeSettings>(_forecast);
        Services.AddSingleton<ITargetedChargeSelector>(_target);
        Services.AddSingleton<ITargetedChargePreview>(new FakeTargetedChargePreview());
        Services.AddSingleton<IVehicleTelemetry>(new VehicleStateHolder());
        Services.AddSingleton(new TargetedDisplayOptions(TimeSpan.FromHours(36)));
        Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12)));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ChargingPlan> RenderTab(string? tab = null) =>
        Render<ChargingPlan>(parameters => parameters.Add(p => p.Tab, tab));

    [Fact]
    public void Offers_one_tab_per_mode_and_none_for_off()
    {
        var page = RenderTab();

        Assert.Equal(
            ["Solar", "Forecasted", "Fast (no battery)", "Targeted"],
            page.FindAll("nav.tabs a").Select(a => a.TextContent.Trim()));
    }

    [Fact]
    public void Opens_on_solar_when_nothing_is_running()
    {
        var page = RenderTab();

        Assert.Equal("tab-solar", page.Find("nav.tabs a.active").Id);
        Assert.NotEmpty(page.FindAll("#start-solar"));
    }

    [Fact]
    public void Opens_on_the_mode_that_is_actually_driving()
    {
        // Arriving with no tab in the URL, the tab worth seeing is nearly always the running one.
        _mode.Set(ChargeControlMode.FastNoBattery, "test setup");

        var page = RenderTab();

        Assert.Equal("tab-fast", page.Find("nav.tabs a.active").Id);
    }

    [Fact]
    public void Shows_the_tab_the_url_asks_for_whatever_is_running()
    {
        _mode.Set(ChargeControlMode.Solar, "test setup");

        var page = RenderTab("forecasted");

        Assert.Equal("tab-forecasted", page.Find("nav.tabs a.active").Id);
        Assert.NotEmpty(page.FindAll("#start-forecasted"));
    }

    [Fact]
    public void Marks_the_running_mode_in_the_strip_so_it_is_findable_without_reading_the_state_line()
    {
        _mode.Set(ChargeControlMode.Targeted, "test setup");

        var page = RenderTab("solar");

        Assert.Equal("tab-targeted", page.Find("nav.tabs a .running").ParentElement!.Id);
    }

    [Fact]
    public void Falls_back_to_a_real_tab_when_the_url_names_one_that_does_not_exist()
    {
        var page = RenderTab("nonsense");

        Assert.Equal("tab-solar", page.Find("nav.tabs a.active").Id);
    }

    [Fact]
    public void Reports_the_current_mode_as_a_read_only_line()
    {
        _mode.Set(ChargeControlMode.Forecasted, "test setup");

        var page = RenderTab();

        Assert.Equal("Forecasted", page.Find("#mode-state .mode").TextContent);
        Assert.Empty(page.FindAll("select"));
    }

    [Theory]
    [InlineData("solar", "#start-solar", ChargeControlMode.Solar)]
    [InlineData("forecasted", "#start-forecasted", ChargeControlMode.Forecasted)]
    [InlineData("fast", "#start-fast-no-battery", ChargeControlMode.FastNoBattery)]
    public void Each_tabs_button_starts_its_own_strategy_with_the_web_ui_as_source(
        string tab, string selector, ChargeControlMode expected)
    {
        var page = RenderTab(tab);

        page.Find(selector).Click();

        Assert.Contains(_actions.Starts, s => s.Mode == expected && s.Source == "Web UI");
        Assert.Equal(expected, _mode.Mode);
    }

    [Fact]
    public void The_off_button_is_common_to_every_tab_and_stops_whatever_was_running()
    {
        _mode.Set(ChargeControlMode.FastNoBattery, "test setup");
        var page = RenderTab("targeted");

        page.Find("#charge-off").Click();

        Assert.Contains("Web UI", _actions.Stops);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public void A_charger_that_refuses_the_use_mode_write_says_so_and_leaves_the_mode_alone()
    {
        _actions.Failure = "The charger did not accept Fast — it is still in Green.";
        var page = RenderTab("solar");

        page.Find("#start-solar").Click();

        page.WaitForAssertion(() => Assert.Contains("did not accept Fast", page.Find("p.error").TextContent));
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public void Says_when_and_from_where_the_running_mode_was_started()
    {
        var page = RenderTab("solar");

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
        var page = RenderTab();

        _mode.Set(ChargeControlMode.FastNoBattery, "the polling loop");
        page.WaitForAssertion(() => Assert.Equal("FastNoBattery", page.Find("#mode-state .mode").TextContent));

        _mode.Set(ChargeControlMode.Off, "the polling loop (charging finished)");

        page.WaitForAssertion(() => Assert.Equal("Off", page.Find("#mode-state .mode").TextContent));
    }

    [Fact]
    public void A_mode_that_ends_itself_takes_this_pages_note_with_it()
    {
        // "started from the Web UI at 12:00" is true of the action that set it, and of nothing else.
        var page = RenderTab("fast");
        page.Find("#start-fast-no-battery").Click();
        page.WaitForAssertion(() => Assert.Contains("started from the Web UI", page.Find("#mode-state").TextContent));

        _mode.Set(ChargeControlMode.Off, "the polling loop (charging finished)");

        page.WaitForAssertion(() =>
            Assert.DoesNotContain("started from the Web UI", page.Find("#mode-state").TextContent));
    }

    [Fact]
    public void Hides_the_battery_hold_switch_before_the_first_poll()
    {
        var page = RenderTab();

        Assert.Empty(page.FindAll("input[type=checkbox]"));
    }

    [Fact]
    public void Hides_the_battery_hold_switch_when_the_feature_is_disabled()
    {
        _holder.Set(Statuses.Sample(_time.Now) with { BatteryHoldEnabled = false });

        var page = RenderTab();

        Assert.Empty(page.FindAll("input[type=checkbox]"));
    }

    [Fact]
    public void Keeps_the_battery_hold_switch_above_the_tabs_because_it_belongs_to_all_of_them()
    {
        _holder.Set(Statuses.Sample(_time.Now) with { BatteryHoldEnabled = true });

        var page = RenderTab("targeted");

        // The switch is in the header section, which the tab strip follows -- not inside any one tab.
        Assert.NotEmpty(page.FindAll("section.controls input[type=checkbox]"));
        Assert.Empty(page.FindAll("section.tab-panel input[type=checkbox]"));
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

        var page = RenderTab();

        var checkbox = page.Find("input[type=checkbox]");
        Assert.Null(checkbox.GetAttribute("checked"));
    }

    [Fact]
    public void Toggling_the_battery_hold_switch_drives_the_selector()
    {
        _holder.Set(Statuses.Sample(_time.Now) with { BatteryHoldEnabled = true, BatteryHoldActive = false });
        var page = RenderTab();

        page.Find("input[type=checkbox]").Change(true);

        Assert.True(_batteryHold.Hold);
        Assert.Contains(_batteryHold.Sets, s => s.Hold && s.Source == "Web UI");
    }

    [Fact]
    public void A_failed_write_shows_the_battery_hold_switch_springing_back()
    {
        _holder.Set(Statuses.Sample(_time.Now) with { BatteryHoldEnabled = true, BatteryHoldActive = false });
        var page = RenderTab();
        page.Find("input[type=checkbox]").Change(true);

        // The next poll reports the write never actually landed on the inverter.
        _holder.Set(Statuses.Sample(_time.Now) with { BatteryHoldEnabled = true, BatteryHoldActive = false });

        page.WaitForAssertion(() => Assert.Null(page.Find("input[type=checkbox]").GetAttribute("checked")));
    }

    [Fact]
    public void The_solar_tab_reports_what_the_mode_decides_on()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with
        {
            SurplusWatts = 1800,
            BatterySocPercent = 100,
        });

        var page = RenderTab("solar");

        Assert.Contains("Solar surplus", page.Markup);
        Assert.Contains("1800 W", page.Markup);
        Assert.Contains("100%", page.Markup);
    }

    [Fact]
    public void The_fast_tab_says_what_the_speed_costs()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.FastNoBattery) with
        {
            EvChargerPowerWatts = 11_000,
            GridPowerWatts = 7_400,
        });

        var page = RenderTab("fast");

        Assert.Contains("Grid power", page.Markup);
        Assert.Contains("7400 W", page.Markup);
        Assert.Contains("11000 W", page.Markup);
    }

    [Fact]
    public void Stops_following_the_holder_once_the_circuit_is_gone()
    {
        var page = RenderTab();
        page.Dispose();

        var exception = Record.Exception(() => _holder.Set(Statuses.Sample(_time.Now)));

        Assert.Null(exception);
    }
}
