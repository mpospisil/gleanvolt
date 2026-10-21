using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Configuration;

/// <summary>
/// Every rule a tariff table is held to before it is saved (issue #265), one test each, with the
/// message the form shows on the row.
/// </summary>
public class TariffRulesTests
{
    private static TariffTable Table(
        TariffPeriod[]? weekday = null,
        TariffPeriod[]? weekend = null,
        decimal? low = 2.10m,
        decimal? high = 4.80m) =>
        new(TariffProvider.Table, weekday ?? [new("00:00", "06:00")], weekend ?? [], low, high);

    [Fact]
    public void AGoodTableHasNoProblems()
    {
        Assert.Empty(TariffRules.Check(Table([new("00:00", "06:00"), new("22:00", "24:00")])));
    }

    [Theory]
    [InlineData("6:00", "08:00", true)]
    [InlineData("06:00", "24:00", true)]
    [InlineData("24:00", "24:00", false)]
    [InlineData("06:60", "08:00", false)]
    [InlineData("0600", "08:00", false)]
    [InlineData("06:00", "", false)]
    [InlineData("25:00", "26:00", false)]
    [InlineData("-1:00", "08:00", false)]
    public void TimesAreHhMmWith2400AllowedOnlyAsAnEnd(string start, string end, bool valid)
    {
        var problems = TariffRules.Check(Table([new(start, end)]));

        if (valid)
        {
            Assert.Empty(problems);
        }
        else
        {
            var problem = Assert.Single(problems);
            Assert.Equal(new TariffProblem(TariffField.Weekday, 0, "Not a time."), problem);
        }
    }

    [Theory]
    [InlineData("22:00", "06:00")]
    [InlineData("08:00", "08:00")]
    public void APeriodMustStartBeforeItEnds(string start, string end)
    {
        var problem = Assert.Single(TariffRules.Check(Table(weekend: [new(start, end)], weekday: [])));

        Assert.Equal(TariffField.Weekend, problem.Field);
        Assert.Equal(0, problem.Row);
        Assert.Equal("Ends before it starts. Split a period over midnight into two rows.", problem.Message);
        Assert.False(problem.IsWarning);
    }

    [Fact]
    public void AnOverlapIsReportedOnBothRows()
    {
        var problems = TariffRules.Check(Table([new("05:00", "07:00"), new("13:00", "15:00"), new("06:00", "08:00")]));

        Assert.Equal(
            [
                new TariffProblem(TariffField.Weekday, 0, "Overlaps 06:00–08:00."),
                new TariffProblem(TariffField.Weekday, 2, "Overlaps 05:00–07:00."),
            ],
            problems);
    }

    [Fact]
    public void PeriodsThatOnlyMeetDoNotOverlap()
    {
        Assert.Empty(TariffRules.Check(Table([new("06:00", "08:00"), new("08:00", "10:00")])));
    }

    [Fact]
    public void TheTwoListsAreCheckedApart()
    {
        Assert.Empty(TariffRules.Check(Table(weekday: [new("00:00", "06:00")], weekend: [new("00:00", "08:00")])));
    }

    [Fact]
    public void APriceCannotBeNegative()
    {
        var problems = TariffRules.Check(Table(low: -1m, high: -0.5m));

        Assert.Equal(
            [
                new TariffProblem(TariffField.LowPrice, null, "A price can't be negative."),
                new TariffProblem(TariffField.HighPrice, null, "A price can't be negative."),
            ],
            problems);
    }

    [Fact]
    public void ALowPriceAboveTheHighOneIsAWarning()
    {
        var problem = Assert.Single(TariffRules.Check(Table(low: 5m, high: 4m)));

        Assert.Equal(TariffField.LowPrice, problem.Field);
        Assert.Equal("Low tariff costs more than high.", problem.Message);
        Assert.True(problem.IsWarning);
    }

    [Fact]
    public void PricesAreOptional()
    {
        Assert.Empty(TariffRules.Check(Table(low: null, high: null)));
    }

    [Fact]
    public void NoPeriodsAtAllIsAWarning()
    {
        var problem = Assert.Single(TariffRules.Check(Table(weekday: [], weekend: [])));

        Assert.Equal(TariffField.Table, problem.Field);
        Assert.Equal("No low-tariff periods: everything would be high tariff.", problem.Message);
        Assert.True(problem.IsWarning);
    }

    [Fact]
    public void ProviderNoneIsNeverRefused()
    {
        var table = Table([new("nonsense", "22:00")], low: -1m) with { Provider = TariffProvider.None };

        Assert.Empty(TariffRules.Check(table));
    }

    [Fact]
    public void NormalizingSortsByStartAndPadsTheHour()
    {
        var normalized = TariffRules.Normalize(Table([new("22:00", "24:00"), new("6:00", "8:00"), new(" 0:00", "1:30")]));

        Assert.Equal(
            [new TariffPeriod("00:00", "01:30"), new TariffPeriod("06:00", "08:00"), new TariffPeriod("22:00", "24:00")],
            normalized.Weekday);
    }
}
