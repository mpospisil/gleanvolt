using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Monitoring;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// Exercises the store against a real SQLite file in a temporary directory — the schema, the merging
/// upsert and the round-trip of every column are exactly what a fake would fail to check.
/// </summary>
public sealed class SqliteEnergyIntervalStoreTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"gleanvolt-energy-{Guid.NewGuid():N}");

    // The store takes an explicit token on every call; nothing in these tests needs to cancel one.
    private static readonly CancellationToken Ct = CancellationToken.None;

    private SqliteEnergyIntervalStore NewStore() =>
        new(Path.Combine(_directory, "energy.db"), NullLogger<SqliteEnergyIntervalStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreatesItsDirectoryAndFileOnFirstUse()
    {
        using var store = NewStore();

        await store.InitializeAsync(Ct);

        Assert.True(File.Exists(store.DatabasePath));
    }

    [Fact]
    public async Task InitialisingTwiceOverTheSameFileKeepsWhatIsAlreadyThere()
    {
        using (var first = NewStore())
        {
            await first.InitializeAsync(Ct);
            await first.AppendAsync([Interval(Noon)], Ct);
        }

        using var second = NewStore();
        await second.InitializeAsync(Ct);

        var intervals = await second.GetIntervalsAsync(Noon.AddDays(-1), Noon.AddDays(1), Ct);
        Assert.Single(intervals);
    }

    [Fact]
    public async Task EveryColumnSurvivesTheRoundTrip()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        var written = Interval(Noon) with
        {
            SolarKwh = 1.42,
            ForecastSolarKwh = 1.31,
            GridImportKwh = 0.11,
            GridExportKwh = 0.35,
            EvKwh = 0.75,
            BatteryChargeKwh = 0.28,
            BatteryDischargeKwh = 0.04,
            SocStartPercent = 61,
            SocEndPercent = 64,
            SocMinPercent = 60,
            SocMaxPercent = 65,
            SocMeanPercent = 62.5,
            Covered = TimeSpan.FromMinutes(15),
            SampleCount = 180,
        };

        await store.AppendAsync([written], Ct);

        var read = Assert.Single(await store.GetIntervalsAsync(Noon, Noon.AddHours(1), Ct));
        Assert.Equal(written, read);
    }

    [Fact]
    public async Task IntervalsComeBackOldestFirstAndOnlyFromTheWindowAsked()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        await store.AppendAsync(
            [Interval(Noon.AddMinutes(30)), Interval(Noon), Interval(Noon.AddMinutes(15)), Interval(Noon.AddHours(2))],
            Ct);

        var window = await store.GetIntervalsAsync(Noon, Noon.AddHours(1), Ct);

        Assert.Equal(
            [Noon, Noon.AddMinutes(15), Noon.AddMinutes(30)],
            window.Select(i => i.PeriodStart));
    }

    [Fact]
    public async Task ASecondWriteForTheSameWindowAddsToTheFirstRatherThanReplacingIt()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        // The shape a restart produces: the stopping process flushes the five minutes it saw, the
        // starting one contributes the other ten. Neither may erase the other.
        await store.AppendAsync(
            [Interval(Noon) with
            {
                SolarKwh = 0.5,
                EvKwh = 0.2,
                GridImportKwh = 0.1,
                Covered = TimeSpan.FromMinutes(5),
                SampleCount = 60,
            }],
            Ct);

        await store.AppendAsync(
            [Interval(Noon) with
            {
                SolarKwh = 1.0,
                EvKwh = 0.4,
                GridImportKwh = 0.2,
                Covered = TimeSpan.FromMinutes(10),
                SampleCount = 120,
            }],
            Ct);

        var merged = Assert.Single(await store.GetIntervalsAsync(Noon, Noon.AddHours(1), Ct));

        Assert.Equal(1.5, merged.SolarKwh, 6);
        Assert.Equal(0.6, merged.EvKwh, 6);
        Assert.Equal(0.3, merged.GridImportKwh, 6);
        Assert.Equal(TimeSpan.FromMinutes(15), merged.Covered);
        Assert.Equal(180, merged.SampleCount);
    }

    [Fact]
    public async Task MergingKeepsTheEarlierStartAndTheLaterEndAndWidensTheSocRange()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        await store.AppendAsync(
            [Interval(Noon) with
            {
                SocStartPercent = 50, SocEndPercent = 55, SocMinPercent = 48, SocMaxPercent = 55,
                SocMeanPercent = 52, Covered = TimeSpan.FromMinutes(5),
            }],
            Ct);

        await store.AppendAsync(
            [Interval(Noon) with
            {
                SocStartPercent = 55, SocEndPercent = 70, SocMinPercent = 55, SocMaxPercent = 72,
                SocMeanPercent = 62, Covered = TimeSpan.FromMinutes(10),
            }],
            Ct);

        var merged = Assert.Single(await store.GetIntervalsAsync(Noon, Noon.AddHours(1), Ct));

        Assert.Equal(50, merged.SocStartPercent);
        Assert.Equal(70, merged.SocEndPercent);
        Assert.Equal(48, merged.SocMinPercent);
        Assert.Equal(72, merged.SocMaxPercent);

        // Time-weighted across the merge too: five minutes at 52 and ten at 62 is 58.67, not the 57
        // a plain average of the two halves would give.
        Assert.Equal((52 * 5 + 62 * 10) / 15.0, merged.SocMeanPercent, 6);
    }

    [Fact]
    public async Task MergingNeverTurnsAMissingForecastIntoAZero()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        // Half the window had a forecast and half had none. The row must report the half it had —
        // not zero, and not null.
        await store.AppendAsync([Interval(Noon) with { ForecastSolarKwh = null }], Ct);
        await store.AppendAsync([Interval(Noon) with { ForecastSolarKwh = 0.8 }], Ct);

        var merged = Assert.Single(await store.GetIntervalsAsync(Noon, Noon.AddHours(1), Ct));
        Assert.Equal(0.8, merged.ForecastSolarKwh);

        // And the other order behaves the same, rather than the null wiping what was there.
        await store.AppendAsync([Interval(Noon) with { ForecastSolarKwh = null }], Ct);

        merged = Assert.Single(await store.GetIntervalsAsync(Noon, Noon.AddHours(1), Ct));
        Assert.Equal(0.8, merged.ForecastSolarKwh);
    }

    [Fact]
    public async Task AWindowThatNeverSawAForecastStaysNull()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        await store.AppendAsync([Interval(Noon) with { ForecastSolarKwh = null }], Ct);

        Assert.Null(Assert.Single(await store.GetIntervalsAsync(Noon, Noon.AddHours(1), Ct)).ForecastSolarKwh);
    }

    [Fact]
    public async Task PruningRemovesOnlyWhatIsOlderThanTheRetention()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        var now = DateTimeOffset.UtcNow;
        await store.AppendAsync(
            [Interval(now.AddDays(-40)), Interval(now.AddDays(-20)), Interval(now.AddDays(-1))],
            Ct);

        var removed = await store.PruneAsync(TimeSpan.FromDays(30), Ct);

        Assert.Equal(1, removed);
        Assert.Equal(2, (await store.GetIntervalsAsync(now.AddYears(-1), now.AddDays(1), Ct)).Count);
    }

    [Fact]
    public async Task PruningIsOffWhenTheRetentionIsNotPositive()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);
        await store.AppendAsync([Interval(DateTimeOffset.UtcNow.AddYears(-5))], Ct);

        Assert.Equal(0, await store.PruneAsync(TimeSpan.Zero, Ct));
        Assert.Single(await store.GetIntervalsAsync(DateTimeOffset.UtcNow.AddYears(-10), DateTimeOffset.UtcNow, Ct));
    }

    [Fact]
    public async Task AppendingNothingIsHarmless()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        await store.AppendAsync([], Ct);

        Assert.Empty(await store.GetIntervalsAsync(Noon.AddDays(-1), Noon.AddDays(1), Ct));
    }

    private static EnergyInterval Interval(DateTimeOffset start) => new(
        PeriodStart: start,
        PeriodEnd: start.AddMinutes(15),
        TimeZoneId: "Europe/Prague",
        LocalDate: DateOnly.FromDateTime(start.UtcDateTime),
        SolarKwh: 1.0,
        ForecastSolarKwh: 0.9,
        GridImportKwh: 0.1,
        GridExportKwh: 0.2,
        EvKwh: 0.5,
        BatteryChargeKwh: 0.2,
        BatteryDischargeKwh: 0.0,
        SocStartPercent: 60,
        SocEndPercent: 62,
        SocMinPercent: 60,
        SocMaxPercent: 62,
        SocMeanPercent: 61,
        Covered: TimeSpan.FromMinutes(15),
        SampleCount: 180);
}
