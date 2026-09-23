using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The forecast viewer (#219): today and tomorrow drawn, then the same periods printed.
///
/// JSInterop is Loose because the chart itself is vendored JS this suite cannot see — what is checked
/// here is that the page prints the periods it holds and says the things it has to say about them.
/// The numbers that go into the picture are <see cref="ForecastChartSeriesTests"/>' business.
///
/// The fake throws for the live accessors, so every test here also holds the page to reading the
/// retained history: a provider answers only what is still to come, and the live cache at four in the
/// afternoon cannot say what this morning was expected to bring.
/// </summary>
public class ForecastPageTests : PageTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    // 2026-08-12 14:00 UTC is 16:00 in Prague: unambiguously the afternoon of the 12th locally, and
    // late enough that "the rest of today" is a real question.
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 14, 0, 0, TimeSpan.Zero), Prague);
    private readonly FakeSolarDayForecasts _forecasts = new();

    private static readonly DateOnly Today = new(2026, 8, 12);
    private static readonly DateOnly Tomorrow = new(2026, 8, 13);

    public ForecastPageTests()
    {
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<ISolarForecastService>(_forecasts);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>The instant a local Prague time falls at on a summer day (the offset is +2).</summary>
    private static DateTimeOffset Local(DateOnly day, int hour, int minute = 0) =>
        new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(2));

    private static readonly DateTimeOffset Retrieved = Local(Today, 13, 15);

    [Fact]
    public void Says_so_before_the_first_forecast_lands()
    {
        var page = Render<Forecast>();

        Assert.Contains("No forecast is held yet", page.Markup);

        // Not an empty chart and not a table of dashes: there is nothing to draw and nothing to print.
        Assert.DoesNotContain("forecast-chart", page.Markup);
    }

    [Fact]
    public void Prints_both_days_a_row_per_period()
    {
        _forecasts.Days[Today] = TestForecasts.Run(Retrieved, Local(Today, 14), 3200, 2900);
        _forecasts.Days[Tomorrow] = TestForecasts.Run(Retrieved, Local(Tomorrow, 10), 1500);

        var page = Render<Forecast>();

        Assert.Contains("Today", page.Markup);
        Assert.Contains("Tomorrow", page.Markup);

        // Each row is the half hour it names, in the site's own zone.
        Assert.Contains("13:30–14:00", page.Markup);
        Assert.Contains("14:00–14:30", page.Markup);
        Assert.Contains("09:30–10:00", page.Markup);

        Assert.Equal(3, page.FindAll("table.forecast-periods tbody tr:not(.day-heading):not(.empty-day)").Count);
    }

    [Fact]
    public void The_night_periods_are_kept()
    {
        // A 0 W row at 02:00 *is* the forecast. A table that drops rows is one a reader has to learn
        // the rules of before a gap in it can mean anything.
        _forecasts.Days[Tomorrow] = TestForecasts.Run(Retrieved, Local(Tomorrow, 2, 30), 0, 0);

        var page = Render<Forecast>();

        Assert.Contains("02:00–02:30", page.Markup);
        Assert.Equal(2, page.FindAll("table.forecast-periods tbody tr:not(.day-heading):not(.empty-day)").Count);
    }

    [Fact]
    public void Each_day_carries_the_figures_the_dashboard_shows()
    {
        _forecasts.Days[Today] = TestForecasts.Run(Retrieved, Local(Today, 14), 3200);

        // Deliberately not what summing the periods gives: the dashboard reads the summary the source
        // worked out when the forecast landed, and this page has to read the very same one.
        _forecasts.Summaries[Today] = new SolarDayForecastSummary(
            ExpectedWh: 24_800, LowWh: 18_100, HighWh: 31_400, PeakWatts: 6_200);

        var page = Render<Forecast>();

        var heading = page.Find("table.forecast-periods tr.day-heading").TextContent;
        Assert.Contains("24.80 kWh expected", heading);
        Assert.Contains("18.10", heading);
        Assert.Contains("31.40", heading);
        Assert.Contains("6.20 kW", heading);
    }

    [Fact]
    public void Says_so_when_today_is_only_held_from_mid_morning()
    {
        // The reference case: a controller started at 10:44 has nothing for today before then, and an
        // empty morning drawn without a word reads as darkness.
        _forecasts.Days[Today] = TestForecasts.Run(Retrieved, Local(Today, 11), 2600, 3200);

        var page = Render<Forecast>();

        Assert.Contains("Today starts at 10:30", page.Markup);
        Assert.Contains("gap in what is known", page.Markup);
    }

    [Fact]
    public void A_day_held_from_its_own_midnight_is_not_called_incomplete()
    {
        // The first period of a whole day ends at its midnight, so it starts on the day before -- which
        // must not be mistaken for a morning that is missing.
        _forecasts.Days[Today] = TestForecasts.Run(Retrieved, Local(Today, 0), 0, 0);

        var page = Render<Forecast>();

        Assert.DoesNotContain("Today starts at", page.Markup);
    }

    [Fact]
    public void A_period_with_no_band_prints_a_dash_rather_than_the_median()
    {
        _forecasts.Days[Today] = new SolarForecast(Retrieved, [TestForecasts.At(Local(Today, 14), 3200)]);

        var page = Render<Forecast>();

        var cells = page.FindAll("table.forecast-periods tbody tr:not(.day-heading):not(.empty-day) td");
        Assert.Equal("3,200", cells[1].TextContent.Trim());
        Assert.Equal("—", cells[2].TextContent.Trim());
        Assert.Equal("—", cells[3].TextContent.Trim());

        // ...and the chart's band collapses onto the line there, which the note has to say so that a
        // band no wider than the line cannot read as confidence.
        Assert.Contains("carry no range of their own", page.Markup);
    }

    [Fact]
    public void A_day_of_full_bands_makes_no_excuse_for_them()
    {
        _forecasts.Days[Today] = TestForecasts.Run(Retrieved, Local(Today, 14), 3200);

        var page = Render<Forecast>();

        Assert.DoesNotContain("carry no range of their own", page.Markup);
    }

    [Fact]
    public void Shows_when_the_forecast_was_fetched()
    {
        // Tomorrow's figures move until the day arrives, so when this one was taken is part of it.
        _forecasts.Days[Today] = TestForecasts.Run(Retrieved, Local(Today, 14), 3200);

        var page = Render<Forecast>();

        Assert.Contains("Last fetched <strong>13:15</strong>", page.Markup);
    }

    [Fact]
    public void A_day_with_nothing_held_says_so_rather_than_disappearing()
    {
        _forecasts.Days[Today] = TestForecasts.Run(Retrieved, Local(Today, 14), 3200);

        var page = Render<Forecast>();

        // Tomorrow past the provider's horizon is a day nothing is known about, not a day with no sun.
        Assert.Contains("Nothing is held for this day", page.Markup);
    }
}
