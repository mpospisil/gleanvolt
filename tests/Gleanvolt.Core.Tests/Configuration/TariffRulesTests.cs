using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Configuration;

/// <summary>
/// Every rule tariff settings are held to before they are saved (issue #265), one test each, with the
/// message the form shows in place: the shared price rules, and the Table strategy's own.
/// </summary>
public class TariffRulesTests
{
    private static TariffTable Table(TariffPeriod[]? weekday = null, TariffPeriod[]? weekend = null) =>
        new(weekday ?? [new("00:00", "06:00")], weekend ?? []);

    [Fact]
    public void AGoodTableHasNoProblems()
    {
        Assert.Empty(TariffRules.CheckTable(Table([new("00:00", "06:00"), new("22:00", "24:00")])));
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
        var problems = TariffRules.CheckTable(Table([new(start, end)]));

        if (valid)
        {
            Assert.Empty(problems);
        }
        else
        {
            Assert.Equal(new TariffProblem(TariffFields.TableWeekday, 0, "Not a time."), Assert.Single(problems));
        }
    }

    [Theory]
    [InlineData("22:00", "06:00")]
    [InlineData("08:00", "08:00")]
    public void APeriodMustStartBeforeItEnds(string start, string end)
    {
        var problem = Assert.Single(TariffRules.CheckTable(Table(weekday: [], weekend: [new(start, end)])));

        Assert.Equal(TariffFields.TableWeekend, problem.Field);
        Assert.Equal(0, problem.Row);
        Assert.Equal("Ends before it starts. Split a period over midnight into two rows.", problem.Message);
        Assert.False(problem.IsWarning);
    }

    [Fact]
    public void AnOverlapIsReportedOnBothRows()
    {
        var problems = TariffRules.CheckTable(Table([new("05:00", "07:00"), new("13:00", "15:00"), new("06:00", "08:00")]));

        Assert.Equal(
            [
                new TariffProblem(TariffFields.TableWeekday, 0, "Overlaps 06:00–08:00."),
                new TariffProblem(TariffFields.TableWeekday, 2, "Overlaps 05:00–07:00."),
            ],
            problems);
    }

    [Fact]
    public void PeriodsThatOnlyMeetDoNotOverlap()
    {
        Assert.Empty(TariffRules.CheckTable(Table([new("06:00", "08:00"), new("08:00", "10:00")])));
    }

    [Fact]
    public void TheTwoListsAreCheckedApart()
    {
        Assert.Empty(TariffRules.CheckTable(Table(weekday: [new("00:00", "06:00")], weekend: [new("00:00", "08:00")])));
    }

    [Fact]
    public void NoPeriodsAtAllIsAWarning()
    {
        var problem = Assert.Single(TariffRules.CheckTable(TariffTable.Empty));

        Assert.Equal(TariffFields.Table, problem.Field);
        Assert.Equal("No low-tariff periods: everything would be high tariff.", problem.Message);
        Assert.True(problem.IsWarning);
    }

    [Fact]
    public void APriceCannotBeNegative()
    {
        Assert.Equal(
            [
                new TariffProblem(TariffFields.LowPrice, null, "A price can't be negative."),
                new TariffProblem(TariffFields.HighPrice, null, "A price can't be negative."),
            ],
            TariffRules.CheckPrices(-1m, -0.5m));
    }

    [Fact]
    public void ALowPriceAboveTheHighOneIsAWarning()
    {
        var problem = Assert.Single(TariffRules.CheckPrices(5m, 4m));

        Assert.Equal(TariffFields.LowPrice, problem.Field);
        Assert.Equal("Low tariff costs more than high.", problem.Message);
        Assert.True(problem.IsWarning);
    }

    [Fact]
    public void PricesAreOptional()
    {
        Assert.Empty(TariffRules.CheckPrices(null, null));
        Assert.Empty(TariffRules.CheckPrices(2.10m, null));
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
