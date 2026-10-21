using System.Text.Json;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>The strategy that knows of no tariff: held to the contract, and never a timeline.</summary>
public class NoTariffProviderTests : TariffProviderContract
{
    protected override ITariffProvider Provider { get; } = new NoTariffProvider();

    protected override JsonElement UsableSettings => default;

    [Fact]
    public void ItIsNeverATimeline()
    {
        Assert.Null(Provider.Expand(Context(new DateOnly(2026, 10, 21), UsableSettings)));
    }
}

/// <summary>
/// The owner's table as a strategy (issue #265): its section of <c>tariff.json</c> read, checked,
/// normalized and expanded. The expansion's own cases are in <see cref="Models.TariffExpansionTests"/>.
/// </summary>
public class TableTariffProviderTests : TariffProviderContract
{
    protected override ITariffProvider Provider { get; } = new TableTariffProvider();

    protected override JsonElement UsableSettings => Section("""
        {
          "weekday": [ { "start": "00:00", "end": "06:00" }, { "start": "22:00", "end": "24:00" } ],
          "weekend": [ { "start": "00:00", "end": "08:00" } ]
        }
        """);

    private static JsonElement Section(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void NoSectionIsAnEmptyTable()
    {
        Assert.Equal(TariffTable.Empty, TableTariffProvider.Read(default));
    }

    [Fact]
    public void ASectionThatIsNotATableIsOneProblem()
    {
        var problem = Assert.Single(Provider.Check(Section("[]")));

        Assert.Equal(TariffFields.Table, problem.Field);
        Assert.False(problem.IsWarning);
    }

    [Fact]
    public void ARowWithAMissingTimeIsReportedOnTheRow()
    {
        var problem = Assert.Single(Provider.Check(Section("""{ "weekday": [ { "start": "06:00" } ] }""")));

        Assert.Equal(new TariffProblem(TariffFields.TableWeekday, 0, "Not a time."), problem);
    }

    [Fact]
    public void NormalizingSortsAndPadsTheSection()
    {
        var normalized = Provider.Normalize(Section("""{ "weekday": [ { "start": "22:00", "end": "24:00" }, { "start": "0:00", "end": "6:00" } ] }"""));

        Assert.Equal(
            [new TariffPeriod("00:00", "06:00"), new TariffPeriod("22:00", "24:00")],
            TableTariffProvider.Read(normalized)!.Weekday);
    }

    [Fact]
    public void WriteAndReadAreInverse()
    {
        var table = new TariffTable([new("00:00", "06:00")], [new("20:00", "24:00")]);

        var read = TableTariffProvider.Read(TableTariffProvider.Write(table))!;

        Assert.Equal(table.Weekday, read.Weekday);
        Assert.Equal(table.Weekend, read.Weekend);
    }

    [Fact]
    public void TheTimelineCarriesTheSharedPricesAndNamesItsStrategy()
    {
        var timeline = Provider.Expand(Context(new DateOnly(2026, 10, 21), UsableSettings))!;

        Assert.Equal(Prices, timeline.Prices);
        Assert.Equal(TariffProviderIds.Table, timeline.ProviderId);
    }
}

/// <summary>
/// A strategy that fetches, written only for the tests: the seam a distributor's feed or a spot price
/// will use, proved before either exists. Its "feed" is a night tariff with a price per window, and the
/// tests can make the next fetch fail.
/// </summary>
internal sealed class FakeFeedTariffProvider : IRefreshingTariffProvider
{
    private IReadOnlyList<TariffWindow>? _fetched;

    public string Id => "Feed";

    public string Name => "Test feed";

    public TimeSpan RefreshInterval => TimeSpan.FromHours(6);

    /// <summary>The local date the next fetch describes, from its midnight for two days.</summary>
    public DateOnly FeedDate { get; set; } = new(2026, 10, 21);

    /// <summary>When set, the next fetch throws it.</summary>
    public Exception? Failure { get; set; }

    public int Fetches { get; private set; }

    public IReadOnlyList<TariffProblem> Check(JsonElement settings) =>
        settings.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object
            ? []
            : [new TariffProblem("feed", null, "Not feed settings.")];

    public Task RefreshAsync(JsonElement settings, CancellationToken cancellationToken)
    {
        Fetches++;

        if (Failure is { } failure)
        {
            throw failure;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        var edges = new[] { (0, 0), (0, 6 * 60), (0, 22 * 60), (1, 6 * 60), (1, 22 * 60), (2, 0) }
            .Select(edge => TariffExpansion.ToInstant(FeedDate.AddDays(edge.Item1), edge.Item2, zone))
            .ToArray();

        _fetched =
        [
            .. edges.Zip(edges.Skip(1)).Select((pair, i) => new TariffWindow(
                new TimeWindow(pair.First, pair.Second),
                i % 2 == 0 ? TariffRate.Low : TariffRate.High,
                PricePerKwh: i % 2 == 0 ? 1.50m : 5.25m)),
        ];

        return Task.CompletedTask;
    }

    // Nothing fetched, or a fetch that no longer reaches now, is nothing to answer from.
    public TariffTimeline? Expand(TariffContext context) =>
        _fetched is { Count: > 0 } windows && windows[^1].Window.End > context.Now
            ? new TariffTimeline(windows, context.Prices, Id)
            : null;
}

/// <summary>The test-only fetching strategy, held to the contract and to what fetching adds: first fetch, failed fetch, stale fetch.</summary>
public class FeedTariffProviderTests : TariffProviderContract
{
    private readonly FakeFeedTariffProvider _feed = new();

    protected override ITariffProvider Provider => _feed;

    protected override JsonElement UsableSettings => JsonDocument.Parse("{}").RootElement.Clone();

    protected override Task PrepareAsync(DateOnly date)
    {
        _feed.FeedDate = date;
        return _feed.RefreshAsync(UsableSettings, CancellationToken.None);
    }

    private static readonly DateOnly Wednesday = new(2026, 10, 21);

    [Fact]
    public void BeforeTheFirstFetchThereIsNothingToAnswerFrom()
    {
        Assert.Null(_feed.Expand(Context(Wednesday, UsableSettings)));
    }

    [Fact]
    public async Task AfterTheFirstFetchItAnswersWithItsOwnPrices()
    {
        await PrepareAsync(Wednesday);

        var context = Context(Wednesday, UsableSettings);
        var timeline = _feed.Expand(context)!;

        Assert.Equal(TariffRate.High, timeline.RateAt(context.Now));
        Assert.Equal(5.25m, timeline.PriceAt(context.Now));
    }

    [Fact]
    public async Task AFailedFetchKeepsTheLastGoodOne()
    {
        await PrepareAsync(Wednesday);
        _feed.Failure = new HttpRequestException("feed is down");

        await Assert.ThrowsAsync<HttpRequestException>(() => _feed.RefreshAsync(UsableSettings, CancellationToken.None));

        Assert.NotNull(_feed.Expand(Context(Wednesday, UsableSettings)));
    }

    [Fact]
    public async Task AFetchThatNoLongerReachesNowIsNotAnswered()
    {
        await PrepareAsync(Wednesday);

        Assert.Null(_feed.Expand(Context(Wednesday.AddDays(3), UsableSettings)));
    }
}
