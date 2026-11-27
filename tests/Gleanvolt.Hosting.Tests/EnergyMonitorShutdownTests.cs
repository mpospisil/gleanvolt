using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Monitoring;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// What the energy monitor does with the interval it was still filling when the service stops.
/// </summary>
public class EnergyMonitorShutdownTests
{
    /// <summary>
    /// The host hands every hosted service one shared shutdown token, and the web server stops first:
    /// a connection it waits out can spend the whole of that token before this worker is asked to
    /// stop. The part-finished interval must be written anyway, or every such restart loses the
    /// minutes before it.
    /// </summary>
    [Fact]
    public async Task ThePartFinishedIntervalIsWrittenEvenWhenTheHostsDeadlineHasAlreadyPassed()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore();
        var forecast = new SignallingForecast();
        var status = new ChargeControlStatusHolder();
        using var worker = new EnergyMonitorWorker(
            Options.Create(new EnergyMonitorOptions()),
            store,
            status,
            forecast,
            NullLogger<EnergyMonitorWorker>.Instance,
            timeProvider: time);

        await worker.StartAsync(CancellationToken.None);

        // The worker reads the forecast for every snapshot it takes. It subscribes only once its store
        // is open, so the snapshot is offered until one is taken; then the offers stop and the worker
        // is given until it has gone quiet, so nothing is still being observed when it is stopped.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (forecast.Asks == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The worker never observed a snapshot.");
            status.Set(Status(time.GetUtcNow()));
            await Task.Delay(20);
        }

        int seen;
        do
        {
            seen = forecast.Asks;
            await Task.Delay(100);
        }
        while (forecast.Asks != seen);

        // Five minutes into the quarter hour: an open interval with time on it, not yet complete.
        time.Advance(TimeSpan.FromMinutes(5));

        await worker.StopAsync(new CancellationToken(canceled: true)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(store.Appended);
    }

    private static ChargeControlStatus Status(DateTimeOffset at) =>
        new(ChargeControlMode.Off, DryRun: false, HoldingControl: false, ChargeControlState.Disabled, SurplusWatts: null,
            TargetCurrentAmps: null, ActiveCurrentAmps: null, BatterySocPercent: 50,
            ChargerStatus: EvChargerStatus.Available, CarConnected: false, SolarPowerWatts: 0,
            ForecastSolarPowerWatts: 0, EvChargerPowerWatts: 0, EvChargingCurrentAmps: 0, BatteryPowerWatts: 0,
            GridPowerWatts: 300, BatteryHoldEnabled: false, BatteryHoldRequested: false, BatteryHoldActive: false,
            BatteryHoldTargetWatts: null, Plan: null, LoanPowerWatts: 0, SessionEnergyWh: 0, LoanedTodayWh: 0,
            TomorrowForecastWh: null, Timestamp: at);

    /// <summary>
    /// Honours its token the way SQLite does — a cancelled one throws before anything is written — and
    /// keeps what it was given.
    /// </summary>
    private sealed class RecordingStore : IEnergyIntervalStore
    {
        public List<EnergyInterval> Appended { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AppendAsync(IReadOnlyList<EnergyInterval> intervals, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Appended.AddRange(intervals);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EnergyInterval>> GetIntervalsAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EnergyInterval>>([]);

        public Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class SignallingForecast : ISolarForecastService
    {
        private int _asks;

        public int Asks => Volatile.Read(ref _asks);

        public SolarForecast? GetForecastForToday()
        {
            Interlocked.Increment(ref _asks);
            return null;
        }

        public SolarForecast? GetForecast(DateTimeOffset from, DateTimeOffset to) => null;

        public SolarForecast? GetDayForecast(DateOnly localDate) => null;
    }
}
