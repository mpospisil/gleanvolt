using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Issue #229: the day plan's house-load profile is rebuilt from the stored energy history at startup,
/// rather than going back to <see cref="ForecastChargeOptions.BaselineHouseLoadWatts"/> on every deploy.
/// </summary>
public class HouseLoadHistoryTests
{
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Local));

    private static DayPlanProvider Provider(ForecastChargeOptions options) =>
        new(
            new NoForecastService(),
            Options.Create(options),
            Options.Create(new ChargeControlOptions()),
            new ChargePowerConverter(230, 3),
            NullLogger<DayPlanProvider>.Instance,
            timeProvider: new FixedTimeProvider(Now));

    private static EnergyState Reading(double houseWatts) =>
        new(Now, 50, 0, 0, houseWatts, EvChargerStatus.Available, 0);

    [Fact]
    public async Task TheStoredHistoryReachesThePlanOnTheNextPoll()
    {
        var provider = Provider(new ForecastChargeOptions { BaselineHouseLoadWatts = 350 });
        var store = new HistoryStore(Now, hour: 17, houseWatts: 900, days: 14);

        await provider.SeedHouseLoadAsync(store, CancellationToken.None);

        // Read on the monitor's thread, applied on the poll's: nothing changes until Update runs.
        var at17 = Now.Date.AddHours(17);
        Assert.Equal(350, provider.HouseLoad.ExpectedWattsAt(at17));

        provider.Update(Reading(houseWatts: 500), loanedTodayWh: 0);

        Assert.Equal(900, provider.HouseLoad.ExpectedWattsAt(at17), 3);
        Assert.Equal(Now.AddDays(-14), store.From);
    }

    [Fact]
    public async Task NoHistoryDaysMeansTheStoreIsNotRead()
    {
        var provider = Provider(new ForecastChargeOptions { HouseLoadHistoryDays = 0 });
        var store = new HistoryStore(Now, hour: 17, houseWatts: 900, days: 14);

        await provider.SeedHouseLoadAsync(store, CancellationToken.None);
        provider.Update(Reading(houseWatts: 500), loanedTodayWh: 0);

        Assert.Null(store.From);
    }

    [Fact]
    public async Task AStoreThatFailsLeavesTheSeed()
    {
        var provider = Provider(new ForecastChargeOptions { BaselineHouseLoadWatts = 350 });

        await provider.SeedHouseLoadAsync(new HistoryStore(Now, 17, 900, 14) { Fail = true }, CancellationToken.None);
        provider.Update(Reading(houseWatts: 500), loanedTodayWh: 0);

        Assert.Equal(350, provider.HouseLoad.ExpectedWattsAt(Now.Date.AddHours(17)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class HistoryStore(DateTimeOffset now, int hour, double houseWatts, int days) : IEnergyIntervalStore
    {
        public bool Fail { get; init; }

        public DateTimeOffset? From { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AppendAsync(IReadOnlyList<EnergyInterval> intervals, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<EnergyInterval>> GetIntervalsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            From = from;
            if (Fail)
            {
                throw new IOException("disk gone");
            }

            IReadOnlyList<EnergyInterval> rows =
            [
                .. from day in Enumerable.Range(1, days)
                   from quarter in Enumerable.Range(0, 4)
                   let start = new DateTimeOffset(now.Date.AddDays(-day).AddHours(hour).AddMinutes(15 * quarter), now.Offset)
                   select new EnergyInterval(
                       start, start.AddMinutes(15), "Europe/Prague", DateOnly.FromDateTime(start.LocalDateTime),
                       0, null, houseWatts / 4000, 0, 0, 0, 0, 50, 50, 50, 50, 50, TimeSpan.FromMinutes(15), 100),
            ];
            return Task.FromResult(rows);
        }
    }
}
