using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Models;

/// <summary>
/// The owner's wall-clock table turned into instants (issue #265). What is worth asserting is where a
/// day's edges land: joined across midnight, including where the weekday and weekend lists meet, and
/// moved by the zone's rules on the two nights a year the clocks change.
/// </summary>
public class TariffExpansionTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly TariffPeriod[] Night = [new("00:00", "06:00"), new("22:00", "24:00")];

    private static readonly TariffPrices Prices = new(2.10m, 4.80m, "CZK");

    private static TariffTable Table(TariffPeriod[] weekday, TariffPeriod[]? weekend = null) =>
        new(weekday, weekend ?? weekday);

    private static TariffTimeline Expand(TariffTable table, DateOnly today, TariffPrices? prices = null) =>
        TariffExpansion.Expand(table, Prague, today, prices ?? Prices);

    // A wall-clock time in Prague on `date`, as an instant, for dates where it is unambiguous.
    private static DateTimeOffset At(DateOnly date, int hour, int minute = 0)
    {
        var local = date.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, Prague.GetUtcOffset(local));
    }

    private static readonly DateOnly Wednesday = new(2026, 10, 21);

    [Fact]
    public void TheTimelineTilesTodayAndTomorrowWithoutAGapAndAlternatesRates()
    {
        var timeline = Expand(Table([new("00:00", "06:00"), new("13:00", "15:00")]), Wednesday);

        Assert.Equal(At(Wednesday, 0), timeline.Windows[0].Window.Start);
        Assert.Equal(At(Wednesday.AddDays(2), 0), timeline.Windows[^1].Window.End);

        for (var i = 1; i < timeline.Windows.Count; i++)
        {
            Assert.Equal(timeline.Windows[i - 1].Window.End, timeline.Windows[i].Window.Start);
            Assert.NotEqual(timeline.Windows[i - 1].Rate, timeline.Windows[i].Rate);
        }

        Assert.Equal(TariffRate.Low, timeline.RateAt(At(Wednesday, 14)));
        Assert.Equal(TariffRate.High, timeline.RateAt(At(Wednesday, 15)));
        Assert.Equal(4.80m, timeline.PriceAt(At(Wednesday, 15)));
        Assert.Equal(At(Wednesday, 13), timeline.NextChangeAfter(At(Wednesday, 10)));
    }

    [Fact]
    public void ANightTariffIsJoinedAcrossMidnight()
    {
        var timeline = Expand(Table(Night), Wednesday);

        Assert.Equal(TariffRate.Low, timeline.RateAt(At(Wednesday, 23)));
        Assert.Equal(At(Wednesday.AddDays(1), 6), timeline.NextChangeAfter(At(Wednesday, 23)));
        Assert.DoesNotContain(timeline.Windows, window => window.Window.End == At(Wednesday.AddDays(1), 0));
    }

    [Fact]
    public void AnEndOf2400IsTheNextMidnight()
    {
        var timeline = Expand(Table([new("20:00", "24:00")]), Wednesday);

        var evening = timeline.WindowAt(At(Wednesday, 21))!;
        Assert.Equal(TariffRate.Low, evening.Rate);
        Assert.Equal(At(Wednesday.AddDays(1), 0), evening.Window.End);
        Assert.Equal(TariffRate.High, timeline.RateAt(At(Wednesday.AddDays(1), 0)));
    }

    [Fact]
    public void FridayNightJoinsSaturdayMorningFromTheWeekendList()
    {
        var friday = new DateOnly(2026, 10, 23);
        Assert.Equal(DayOfWeek.Friday, friday.DayOfWeek);

        var timeline = Expand(
            Table(weekday: [new("22:00", "24:00")], weekend: [new("00:00", "08:00")]),
            friday);

        Assert.Equal(At(friday.AddDays(1), 8), timeline.NextChangeAfter(At(friday, 23)));
    }

    [Fact]
    public void SundayNightJoinsMondayMorningFromTheWeekdayList()
    {
        var sunday = new DateOnly(2026, 11, 1);
        Assert.Equal(DayOfWeek.Sunday, sunday.DayOfWeek);

        var timeline = Expand(
            Table(weekday: [new("00:00", "06:00")], weekend: [new("20:00", "24:00")]),
            sunday);

        Assert.Equal(At(sunday.AddDays(1), 6), timeline.NextChangeAfter(At(sunday, 21)));
    }

    [Fact]
    public void TheWeekendListAppliesOnlyOnSaturdayAndSunday()
    {
        var friday = new DateOnly(2026, 10, 23);
        var timeline = Expand(Table(weekday: [new("10:00", "11:00")], weekend: [new("14:00", "15:00")]), friday);

        Assert.Equal(TariffRate.Low, timeline.RateAt(At(friday, 10, 30)));
        Assert.Equal(TariffRate.High, timeline.RateAt(At(friday, 14, 30)));
        Assert.Equal(TariffRate.High, timeline.RateAt(At(friday.AddDays(1), 10, 30)));
        Assert.Equal(TariffRate.Low, timeline.RateAt(At(friday.AddDays(1), 14, 30)));
    }

    [Fact]
    public void AnEdgeInTheSpringForwardGapMovesToThreeOClock()
    {
        // 02:00 CET jumps to 03:00 CEST: 02:30 never happens that night.
        var spring = new DateOnly(2026, 3, 29);
        Assert.True(Prague.IsInvalidTime(spring.ToDateTime(new TimeOnly(2, 30))));

        var timeline = Expand(Table([new("00:00", "02:30"), new("22:00", "24:00")]), spring);

        var night = timeline.WindowAt(At(spring, 1))!;
        Assert.Equal(At(spring, 0), night.Window.Start);
        Assert.Equal(At(spring, 3), night.Window.End);

        // The day is 23 hours long, and the timeline knows it.
        Assert.Equal(TimeSpan.FromHours(23), At(spring.AddDays(1), 0) - At(spring, 0));
        Assert.Equal(TimeSpan.FromHours(47), timeline.Windows[^1].Window.End - timeline.Windows[0].Window.Start);
    }

    [Fact]
    public void APeriodWhollyInsideTheSkippedHourIsDropped()
    {
        var spring = new DateOnly(2026, 3, 29);

        var timeline = Expand(Table([new("02:10", "02:50")]), spring);

        Assert.Equal(TariffRate.High, timeline.RateAt(At(spring, 3)));
        Assert.DoesNotContain(
            timeline.Windows,
            window => window.Rate == TariffRate.Low && window.Window.Start < At(spring, 12));
    }

    [Fact]
    public void AnEdgeInTheRepeatedAutumnHourIsTheFirstTimeRound()
    {
        // 03:00 CEST falls back to 02:00 CET: 02:30 happens twice that night.
        var autumn = new DateOnly(2026, 10, 25);
        var twice = autumn.ToDateTime(new TimeOnly(2, 30));
        Assert.True(Prague.IsAmbiguousTime(twice));

        var timeline = Expand(Table([new("00:00", "02:30")]), autumn);

        var night = timeline.WindowAt(At(autumn, 0))!;
        Assert.Equal(new DateTimeOffset(twice, TimeSpan.FromHours(2)), night.Window.End);

        // 00:00 to the first 02:30 is two and a half hours, and the day itself is 25.
        Assert.Equal(TimeSpan.FromHours(2.5), night.Window.Duration);
        Assert.Equal(TimeSpan.FromHours(25), At(autumn.AddDays(1), 0) - At(autumn, 0));
    }

    [Fact]
    public void AWindowRunningPastTheHorizonIsNotAChange()
    {
        // Tomorrow night's low tariff runs on into the day after, which the timeline stops short of.
        var timeline = Expand(Table(Night), Wednesday);

        Assert.Equal(TariffRate.Low, timeline.RateAt(At(Wednesday.AddDays(1), 23)));
        Assert.Null(timeline.NextChangeAfter(At(Wednesday.AddDays(1), 23)));
    }

    [Fact]
    public void AnEmptyTableIsHighTariffAllDay()
    {
        var timeline = Expand(Table([]), Wednesday);

        var only = Assert.Single(timeline.Windows);
        Assert.Equal(TariffRate.High, only.Rate);
        Assert.Null(timeline.NextChangeAfter(At(Wednesday, 12)));
    }

    [Fact]
    public void PricesNeedBothRates()
    {
        var settings = TariffSettings.None with { LowPricePerKwh = 2.10m };
        var timeline = TariffExpansion.Expand(Table(Night), Prague, Wednesday, settings.PricesIn("CZK"));

        Assert.Null(timeline.Prices);
        Assert.Null(timeline.PriceAt(At(Wednesday, 1)));
    }

    [Fact]
    public void AWindowsOwnPriceWinsOverItsRates()
    {
        var start = At(Wednesday, 0);
        var timeline = new TariffTimeline(
            [
                new TariffWindow(new TimeWindow(start, start.AddHours(1)), TariffRate.Low, PricePerKwh: 0.85m),
                new TariffWindow(new TimeWindow(start.AddHours(1), start.AddHours(2)), TariffRate.High),
            ],
            Prices);

        Assert.Equal(0.85m, timeline.PriceAt(start));
        Assert.Equal(4.80m, timeline.PriceAt(start.AddHours(1)));
        Assert.Null(timeline.PriceAt(start.AddHours(2)));
    }
}
