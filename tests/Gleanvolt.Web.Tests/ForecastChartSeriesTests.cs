using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The arithmetic behind the chart on /forecast (#219). Three things can quietly lie here and they are
/// what these tests pin down: a stretch nothing is held for drawn as a line rather than left as a hole,
/// a period the provider sent no band for drawn as a certain one, and a day the history only half
/// covers stretched to fill the axis.
/// </summary>
public class ForecastChartSeriesTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly DateTimeOffset WindowStart = new(2026, 8, 12, 0, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset Midnight = WindowStart.AddDays(1);
    private static readonly DateTimeOffset WindowEnd = WindowStart.AddDays(2);
    private static readonly DateTimeOffset Now = WindowStart.AddHours(16);

    private static DateTimeOffset At(int hour, int minute = 0) => WindowStart.AddHours(hour).AddMinutes(minute);

    private static ForecastChartSeries Build(params SolarForecastPeriod[] periods) =>
        ForecastChartSeries.From(periods, WindowStart, Midnight, WindowEnd, Now, Prague);

    [Fact]
    public void A_single_period_is_drawn_as_the_half_hour_it_covers()
    {
        // Its start, then its end carrying the same value: a step holds until the next point, so
        // without the closing point the period would be a line of no width at all.
        var series = Build(TestForecasts.At(At(10, 30), watts: 3200, p10: 2400, p90: 4100));

        Assert.Equal([At(10).ToUnixTimeSeconds(), At(10, 30).ToUnixTimeSeconds()], series.Timestamps);
        Assert.Equal([3200, 3200], series.Expected);
        Assert.Equal([2400, 2400], series.Low);
        Assert.Equal([4100, 4100], series.High);
    }

    [Fact]
    public void Consecutive_periods_are_one_unbroken_run()
    {
        var series = Build(
            TestForecasts.At(At(10, 30), 3200),
            TestForecasts.At(At(11), 3600));

        // Two starts and one closing point -- nothing to close between them, because the second
        // begins exactly where the first ends.
        Assert.Equal(
            [At(10).ToUnixTimeSeconds(), At(10, 30).ToUnixTimeSeconds(), At(11).ToUnixTimeSeconds()],
            series.Timestamps);
        Assert.DoesNotContain(null, series.Expected);
    }

    [Fact]
    public void A_stretch_nothing_is_held_for_stays_a_hole()
    {
        var series = Build(
            TestForecasts.At(At(10, 30), 3200),
            TestForecasts.At(At(13), 4100));

        // A missed refresh is not a forecast of darkness, and it must not be drawn as one: the run
        // closes, a null follows it, and the next run starts on its own.
        var gap = Array.IndexOf(series.Expected, null);
        Assert.True(gap > 0, "the run before the gap must be closed before the break");
        Assert.Equal(At(10, 30).ToUnixTimeSeconds() + 1, series.Timestamps[gap]);
        Assert.Equal(4100, series.Expected[gap + 1]);
    }

    [Fact]
    public void A_period_with_no_band_collapses_onto_the_median_and_says_so()
    {
        var series = Build(
            TestForecasts.At(At(10, 30), 3200, p10: 2400, p90: 4100),
            TestForecasts.At(At(11), 3600));

        // PowerWatts' own fallback, so the band closes onto the line rather than dropping to zero --
        // which would read as a half hour of darkness in the middle of the afternoon.
        Assert.Equal(3600, series.Low[^1]);
        Assert.Equal(3600, series.High[^1]);

        // ...and the page has to be able to say that the band is not a band there.
        Assert.False(series.HasBand);
    }

    [Fact]
    public void The_band_is_only_claimed_when_every_period_carries_one()
    {
        var series = Build(
            TestForecasts.At(At(10, 30), 3200, p10: 2400, p90: 4100),
            TestForecasts.At(At(11), 3600, p10: 2900, p90: 4300));

        Assert.True(series.HasBand);
    }

    [Fact]
    public void A_day_the_history_only_half_covers_keeps_the_whole_window()
    {
        // A controller started mid-morning has nothing before then. The chart's left edge is still
        // midnight -- a morning missing has to look like a morning missing.
        var series = Build(TestForecasts.At(At(11), 3600));

        Assert.Equal(WindowStart.ToUnixTimeSeconds(), series.WindowStart);
        Assert.Equal(WindowEnd.ToUnixTimeSeconds(), series.WindowEnd);
        Assert.Equal(At(10, 30).ToUnixTimeSeconds(), series.Timestamps[0]);
    }

    [Fact]
    public void An_empty_history_draws_nothing_and_claims_no_band()
    {
        var series = Build();

        Assert.Empty(series.Timestamps);
        Assert.Empty(series.Expected);
        Assert.False(series.HasBand);
    }

    [Fact]
    public void The_midnight_between_the_days_and_the_moment_now_are_carried()
    {
        var series = Build(TestForecasts.At(At(10, 30), 3200));

        Assert.Equal(Midnight.ToUnixTimeSeconds(), series.Midnight);
        Assert.Equal(Now.ToUnixTimeSeconds(), series.Now);
        Assert.Equal("Europe/Prague", series.TimeZoneId);
    }

    [Fact]
    public void Periods_arriving_out_of_order_are_still_one_run()
    {
        var series = Build(
            TestForecasts.At(At(11), 3600),
            TestForecasts.At(At(10, 30), 3200));

        Assert.Equal(3200, series.Expected[0]);
        Assert.DoesNotContain(null, series.Expected);
    }
}
