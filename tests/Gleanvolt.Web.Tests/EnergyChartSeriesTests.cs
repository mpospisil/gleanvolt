using System.Text.Json;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The arithmetic behind the day chart on /energy (#173). The store keeps kilowatt-hours per bucket
/// and the chart is read as power, and the two ways that conversion can quietly lie — dividing a
/// partly observed window by its nominal length, and drawing a line straight across a stretch nobody
/// watched — are exactly what these tests are here to pin down.
/// </summary>
public class EnergyChartSeriesTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly DateTimeOffset DayStart = new(2026, 8, 12, 0, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset DayEnd = DayStart.AddDays(1);

    private static DateTimeOffset At(int hour, int minute = 0) => DayStart.AddHours(hour).AddMinutes(minute);

    private static EnergyChartSeries Build(params EnergyInterval[] intervals) =>
        EnergyChartSeries.From(intervals, DayStart, DayEnd, Prague);

    [Fact]
    public void A_full_quarter_hour_becomes_its_average_power()
    {
        // 1 kWh delivered over a quarter hour is 4 kW while it was being delivered.
        var series = Build(TestIntervals.Sample(At(10), solarKwh: 1.0));

        Assert.Equal(4000, series.Solar[0]!.Value, 3);
    }

    [Fact]
    public void A_short_window_is_divided_by_the_part_that_was_observed()
    {
        // Half the window seen, half a window's energy in it: the roof was doing 2 kW throughout, and
        // dividing by the nominal quarter hour would draw a dip the sun never took.
        var series = Build(TestIntervals.Sample(
            At(10), solarKwh: 0.25, covered: TimeSpan.FromMinutes(7.5)));

        Assert.Equal(2000, series.Solar[0]!.Value, 3);
    }

    [Fact]
    public void A_window_barely_observed_is_not_drawn_at_all()
    {
        // Twenty seconds of a quarter hour: whatever energy landed in it, dividing by twenty seconds
        // turns measurement noise into a spike taller than the roof can produce.
        var series = Build(TestIntervals.Sample(
            At(10), solarKwh: 0.05, covered: TimeSpan.FromSeconds(20), socStartPercent: 41, socEndPercent: 41));

        Assert.Null(series.Solar[0]);
        Assert.Null(series.House[0]);

        // The SOC still stands, though: a reading that held for twenty seconds is still where the
        // pack was, and it is not an average of anything divided by time.
        Assert.Equal(41, series.Soc[0]!.Value, 3);
    }

    [Fact]
    public void Import_and_export_meet_on_one_line_with_export_below_it()
    {
        var importing = Build(TestIntervals.Sample(At(2), gridImportKwh: 0.5, gridExportKwh: 0));
        var exporting = Build(TestIntervals.Sample(At(12), gridImportKwh: 0, gridExportKwh: 0.5));

        Assert.Equal(2000, importing.Grid[0]!.Value, 3);
        Assert.Equal(-2000, exporting.Grid[0]!.Value, 3);
    }

    [Fact]
    public void Charging_is_above_the_line_and_discharging_below_it()
    {
        var charging = Build(TestIntervals.Sample(At(12), batteryChargeKwh: 0.75, batteryDischargeKwh: 0));
        var discharging = Build(TestIntervals.Sample(At(20), batteryChargeKwh: 0, batteryDischargeKwh: 0.75));

        Assert.Equal(3000, charging.Battery[0]!.Value, 3);
        Assert.Equal(-3000, discharging.Battery[0]!.Value, 3);
    }

    [Fact]
    public void Contiguous_windows_are_one_unbroken_line()
    {
        var series = Build(
            TestIntervals.Sample(At(10, 0)),
            TestIntervals.Sample(At(10, 15)),
            TestIntervals.Sample(At(10, 30)));

        // One point per window, plus one closing the last one -- a step holds its value until the next
        // point, so without that the final quarter hour would have no width.
        Assert.Equal(4, series.Timestamps.Length);
        Assert.Equal(At(10, 45).ToUnixTimeSeconds(), series.Timestamps[^1]);
        Assert.DoesNotContain(series.Solar, v => v is null);
    }

    [Fact]
    public void A_stretch_nobody_recorded_is_left_as_a_hole()
    {
        // The service was down between 10:15 and 12:00 -- no rows at all, not short ones.
        var series = Build(TestIntervals.Sample(At(10)), TestIntervals.Sample(At(12)));

        var hole = Array.FindIndex(series.Solar, v => v is null);
        Assert.True(hole > 0, "the gap between the two windows must break the line");

        // It opens the moment the recorded window ends, and everything it breaks breaks together.
        Assert.Equal(At(10, 15).ToUnixTimeSeconds() + 1, series.Timestamps[hole]);
        Assert.Null(series.Grid[hole]);
        Assert.Null(series.Battery[hole]);
        Assert.Null(series.Ev[hole]);
        Assert.Null(series.House[hole]);
        Assert.Null(series.Soc[hole]);

        // ...and the window before it keeps its full width rather than being closed by the hole.
        Assert.Equal(At(10, 15).ToUnixTimeSeconds(), series.Timestamps[hole - 1]);
        Assert.NotNull(series.Solar[hole - 1]);
    }

    [Fact]
    public void Partly_observed_windows_are_marked_as_one_band_each()
    {
        var partial = TimeSpan.FromMinutes(5);
        var series = Build(
            TestIntervals.Sample(At(6, 0), covered: partial),
            TestIntervals.Sample(At(6, 15), covered: partial),
            TestIntervals.Sample(At(6, 30)),
            TestIntervals.Sample(At(6, 45), covered: partial));

        // Two runs, not three marks: neighbouring short windows are one stretch of the day the
        // service was struggling through, and a chart says that better as a band than as a hatch.
        Assert.Equal(2, series.PartialStarts.Length);
        Assert.Equal(At(6, 0).ToUnixTimeSeconds(), series.PartialStarts[0]);
        Assert.Equal(At(6, 30).ToUnixTimeSeconds(), series.PartialEnds[0]);
        Assert.Equal(At(6, 45).ToUnixTimeSeconds(), series.PartialStarts[1]);
        Assert.Equal(At(7, 0).ToUnixTimeSeconds(), series.PartialEnds[1]);
    }

    [Fact]
    public void A_full_day_of_full_windows_is_marked_nowhere()
    {
        var series = Build(TestIntervals.Sample(At(10)), TestIntervals.Sample(At(10, 15)));

        Assert.Empty(series.PartialStarts);
        Assert.Empty(series.PartialEnds);
    }

    [Fact]
    public void The_chart_spans_the_whole_day_even_when_the_data_does_not()
    {
        var series = Build(TestIntervals.Sample(At(10)));

        // A morning's rows are a morning, not a full-width chart that happens to stop at noon.
        Assert.Equal(DayStart.ToUnixTimeSeconds(), series.DayStart);
        Assert.Equal(DayEnd.ToUnixTimeSeconds(), series.DayEnd);
        Assert.Equal("Europe/Prague", series.TimeZoneId);
    }

    [Fact]
    public void A_window_no_forecast_covered_is_a_hole_in_the_forecast_line_alone()
    {
        var series = Build(
            TestIntervals.Sample(At(10), forecastSolarKwh: null, solarKwh: 1.0),
            TestIntervals.Sample(At(10, 15), forecastSolarKwh: 0.5, solarKwh: 1.0));

        Assert.True(series.HasForecast);
        Assert.Null(series.Forecast[0]);
        Assert.Equal(2000, series.Forecast[1]!.Value, 3);
        Assert.NotNull(series.Solar[0]);
    }

    [Fact]
    public void A_day_no_forecast_covered_offers_no_forecast_line()
    {
        var series = Build(
            TestIntervals.Sample(At(10), forecastSolarKwh: null),
            TestIntervals.Sample(At(10, 15), forecastSolarKwh: null));

        // Not a flat zero: "nothing forecast this day" is a different claim from "darkness was
        // forecast", and the legend should not offer a line that is nothing but a gap.
        Assert.False(series.HasForecast);
    }

    [Fact]
    public void Rows_arriving_out_of_order_are_still_drawn_in_time_order()
    {
        var series = Build(TestIntervals.Sample(At(10, 15)), TestIntervals.Sample(At(10, 0)));

        Assert.Equal(At(10, 0).ToUnixTimeSeconds(), series.Timestamps[0]);
        Assert.Equal(series.Timestamps.OrderBy(t => t), series.Timestamps);
    }

    [Fact]
    public void Crosses_to_the_browser_under_the_names_the_chart_reads()
    {
        // The only contract between this record and charts.js, and nothing else checks it: the field
        // names uPlot is handed are whatever Blazor's interop serialiser makes of the properties, and
        // a rename here would leave the chart silently empty rather than failing a build.
        var series = Build(TestIntervals.Sample(At(10)));

        var json = JsonSerializer.Serialize(series, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);

        foreach (var field in new[]
                 {
                     "timestamps", "solar", "forecast", "grid", "battery", "ev", "house", "soc",
                     "partialStarts", "partialEnds", "dayStart", "dayEnd", "timeZoneId", "hasForecast",
                 })
        {
            Assert.True(document.RootElement.TryGetProperty(field, out _), $"the chart reads day.{field}");
        }

        // And a window that was not observed has to arrive as a null, not as a zero: that is what
        // uPlot draws as a hole.
        var withHole = Build(TestIntervals.Sample(At(10)), TestIntervals.Sample(At(12)));
        Assert.Contains("null", JsonSerializer.Serialize(withHole.Solar));
    }

    [Fact]
    public void A_day_with_nothing_recorded_draws_nothing_but_still_covers_the_day()
    {
        var series = EnergyChartSeries.From([], DayStart, DayEnd, Prague);

        Assert.Empty(series.Timestamps);
        Assert.Empty(series.PartialStarts);
        Assert.False(series.HasForecast);
        Assert.Equal(DayStart.ToUnixTimeSeconds(), series.DayStart);
    }
}
