using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The overview's tariff line (issue #265): the rate, the price, and when it changes, said so that the
/// day is never in doubt; and nothing at all when there is no tariff.
/// </summary>
public class TariffNarrativeTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static DateTimeOffset At(int day, int hour) => new(2026, 10, day, hour, 0, 0, TimeSpan.FromHours(2));

    private static TariffTimeline Night(int day) => FakeTariffSchedule.Night(new DateOnly(2026, 10, day), Prague);

    private static string? Describe(TariffTimeline? timeline, DateTimeOffset now, string? unavailable = null) =>
        TariffNarrative.Describe(timeline, unavailable, "CZK", now, Prague);

    [Fact]
    public void LowTariffWithItsPriceAndTheChangeTomorrowMorning()
    {
        Assert.Equal("Low tariff, 2.10 CZK/kWh, until tomorrow 06:00", Describe(Night(21), At(21, 23)));
    }

    [Fact]
    public void HighTariffUntilTonight()
    {
        Assert.Equal("High tariff, 4.80 CZK/kWh, until 22:00", Describe(Night(21), At(21, 12)));
    }

    [Fact]
    public void AChangeLaterThanTomorrowNamesTheDay()
    {
        // Friday afternoon, with nothing low over the weekend: a strategy that knows as far as Monday.
        var timeline = new TariffTimeline(
            [
                new TariffWindow(new TimeWindow(At(23, 0), At(23, 6)), TariffRate.Low),
                new TariffWindow(new TimeWindow(At(23, 6), new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(1))), TariffRate.High),
                new TariffWindow(new TimeWindow(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(1)), new DateTimeOffset(2026, 10, 26, 6, 0, 0, TimeSpan.FromHours(1))), TariffRate.Low),
            ],
            Prices: null);

        Assert.Equal("High tariff, until Monday 00:00", Describe(timeline, At(23, 15)));
    }

    [Fact]
    public void WithoutPricesTheLineHasNone()
    {
        var timeline = Night(21) with { Prices = null };

        Assert.Equal("High tariff, until 22:00", Describe(timeline, At(21, 12)));
    }

    [Fact]
    public void AWindowsOwnPriceIsTheOneShown()
    {
        var start = At(21, 0);
        var timeline = new TariffTimeline(
            [
                new TariffWindow(new TimeWindow(start, start.AddHours(13)), TariffRate.Low, PricePerKwh: 0.85m),
                new TariffWindow(new TimeWindow(start.AddHours(13), start.AddHours(24)), TariffRate.High),
            ],
            Prices: null);

        Assert.Equal("Low tariff, 0.85 CZK/kWh, until 13:00", Describe(timeline, At(21, 12)));
    }

    [Fact]
    public void NoTariffIsNothingToSay()
    {
        Assert.Null(Describe(null, At(21, 12)));
    }

    [Fact]
    public void ASelectedTariffThatCannotAnswerSaysWhy()
    {
        Assert.Equal("broken, so there is no tariff restriction.", Describe(null, At(21, 12), "broken, so there is no tariff restriction."));
    }
}
