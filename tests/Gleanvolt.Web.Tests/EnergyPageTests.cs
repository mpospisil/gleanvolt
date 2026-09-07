using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Web.Components;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The energy viewer: one recorded day at a time, drawn and then printed — the chart above (#173) and
/// the table it is stored as below.
///
/// JSInterop is Loose because the chart itself is vendored JS (uPlot) this suite cannot see: what is
/// checked here is that the page hands it a day when it has one and keeps quiet when it doesn't. The
/// numbers that go into it are <see cref="EnergyChartSeriesTests"/>' business.
/// </summary>
public class EnergyPageTests : PageTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    // 2026-08-12 18:00 UTC is 20:00 in Prague, so "today" is unambiguously the 12th locally.
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero), Prague);
    private readonly FakeEnergyIntervalStore _store = new();

    public EnergyPageTests()
    {
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IEnergyIntervalStore>(_store);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // 10:00 Prague on the 12th, i.e. 08:00 UTC — the summer offset is +2.
    private static DateTimeOffset PragueMorning(int day = 12, int localHour = 10) =>
        new(2026, 8, day, localHour - 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Says_so_when_the_day_has_nothing_recorded()
    {
        var page = Render<Energy>();

        page.WaitForAssertion(() => Assert.Contains("Nothing was recorded on this day", page.Markup));
    }

    [Fact]
    public void Opens_on_today_and_lists_its_intervals_in_local_time()
    {
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(), solarKwh: 1.42, evKwh: 0.75));

        var page = Render<Energy>();

        page.WaitForAssertion(() =>
        {
            // Stored as 08:00 UTC, shown as the 10:00 it was in Prague.
            Assert.Contains("10:00", page.Markup);
            Assert.Contains("1.42", page.Markup);
            Assert.Contains("0.75", page.Markup);
        });
    }

    [Fact]
    public void Shows_the_day_a_row_belongs_to_and_not_the_neighbouring_days()
    {
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(day: 11), solarKwh: 9.11));
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(day: 12), solarKwh: 9.12));

        var page = Render<Energy>();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("9.12", page.Markup);
            Assert.DoesNotContain("9.11", page.Markup);
        });
    }

    [Fact]
    public void The_day_picker_moves_the_window()
    {
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(day: 9), solarKwh: 9.09));

        var page = Render<Energy>();
        page.WaitForAssertion(() => Assert.Contains("Nothing was recorded on this day", page.Markup));

        page.Find("#day").Change("2026-08-09");

        page.WaitForAssertion(() => Assert.Contains("9.09", page.Markup));
    }

    [Fact]
    public void Stepping_back_a_day_loads_the_previous_day()
    {
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(day: 11), solarKwh: 9.11));

        var page = Render<Energy>();
        page.Find("button[aria-label='Previous day']").Click();

        page.WaitForAssertion(() => Assert.Contains("9.11", page.Markup));
    }

    [Fact]
    public void Cannot_step_past_today()
    {
        var page = Render<Energy>();

        page.WaitForAssertion(() => Assert.True(page.Find("button[aria-label='Next day']").HasAttribute("disabled")));
    }

    [Fact]
    public void Totals_the_day_beneath_the_rows()
    {
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(localHour: 10), solarKwh: 1.25, socStartPercent: 40, socEndPercent: 50));
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(localHour: 11), solarKwh: 2.75, socStartPercent: 50, socEndPercent: 70));

        var page = Render<Energy>();

        page.WaitForAssertion(() =>
        {
            var footer = page.Find("tfoot").TextContent;
            Assert.Contains("4.00", footer);        // 1.25 + 2.75
            Assert.Contains("40 → 70%", footer);    // first row's start to the last row's end
            Assert.Contains("2 rows", footer);
        });
    }

    [Fact]
    public void A_missing_forecast_shows_as_a_dash_rather_than_zero()
    {
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(), forecastSolarKwh: null));

        var page = Render<Energy>();

        page.WaitForAssertion(() =>
        {
            // Third column: no forecast covered the window, which is not the same fact as 0.00 kWh.
            var forecastCell = page.FindAll("tbody td")[2];
            Assert.Equal("—", forecastCell.TextContent.Trim());
        });
    }

    [Fact]
    public void A_short_row_is_marked_and_explained_rather_than_passing_as_a_quiet_quarter_hour()
    {
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(), covered: TimeSpan.FromMinutes(5)));

        var page = Render<Energy>();

        page.WaitForAssertion(() =>
        {
            Assert.Single(page.FindAll("tr.partial"));
            Assert.Contains("33%", page.Markup);                  // 5 of 15 minutes
            Assert.Contains("1 row</strong> covers less", page.Markup);
        });
    }

    [Fact]
    public void A_full_row_is_neither_marked_nor_counted_as_partial()
    {
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning()));

        var page = Render<Energy>();

        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("tr.partial"));
            Assert.DoesNotContain("covers less", page.Markup);
        });
    }

    [Fact]
    public void House_load_is_shown_as_the_residual_of_the_other_columns()
    {
        // 6 kW for a quarter hour: 1.0 to the car, 0.25 into the battery, nothing across the meter.
        _store.Intervals.Add(TestIntervals.Sample(
            PragueMorning(),
            solarKwh: 1.5, evKwh: 1.0, batteryChargeKwh: 0.25, gridImportKwh: 0, gridExportKwh: 0));

        var page = Render<Energy>();

        page.WaitForAssertion(() =>
        {
            var house = page.FindAll("tbody td")[8];
            Assert.Equal("1.25", house.TextContent.Trim());
        });
    }

    [Fact]
    public void Reports_when_the_store_is_unavailable_rather_than_throwing()
    {
        _store.Unavailable = true;

        var page = Render<Energy>();

        page.WaitForAssertion(() => Assert.Contains("isn't available right now", page.Markup));
    }

    [Fact]
    public void Draws_the_day_above_the_rows_it_is_drawn_from()
    {
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(), solarKwh: 1.42));

        var page = Render<Energy>();

        page.WaitForAssertion(() =>
        {
            Assert.NotNull(page.Find("#energy-chart"));

            var invocation = Assert.Single(JSInterop.Invocations["solaxCharts.renderEnergyDayChart"]);
            var series = Assert.IsType<EnergyChartSeries>(invocation.Arguments[1]);

            // 1.42 kWh over a quarter hour, and the site's zone rather than the browser's -- the axis
            // has to agree with the table underneath it.
            Assert.Equal(5680, series.Solar[0]!.Value, 3);
            Assert.Equal("Europe/Prague", series.TimeZoneId);
        });
    }

    [Fact]
    public void Draws_nothing_when_the_day_recorded_nothing()
    {
        var page = Render<Energy>();

        page.WaitForAssertion(() => Assert.Contains("Nothing was recorded on this day", page.Markup));

        Assert.Empty(page.FindAll("#energy-chart"));
        Assert.Empty(JSInterop.Invocations["solaxCharts.renderEnergyDayChart"]);
    }

    [Fact]
    public void Draws_nothing_when_the_store_is_unavailable()
    {
        _store.Unavailable = true;

        var page = Render<Energy>();

        page.WaitForAssertion(() => Assert.Contains("isn't available right now", page.Markup));

        Assert.Empty(page.FindAll("#energy-chart"));
        Assert.Empty(JSInterop.Invocations["solaxCharts.renderEnergyDayChart"]);
    }

    [Fact]
    public void Redraws_when_the_day_moves()
    {
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(day: 11), solarKwh: 2.0));
        _store.Intervals.Add(TestIntervals.Sample(PragueMorning(day: 12), solarKwh: 1.0));

        var page = Render<Energy>();
        page.WaitForAssertion(() => Assert.Single(JSInterop.Invocations["solaxCharts.renderEnergyDayChart"]));

        page.Find("button[aria-label='Previous day']").Click();

        page.WaitForAssertion(() =>
        {
            // The 11th, not the 12th redrawn: stepping the day has to move the picture as well as the
            // rows, and both come from the one query.
            var series = Assert.IsType<EnergyChartSeries>(
                JSInterop.Invocations["solaxCharts.renderEnergyDayChart"][^1].Arguments[1]);

            Assert.Equal(8000, series.Solar[0]!.Value, 3);
        });
    }
}
