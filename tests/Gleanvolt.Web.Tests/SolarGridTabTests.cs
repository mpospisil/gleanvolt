using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The Solar + grid tab of the charging-plan page: its one number, written through the same seam the
/// Home Assistant entity writes, and its one verdict — when the forecast's useful sun ends.
/// </summary>
public class SolarGridTabTests : PageTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly ChargeControlStatusHolder _holder = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero), Prague);
    private readonly FakeChargeControlModeSelector _mode = new();
    private readonly FakeChargeActions _actions;
    private readonly FakeSolarGridSettings _settings = new();

    public SolarGridTabTests()
    {
        _actions = new FakeChargeActions(_mode);

        Services.AddSingleton(_holder);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IChargeControlModeSelector>(_mode);
        Services.AddSingleton<IChargeActions>(_actions);
        Services.AddSingleton<IBatteryHoldSelector>(new FakeBatteryHoldSelector());
        Services.AddSingleton<ISolarGridSettings>(_settings);
    }

    private IRenderedComponent<ChargingPlan> RenderTab() =>
        Render<ChargingPlan>(parameters => parameters.Add(p => p.Tab, "solar-grid"));

    [Fact]
    public void Shows_the_minimum_the_controller_is_using()
    {
        var page = RenderTab();

        Assert.Equal("2000", page.Find("#min-solar-surplus").GetAttribute("value"));
    }

    [Fact]
    public void Changing_the_minimum_writes_it_through_the_runtime_settings()
    {
        var page = RenderTab();

        page.Find("#min-solar-surplus").Change("3500");

        Assert.Equal((3500d, "Web UI"), Assert.Single(_settings.Sets));
    }

    [Fact]
    public void Says_when_the_useful_sun_ends_while_the_mode_is_driving()
    {
        var until = new DateTimeOffset(2026, 9, 12, 14, 30, 0, TimeSpan.Zero);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.SolarGrid) with
        {
            SolarGrid = new SolarGridOutlook(2000, _time.Now, ForecastUsable: true, _time.Now, until, "forecast surplus clears 2000W"),
        });

        var page = RenderTab();

        // 14:30 UTC is 16:30 in Prague in September.
        Assert.Contains("16:30", page.Markup);
        Assert.Contains("forecast surplus clears 2000W", page.Markup);
    }

    [Fact]
    public void Says_there_is_no_sun_left_once_the_forecast_has_none()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.SolarGrid) with
        {
            SolarGrid = new SolarGridOutlook(2000, _time.Now, ForecastUsable: true, null, null, "no forecast period left today"),
        });

        var page = RenderTab();

        Assert.Contains("none left today", page.Markup);
    }
}
