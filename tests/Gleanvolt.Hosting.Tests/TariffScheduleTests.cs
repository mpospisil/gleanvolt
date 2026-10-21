using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Tariff;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The schedule as the controller and every surface read it (issue #265): whichever strategy is
/// selected, switched at runtime by a save, re-expanded at midnight, falling back to no restriction
/// with a reason when the selected strategy cannot answer — and a strategy registered only here drives
/// it exactly as the built-in ones do. Also the worker that feeds a strategy that fetches.
/// </summary>
public sealed class TariffScheduleTests : IDisposable
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-tariff-" + Guid.NewGuid().ToString("N"));

    // 23:00 on a Wednesday in Prague; FakeTimeProvider takes its start in UTC.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 21, 0, 0, TimeSpan.Zero));

    private readonly TestFeedProvider _feed = new();

    public TariffScheduleTests() => _time.SetLocalTimeZone(Prague);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TariffSettingsFile Open(params ITariffProvider[] extra) => new(
        Path.Combine(_root, "tariff.json"),
        "CZK",
        [.. TariffSettingsFileTests.Providers, .. extra],
        NullLogger<TariffSettingsFile>.Instance);

    private TariffSchedule Schedule(ITariffEditor settings) => new(settings, _time, NullLogger<TariffSchedule>.Instance);

    private static DateTimeOffset Prg(int day, int hour) => new(2026, 10, day, hour, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void ASaveSwitchesTheStrategyWithoutARestart()
    {
        var file = Open();
        var schedule = Schedule(file);

        Assert.Null(schedule.Timeline);
        Assert.Null(schedule.Unavailable);

        file.Save(TariffSettingsFileTests.Night());

        var timeline = schedule.Timeline!;
        Assert.Equal(TariffProviderIds.Table, timeline.ProviderId);
        Assert.Equal(TariffRate.Low, timeline.RateAt(_time.GetUtcNow()));
        Assert.Equal(Prg(22, 6), timeline.NextChangeAfter(_time.GetUtcNow()));
        Assert.Equal(new TariffPrices(2.10m, 4.80m, "CZK"), timeline.Prices);

        file.Save(file.Read() with { Provider = TariffProviderIds.None });

        Assert.Null(schedule.Timeline);
        Assert.Null(schedule.Unavailable);

        file.Save(file.Read() with { Provider = TariffProviderIds.Table });

        Assert.Equal(TariffRate.Low, schedule.Timeline!.RateAt(_time.GetUtcNow()));
    }

    [Fact]
    public void TheScheduleMovesOnAtMidnight()
    {
        var file = Open();
        file.Save(TariffSettingsFileTests.Night());
        var schedule = Schedule(file);

        var wednesday = schedule.Timeline!;
        _time.Advance(TimeSpan.FromDays(1));
        var thursday = schedule.Timeline!;

        Assert.Equal(wednesday.Windows[0].Window.Start.AddDays(1), thursday.Windows[0].Window.Start);
    }

    [Fact]
    public void AStrategyRegisteredOnlyHereDrivesTheSchedule()
    {
        var file = Open(_feed);
        var schedule = Schedule(file);

        file.Save(file.Read() with { Provider = _feed.Id });

        Assert.Equal(_feed.Id, schedule.Timeline!.ProviderId);
        Assert.Equal(TariffRate.High, schedule.Timeline!.RateAt(_time.GetUtcNow()));
    }

    [Fact]
    public void AStrategyThisBuildLacksIsNoRestrictionWithAReason()
    {
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(_root).FullName, "tariff.json"), """{ "provider": "Egd" }""");
        var schedule = Schedule(Open());

        Assert.Null(schedule.Timeline);
        Assert.Contains("Egd, is not in this build", schedule.Unavailable);
    }

    [Fact]
    public void AStrategyThatThrowsIsNoRestrictionWithAReason()
    {
        var file = Open(_feed);
        var schedule = Schedule(file);
        _feed.Throw = true;

        file.Save(file.Read() with { Provider = _feed.Id });

        Assert.Null(schedule.Timeline);
        Assert.Contains("failed (broken)", schedule.Unavailable);
    }

    [Fact]
    public void AStrategyWithNothingYetIsNoRestrictionWithAReason()
    {
        var file = Open(_feed);
        var schedule = Schedule(file);
        _feed.Answer = false;

        file.Save(file.Read() with { Provider = _feed.Id });

        Assert.Null(schedule.Timeline);
        Assert.Contains("nothing to answer from yet", schedule.Unavailable);
    }

    [Fact]
    public async Task TheWorkerFetchesOnlyForTheSelectedStrategyAndRefreshesTheSchedule()
    {
        var file = Open(_feed);
        var schedule = Schedule(file);
        using var worker = new TariffRefreshWorker(file, schedule, NullLogger<TariffRefreshWorker>.Instance);

        Assert.Equal(Timeout.InfiniteTimeSpan, await worker.RefreshOnceAsync(CancellationToken.None));
        Assert.Equal(0, _feed.Fetches);

        file.Save(file.Read() with { Provider = _feed.Id });
        _feed.Answer = false;
        Assert.Null(schedule.Timeline);

        _feed.Answer = true;
        Assert.Equal(_feed.RefreshInterval, await worker.RefreshOnceAsync(CancellationToken.None));

        Assert.Equal(1, _feed.Fetches);
        Assert.NotNull(schedule.Timeline);
    }

    [Fact]
    public async Task AFailedFetchIsLoggedNotThrown()
    {
        var file = Open(_feed);
        file.Save(file.Read() with { Provider = _feed.Id });
        using var worker = new TariffRefreshWorker(file, Schedule(file), NullLogger<TariffRefreshWorker>.Instance);
        _feed.FetchFails = true;

        Assert.Equal(_feed.RefreshInterval, await worker.RefreshOnceAsync(CancellationToken.None));
        Assert.Equal(1, _feed.Fetches);
    }

    // A strategy only these tests have: high tariff all of the next day, with switches for each way of
    // not answering.
    private sealed class TestFeedProvider : IRefreshingTariffProvider
    {
        public string Id => "TestFeed";

        public string Name => "Test feed";

        public TimeSpan RefreshInterval => TimeSpan.FromHours(1);

        public bool Answer { get; set; } = true;

        public bool Throw { get; set; }

        public bool FetchFails { get; set; }

        public int Fetches { get; private set; }

        public IReadOnlyList<TariffProblem> Check(JsonElement settings) => [];

        public Task RefreshAsync(JsonElement settings, CancellationToken cancellationToken)
        {
            Fetches++;
            return FetchFails ? throw new HttpRequestException("feed is down") : Task.CompletedTask;
        }

        public TariffTimeline? Expand(TariffContext context) =>
            Throw ? throw new InvalidOperationException("broken")
            : !Answer ? null
            : new TariffTimeline(
                [new TariffWindow(new TimeWindow(context.Now.AddHours(-1), context.Now.AddDays(1)), TariffRate.High)],
                context.Prices);
    }
}
