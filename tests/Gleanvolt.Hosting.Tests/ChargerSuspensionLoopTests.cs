using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Chargers;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Fast;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.Resume;
using Gleanvolt.Hosting.Targeting;
using Gleanvolt.Devices;
using Gleanvolt.Tests;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// A charger Gleanvolt can't control (#441), through the real poll loop: a short outage changes nothing, a long one
/// suspends the mode and releases the battery hold, and the charger coming back resumes the mode by itself.
/// </summary>
public sealed class ChargerSuspensionLoopTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"charger-suspension-{Guid.NewGuid():N}");

    private string ModePath => Path.Combine(_directory, "charge-mode.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnOutageShorterThanTheGracePeriod_ChangesNothing()
    {
        var run = new Run(ModePath);
        run.Mode.Set(ChargeControlMode.FastNoBattery, "test");

        // Ninety seconds without the charger, in the middle of a fast charge.
        await run.RunAsync(
            Charging(Now), Unreadable(Now.AddSeconds(30)), Unreadable(Now.AddSeconds(90)), Unreadable(Now.AddSeconds(120)),
            Charging(Now.AddMinutes(3)));

        Assert.Equal(ChargeControlMode.FastNoBattery, run.Mode.Mode);
        Assert.DoesNotContain(run.Statuses, status => status.State == ChargeControlState.Suspended || status.SuspendedSince is not null);
        Assert.All(run.Statuses, status => Assert.True(status.Charger!.Available));

        // The hold stayed armed throughout, and nothing was started again.
        Assert.All(run.Hold.Applied, Assert.True);
        Assert.Empty(run.Charger.ModeWrites);
    }

    [Fact]
    public async Task ALongOutage_SuspendsTheMode_ReleasesTheHold_AndTheChargerComingBackResumesIt()
    {
        var run = new Run(ModePath);
        run.Mode.Set(ChargeControlMode.FastNoBattery, "test");

        await run.RunAsync(
            Charging(Now),
            Unreadable(Now.AddMinutes(1)),
            Unreadable(Now.AddMinutes(2)),
            Unreadable(Now.AddMinutes(3)),
            Unreadable(Now.AddMinutes(4)),
            Charging(Now.AddMinutes(5)),
            Charging(Now.AddMinutes(6)));

        // Within the grace period: as before.
        Assert.Equal(ChargeControlState.Charging, run.Statuses[0].State);
        Assert.Null(run.Statuses[1].SuspendedSince);

        // Two minutes after the charger went: suspended from then, the mode kept, the hold released.
        var suspended = run.Statuses[3];
        Assert.Equal(ChargeControlState.Suspended, suspended.State);
        Assert.Equal(ChargeControlMode.FastNoBattery, suspended.Mode);
        Assert.Equal(Now.AddMinutes(3), suspended.SuspendedSince);
        Assert.Equal(ChargerAvailabilityState.Error, suspended.Charger!.State);
        Assert.Equal(Now.AddMinutes(1), suspended.Charger.Since);
        Assert.Contains("Modbus timeout", suspended.Charger.Reason);
        Assert.False(suspended.BatteryHoldActive);
        Assert.False(run.Statuses[4].BatteryHoldActive);

        // Back: started again as a press would, with no owner, and charging under the mode with the hold re-armed.
        var resumed = Assert.Single(run.Charger.ModeWrites);
        Assert.Equal(SolaxMode.Fast, resumed.Mode);
        Assert.Contains("available again", resumed.Reason);
        Assert.Equal(ChargeControlMode.FastNoBattery, run.Mode.Mode);
        Assert.Null(run.Statuses[^1].SuspendedSince);
        Assert.Equal(ChargeControlState.Charging, run.Statuses[^1].State);
        Assert.True(run.Statuses[^1].BatteryHoldActive);
    }

    [Fact]
    public async Task ASuspendedModeSwitchedOff_DoesNotResume()
    {
        var run = new Run(ModePath);
        run.Mode.Set(ChargeControlMode.FastNoBattery, "test");
        run.AfterPoll = status =>
        {
            if (status.SuspendedSince is not null)
            {
                run.Mode.Set(ChargeControlMode.Off, "test");
            }
        };

        await run.RunAsync(
            Charging(Now), Unreadable(Now.AddMinutes(1)), Unreadable(Now.AddMinutes(3)), Charging(Now.AddMinutes(4)));

        Assert.Equal(ChargeControlMode.Off, run.Mode.Mode);
        Assert.Empty(run.Charger.ModeWrites);
        Assert.Null(run.Statuses[^1].SuspendedSince);
    }

    [Fact]
    public async Task WhileTheChargerIsUnavailable_NoModeStarts()
    {
        var run = new Run(ModePath);
        await run.RunAsync(Unreadable(Now), Unreadable(Now.AddMinutes(3)));

        var result = await run.Actions!.StartAsync(ChargeControlMode.Solar, "test");

        Assert.False(result.Succeeded);
        Assert.True(result.ChargerUnavailable);
        Assert.Contains("Modbus timeout", result.Message);
        Assert.Equal("Action.ChargerUnavailable", result.MessagePhrase!.Key);
        Assert.Equal(ChargeControlMode.Off, run.Mode.Mode);
        Assert.Empty(run.Charger.ModeWrites);
    }

    private static EnergyState Charging(DateTimeOffset at) =>
        new(at, BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 11_040,
            EvChargerStatus.Charging, EvChargerPowerWatts: 11_040);

    // A poll whose charger read failed: Unknown, nothing drawn, and why.
    private static EnergyState Unreadable(DateTimeOffset at) =>
        new(at, BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 300,
            EvChargerStatus.Unknown, EvChargerPowerWatts: 0, EvChargerReadError: "Modbus timeout");

    /// <summary>One process lifetime, as in <see cref="ChargeModeResumeLoopTests"/>, with the charger's availability.</summary>
    private sealed class Run(string modePath)
    {
        public FakeEvChargerControl Charger { get; } = new();

        public ChargeControlModeSelector Mode { get; } = new(ChargeControlMode.Off, NullLogger<ChargeControlModeSelector>.Instance);

        public FastChargeSelector FastSelector { get; } = new(NullLogger<FastChargeSelector>.Instance);

        public TargetedChargeSelector TargetedSelector { get; } = new(NullLogger<TargetedChargeSelector>.Instance);

        public FakeBatteryDischargeControl Hold { get; } = new();

        public ChargerAvailabilityMonitor? Monitor { get; private set; }

        public ChargeActions? Actions { get; private set; }

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
            var monitor = new ChargerAvailabilityMonitor(
                clock, TimeSpan.FromMinutes(2), () => false, () => null, NullLogger<ChargerAvailabilityMonitor>.Instance);
            Monitor = monitor;
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
                Actions = new ChargeActions(Charger, Mode, fastController, NullLogger<ChargeActions>.Instance, monitor),
                new BatteryHoldSelector(initialHold: false, NullLogger<BatteryHoldSelector>.Instance),
                Hold,
                status,
                power,
                Options.Create(new ControllerOptions { PollIntervalSeconds = 0 }),
                Options.Create(chargeControl),
                Options.Create(new BatteryHoldOptions { Enabled = true, DryRun = true }),
                forecastOptions,
                NullLogger<PollingService>.Instance,
                clock,
                modeFile: new ChargeModeFile(modePath, NullLogger<ChargeModeFile>.Instance),
                charger: monitor);

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
