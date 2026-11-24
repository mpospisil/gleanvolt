using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Fast;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.Resume;
using Gleanvolt.Hosting.Targeting;
using Gleanvolt.Devices;
using Gleanvolt.Tests;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Resuming the mode after a restart (#413) through the real poll loop: one run of the service that is
/// stopped, and a second over the same data directory that starts Off, as every start does.
/// </summary>
public sealed class ChargeModeResumeLoopTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 21, 40, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"charge-mode-{Guid.NewGuid():N}");

    private string ModePath => Path.Combine(_directory, "charge-mode.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task AFastChargeStoppedByARestart_ResumesByItself_AndCarriesOnCounting()
    {
        var limit = new FastChargeLimit(RequiredEnergyWh: 20_000, ActivatedAt: Now);

        // 16 A on three phases for twenty minutes, then the restart.
        var before = new Run(ModePath);
        before.Mode.Set(ChargeControlMode.FastNoBattery, "test");
        before.FastSelector.Set(limit, "test");
        await before.RunAsync([.. Enumerable.Range(0, 21).Select(minute => Charging(Now.AddMinutes(minute)))]);

        Assert.True(File.Exists(ModePath));

        // Back two minutes later, Off, with the shutdown pause still on the car.
        var after = new Run(ModePath);
        await after.RunAsync(Paused(Now.AddMinutes(22)), Charging(Now.AddMinutes(23)));

        Assert.Equal(ChargeControlMode.FastNoBattery, after.Mode.Mode);
        Assert.Equal(limit, after.FastSelector.Limit);

        var start = after.Charger.ModeWrites[0];
        Assert.Equal(SolaxMode.Fast, start.Mode);
        Assert.Contains("restart", start.Reason);

        // 11.04kW for twenty minutes before, about a minute after.
        var progress = after.Statuses[^1].FastCharge!;
        Assert.InRange(progress.DeliveredWh, 3_680, 3_680 + 200);

        // Said on the Overview, from the poll that resumed it.
        Assert.Equal(Now.AddMinutes(22), after.Statuses[0].ResumedAt);
        Assert.Equal(Now.AddMinutes(22), after.Statuses[^1].ResumedAt);
    }

    [Fact]
    public async Task AChargerNotReadYetAfterTheRestart_IsNotSentTheStart_UntilAPollReadsIt()
    {
        // The Pi on 2026-11-24: the first poll ran before the charger plugin attached, the start sent then
        // failed with a warning and a stack trace, and the next poll's start went through.
        var before = new Run(ModePath);
        before.Mode.Set(ChargeControlMode.FastNoBattery, "test");
        await before.RunAsync(Charging(Now));

        var after = new Run(ModePath);
        var unread = new EnergyState(Now.AddMinutes(2), BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: 0,
            GridPowerWatts: 300, EvChargerStatus.Unknown, EvChargerPowerWatts: 0,
            EvChargerReadError: "No charger plugin is connected to Gleanvolt.");
        var modeAfterFirstPoll = (ChargeControlMode?)null;
        after.AfterPoll = _ => modeAfterFirstPoll ??= after.Mode.Mode;

        await after.RunAsync(unread, Paused(Now.AddMinutes(3)));

        Assert.Equal(ChargeControlMode.Off, modeAfterFirstPoll);
        var start = Assert.Single(after.Charger.ModeWrites, write => write.Mode == SolaxMode.Fast);
        Assert.Contains("restart", start.Reason);
        Assert.Equal(ChargeControlMode.FastNoBattery, after.Mode.Mode);
    }

    [Fact]
    public async Task ATargetSurvivesTheRestart()
    {
        var request = new TargetedChargeRequest(RequiredEnergyWh: 12_000, DepartBy: Now.AddHours(9), ActivatedAt: Now);

        var before = new Run(ModePath);
        before.TargetedSelector.Set(request, "test");
        before.Mode.Set(ChargeControlMode.Targeted, "test");
        await before.RunAsync(Paused(Now), Paused(Now.AddMinutes(1)));

        var after = new Run(ModePath);
        await after.RunAsync(Paused(Now.AddMinutes(5)), Paused(Now.AddMinutes(6)));

        Assert.Equal(ChargeControlMode.Targeted, after.Mode.Mode);
        Assert.Equal(request.RequiredEnergyWh, after.TargetedSelector.Request?.RequiredEnergyWh);
        Assert.Equal(request.DepartBy, after.TargetedSelector.Request?.DepartBy);
        Assert.Equal(request.ActivatedAt, after.TargetedSelector.Request?.ActivatedAt);
    }

    [Fact]
    public async Task ATargetWhoseDeparturePassedWhileDown_StaysOff_AndTheFileSaysOff()
    {
        var request = new TargetedChargeRequest(RequiredEnergyWh: 12_000, DepartBy: Now.AddHours(1), ActivatedAt: Now);

        var before = new Run(ModePath);
        before.TargetedSelector.Set(request, "test");
        before.Mode.Set(ChargeControlMode.Targeted, "test");
        await before.RunAsync(Paused(Now));

        var after = new Run(ModePath);
        await after.RunAsync(Paused(Now.AddHours(2)));

        Assert.Equal(ChargeControlMode.Off, after.Mode.Mode);
        Assert.Null(after.TargetedSelector.Request);
        Assert.Empty(after.Charger.ModeWrites);
        Assert.Null(after.Statuses[^1].ResumedAt);

        // Written Off, so a third start does not reconsider it.
        Assert.Equal(ChargeControlMode.Off, new ChargeModeFile(ModePath, NullLogger<ChargeModeFile>.Instance).Saved?.Mode);
    }

    [Fact]
    public async Task OffBeforeTheRestart_StaysOff()
    {
        var before = new Run(ModePath);
        before.Mode.Set(ChargeControlMode.FastNoBattery, "test");
        before.AfterPoll = _ => before.Mode.Set(ChargeControlMode.Off, "test");
        await before.RunAsync(Charging(Now), Charging(Now.AddMinutes(1)));

        var after = new Run(ModePath);
        await after.RunAsync(Charging(Now.AddMinutes(3)));

        Assert.Equal(ChargeControlMode.Off, after.Mode.Mode);
        Assert.Empty(after.Charger.ModeWrites);
    }

    [Fact]
    public async Task TheOwnerChoosingAModeBeforeTheResume_Wins_AndTheResumeIsDropped()
    {
        var before = new Run(ModePath);
        before.Mode.Set(ChargeControlMode.FastNoBattery, "test");
        await before.RunAsync(Charging(Now));

        // The charger refuses Fast on the first attempt, and the owner picks Solar before the next poll.
        var after = new Run(ModePath);
        after.Charger.ModeWriteFailure = "Modbus timeout";
        after.AfterPoll = _ =>
        {
            after.Charger.ModeWriteFailure = null;
            after.Mode.Set(ChargeControlMode.Solar, "test");
        };
        await after.RunAsync(Charging(Now.AddMinutes(2)), Charging(Now.AddMinutes(3)));

        Assert.Equal(ChargeControlMode.Solar, after.Mode.Mode);
        Assert.Null(after.Statuses[^1].ResumedAt);
    }

    private static EnergyState Charging(DateTimeOffset at) =>
        new(at, BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 11_040,
            EvChargerStatus.Charging, EvChargerPowerWatts: 11_040);

    // The car as the shutdown pause leaves it: plugged in, taking nothing.
    private static EnergyState Paused(DateTimeOffset at) =>
        new(at, BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 300,
            EvChargerStatus.ChargePaused, EvChargerPowerWatts: 0);

    /// <summary>One process lifetime: everything in memory is new, the data directory is shared.</summary>
    private sealed class Run(string modePath)
    {
        public FakeEvChargerControl Charger { get; } = new();

        public ChargeControlModeSelector Mode { get; } = new(ChargeControlMode.Off, NullLogger<ChargeControlModeSelector>.Instance);

        public FastChargeSelector FastSelector { get; } = new(NullLogger<FastChargeSelector>.Instance);

        public TargetedChargeSelector TargetedSelector { get; } = new(NullLogger<TargetedChargeSelector>.Instance);

        public List<ChargeControlStatus> Statuses { get; } = [];

        public Action<ChargeControlStatus>? AfterPoll { get; set; }

        public async Task RunAsync(params EnergyState[] states)
        {
            var chargeControl = new ChargeControlOptions
            {
                Phases = 3,
                MaxChargingCurrentAmps = 16,
                PauseCurrentAmps = 0,
                CompletionDwell = TimeSpan.FromMinutes(2),
                CompletionPowerThresholdWatts = 200,
            };

            var power = new ChargePowerConverter(chargeControl.NominalVoltage, chargeControl.Phases);
            var forecast = new NoForecastService();
            var clock = new Clock { Now = states[0].Timestamp };
            var reader = new ClockedReader(clock, states);
            var fastController = new FastChargingController(chargeControl.MaxChargingCurrentAmps, chargeControl.CompletionDwell);
            var forecastOptions = Options.Create(new ForecastChargeOptions { BatteryCapacityKWh = 9.0 });

            var coordinator = new ChargingControlCoordinator(
                new Dictionary<ChargeControlMode, IChargingController>
                {
                    [ChargeControlMode.FastNoBattery] = fastController,
                    [ChargeControlMode.Solar] = fastController,
                    [ChargeControlMode.Targeted] = new TargetedChargingController(
                        power,
                        new TargetedChargingOptions(
                            MinChargingCurrentAmps: chargeControl.MinChargingCurrentAmps,
                            MaxChargingCurrentAmps: chargeControl.MaxChargingCurrentAmps,
                            CurrentStepAmps: chargeControl.CurrentStepAmps,
                            ResumeHysteresisWatts: chargeControl.ResumeHysteresisWatts,
                            MinRunTime: TimeSpan.FromMinutes(10),
                            MinPauseTime: TimeSpan.FromMinutes(15),
                            CompletionDwell: chargeControl.CompletionDwell,
                            GridBridge: new TargetedChargeOptions().GridBridge,
                            MinBridgeSurplusWatts: 0)),
                },
                Charger,
                new SurplusMovingAverage(TimeSpan.FromMinutes(3)),
                pauseCurrentAmps: chargeControl.PauseCurrentAmps,
                idlePowerThresholdWatts: chargeControl.CompletionPowerThresholdWatts,
                NullLogger<ChargingControlCoordinator>.Instance,
                clock);

            var dayPlan = new DayPlanProvider(
                forecast, forecastOptions, Options.Create(chargeControl), power, NullLogger<DayPlanProvider>.Instance, timeProvider: clock);

            var status = new ChargeControlStatusHolder();
            status.Updated += snapshot =>
            {
                Statuses.Add(snapshot);
                AfterPoll?.Invoke(snapshot);
            };

            var service = new PollingService(
                reader,
                forecast,
                coordinator,
                dayPlan,
                TargetedCharge.Provider(forecast, dayPlan, power, chargeControl, forecastOptions, TargetedSelector),
                FastCharge.Provider(FastSelector, chargeControl, timeProvider: clock),
                Mode,
                new ChargeActions(Charger, Mode, fastController, NullLogger<ChargeActions>.Instance),
                new BatteryHoldSelector(initialHold: false, NullLogger<BatteryHoldSelector>.Instance),
                new FakeBatteryDischargeControl(),
                status,
                power,
                Options.Create(new ControllerOptions { PollIntervalSeconds = 0 }),
                Options.Create(chargeControl),
                Options.Create(new BatteryHoldOptions { Enabled = true, DryRun = true }),
                forecastOptions,
                NullLogger<PollingService>.Instance,
                clock,
                modeFile: new ChargeModeFile(modePath, NullLogger<ChargeModeFile>.Instance));

            await service.StartAsync(CancellationToken.None);
            await reader.Exhausted.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    // The scripted readings, moving the clock to each one as it is read.
    private sealed class ClockedReader(Clock clock, params EnergyState[] states) : IEnergyStateReader
    {
        private readonly ScriptedEnergyStateReader _inner = new(states);

        public Task Exhausted => _inner.Exhausted;

        public async Task<EnergyState> ReadAsync(CancellationToken cancellationToken = default)
        {
            var state = await _inner.ReadAsync(cancellationToken);
            clock.Now = state.Timestamp;
            return state;
        }
    }
}
