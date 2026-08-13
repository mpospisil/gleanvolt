using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Web.Components.Pages;

namespace Solax.Web.Tests;

/// <summary>
/// Phase 5 (#50): the forecast plan as one coherent view instead of a dozen loosely related
/// entities. JSInterop is Loose: the timeline chart is rendered by vendored JS (uPlot) this suite
/// cannot see, so these tests cover the data and markup around it, not the rendered pixels.
/// </summary>
public class ForecastPageTests : BunitContext
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly ChargeControlStatusHolder _holder = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero), Prague);

    public ForecastPageTests()
    {
        Services.AddSingleton(_holder);
        Services.AddSingleton<TimeProvider>(_time);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Says_so_before_the_first_poll_has_landed()
    {
        var page = Render<Forecast>();

        Assert.Contains("No poll has completed yet", page.Markup);
    }

    [Fact]
    public void Shows_an_explicit_empty_state_when_a_different_mode_is_driving()
    {
        // Plan is null whenever Mode isn't Forecasted (SolaxPollingService only publishes it for that
        // mode) -- the page must not show a stale plan from whatever ran earlier.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with { Plan = null });

        var page = Render<Forecast>();

        Assert.Contains("only shown while", page.Markup);
        Assert.Contains("Solar", page.Markup);
        Assert.DoesNotContain("Day outlook", page.Markup);
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

        var page = Render<Forecast>();

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

        var page = Render<Forecast>();

        // 10:00-12:00 UTC is 12:00-14:00 in Prague in August.
        Assert.Contains("12:00", page.Markup);
        Assert.Contains("14:00", page.Markup);
    }

    [Fact]
    public void Reports_no_window_as_none_rather_than_blank()
    {
        var plan = TestPlans.Usable(_time.Now) with { NextFeasibleWindow = null };
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = plan });

        var page = Render<Forecast>();

        Assert.Contains("none", page.Markup);
    }

    [Fact]
    public void Shows_the_timeline_chart_container_when_the_plan_has_one()
    {
        var plan = TestPlans.Usable(_time.Now);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = plan });

        var page = Render<Forecast>();

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

        var page = Render<Forecast>();

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

        var page = Render<Forecast>();

        Assert.Contains("—", page.Markup);
    }

    [Fact]
    public void Follows_the_holder_instead_of_sampling_it_once()
    {
        var page = Render<Forecast>();
        Assert.Contains("No poll has completed yet", page.Markup);

        var plan = TestPlans.Usable(_time.Now);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = plan });

        page.WaitForAssertion(() => Assert.Contains("Day outlook", page.Markup));
    }

    [Fact]
    public void Stops_following_the_holder_once_the_circuit_is_gone()
    {
        var page = Render<Forecast>();
        page.Dispose();

        var exception = Record.Exception(() => _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted)));

        Assert.Null(exception);
    }
}
