using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Configuration;

/// <summary>What an external tariff provider may publish (#401): each rule, and the joining.</summary>
public class ExternalTariffRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private static TariffWindow W(int fromHour, int toHour, TariffRate rate, decimal? price = null) =>
        new(new TimeWindow(Now.Date.AddHours(fromHour).ToUtc(), Now.Date.AddHours(toHour).ToUtc()), rate, price);

    private static IReadOnlyList<TariffProblem> Problems(IReadOnlyList<TariffWindow> windows, string currency = "CZK", DateTimeOffset? now = null) =>
        ExternalTariffRules.Check(windows, currency, "CZK", now ?? Now).Problems;

    [Fact]
    public void AUsableTariffIsAcceptedAsIs()
    {
        var windows = new[] { W(0, 6, TariffRate.Low), W(6, 22, TariffRate.High), W(22, 30, TariffRate.Low) };

        var (accepted, problems) = ExternalTariffRules.Check(windows, "czk", "CZK", Now);

        Assert.Empty(problems);
        Assert.Equal(windows, accepted);
    }

    [Fact]
    public void NeighboursOnOneRateAtOnePriceAreJoined()
    {
        var windows = new[] { W(0, 6, TariffRate.Low, 2m), W(6, 7, TariffRate.High, 5m), W(7, 8, TariffRate.High, 5m), W(8, 24, TariffRate.Low, 2m) };

        var (accepted, problems) = ExternalTariffRules.Check(windows, "CZK", "CZK", Now);

        Assert.Empty(problems);
        Assert.Equal(3, accepted.Count);
        Assert.Equal(W(6, 8, TariffRate.High, 5m), accepted[1]);
    }

    [Fact]
    public void NeighboursOnOneRateAtDifferentPricesAreRefused()
    {
        var problems = Problems([W(0, 12, TariffRate.High, 5m), W(12, 24, TariffRate.High, 6m)]);

        Assert.Equal("ExternalTariff.SameRateOtherPrice", Assert.Single(problems).Phrase!.Key);
        Assert.Equal(1, problems[0].Row);
    }

    [Fact]
    public void NoWindowsIsRefused() =>
        Assert.Equal("ExternalTariff.Empty", Assert.Single(Problems([])).Phrase!.Key);

    [Fact]
    public void AnotherCurrencyIsRefused() =>
        Assert.Equal("ExternalTariff.Currency", Assert.Single(Problems([W(0, 24, TariffRate.Low)], "EUR")).Phrase!.Key);

    [Fact]
    public void AWindowNotInUtcIsRefused()
    {
        var prague = TimeSpan.FromHours(1);
        var window = new TariffWindow(new TimeWindow(Now.Date.ToUtc().ToOffset(prague), Now.Date.ToUtc().AddHours(24).ToOffset(prague)), TariffRate.Low);

        Assert.Equal("ExternalTariff.NotUtc", Assert.Single(Problems([window])).Phrase!.Key);
    }

    [Fact]
    public void AWindowShorterThanFiveMinutesIsRefused()
    {
        var start = Now.AddMinutes(-1);
        var windows = new[]
        {
            new TariffWindow(new TimeWindow(start, start.AddMinutes(4)), TariffRate.Low),
            new TariffWindow(new TimeWindow(start.AddMinutes(4), start.AddHours(1)), TariffRate.High),
        };

        Assert.Equal("ExternalTariff.TooShort", Assert.Single(Problems(windows)).Phrase!.Key);
    }

    [Fact]
    public void AGapIsRefused() =>
        Assert.Equal("ExternalTariff.Gap", Assert.Single(Problems([W(0, 6, TariffRate.Low), W(7, 24, TariffRate.High)])).Phrase!.Key);

    [Fact]
    public void AnOverlapIsRefused() =>
        Assert.Equal("ExternalTariff.Overlap", Assert.Single(Problems([W(0, 8, TariffRate.Low), W(6, 24, TariffRate.High)])).Phrase!.Key);

    [Fact]
    public void WindowsThatDoNotCoverNowAreRefused() =>
        Assert.Equal("ExternalTariff.NotNow", Assert.Single(Problems([W(12, 24, TariffRate.Low)])).Phrase!.Key);

    [Fact]
    public void WindowsReachingMoreThanSevenDaysAheadAreRefused() =>
        Assert.Equal("ExternalTariff.TooFar", Assert.Single(Problems([W(0, 24 * 8, TariffRate.Low)])).Phrase!.Key);

    [Fact]
    public void NegativePricesAreAllowed() =>
        Assert.Empty(Problems([W(0, 12, TariffRate.Low, -0.5m), W(12, 24, TariffRate.High, 3m)]));

    [Fact]
    public void TooManyWindowsAreRefused()
    {
        var windows = Enumerable.Range(0, ExternalTariffRules.MaxWindows + 1)
            .Select(i => new TariffWindow(
                new TimeWindow(Now.Date.ToUtc().AddMinutes(10 * i), Now.Date.ToUtc().AddMinutes(10 * (i + 1))),
                i % 2 == 0 ? TariffRate.Low : TariffRate.High))
            .ToList();

        Assert.Equal("ExternalTariff.TooMany", Assert.Single(Problems(windows)).Phrase!.Key);
    }
}

internal static class UtcDate
{
    // A DateTime from DateTimeOffset.Date is unspecified; these are all meant as UTC.
    public static DateTimeOffset ToUtc(this DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
