using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;
using Solax.Worker.Configuration;
using Solax.Worker.Forecasting;

namespace Solax.Worker.Tests;

/// <summary>
/// The fast-without-battery mode end to end through the poll loop, because its headline behaviour —
/// arm the hold, pin the current, then end itself — is spread across the controller, the coordinator
/// and the polling service, and only the assembly of the three is worth trusting.
/// </summary>
public class FastNoBatteryModeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 18, 0, 0, TimeSpan.Zero);

    private readonly FakeEvChargerControl _charger = new();
    private readonly FakeBatteryDischargeControl _inverter = new();
    private readonly ChargeControlModeSelector _mode =
        new(ChargeControlMode.FastNoBattery, NullLogger<ChargeControlModeSelector>.Instance);
    private readonly BatteryHoldSelector _manualHold =
        new(initialHold: false, NullLogger<BatteryHoldSelector>.Instance);
    private readonly ChargeControlStatusHolder _status = new();

    // The setpoint writes made while the loop was running. Snapshotted before the service is stopped,
    // because stopping legitimately writes a pause of its own -- which would otherwise mask what the
    // mode itself did. (The fake charger writes on every cycle: suppressing repeats is the real
    // EvChargerControl's job, tested there.)
    private List<(int Active, int Target, string Reason)> _writes = [];

    private int? LastTarget => _writes.Count == 0 ? null : _writes[^1].Target;

    [Fact]
    public async Task TheHoldIsArmedForAsLongAsTheModeIsSelected()
    {
        // No forecast, no plan, mid-evening: none of the forecast mode's conditions are met, and the
        // hold is armed anyway -- that is the whole point of this mode.
        await RunAsync(Charging(Now), Charging(Now.AddMinutes(1)));

        Assert.All(_inverter.Applied, hold => Assert.True(hold));
        Assert.Equal(ChargeControlMode.FastNoBattery, _mode.Mode);
    }

    [Fact]
    public async Task TheChargerIsPinnedAtTheConfiguredMaximum()
    {
        await RunAsync(Charging(Now), Charging(Now.AddMinutes(1)));

        Assert.NotEmpty(_writes);
        Assert.All(_writes, w => Assert.Equal(16, w.Target));
    }

    [Fact]
    public async Task AFinishedCarPausesTheCharger_ReturnsToOff_AndReleasesTheHold()
    {
        await RunAsync(
            Charging(Now),                      // the car draws -> we are committed
            Idle(Now.AddMinutes(1)),            // it stops; the idle clock starts
            Idle(Now.AddMinutes(4)));           // three minutes idle, past the two-minute dwell

        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.Equal(0, LastTarget);            // paused, not left armed at 16A
        Assert.Contains("returning to Off", _writes[^1].Reason);
        Assert.False(_inverter.Applied[^1]);    // released in the same cycle, not a poll later
    }

    [Fact]
    public async Task TheHoldTheOwnerAskedForSurvivesTheModeEnding()
    {
        _manualHold.Set(true, "test");

        await RunAsync(Charging(Now), Idle(Now.AddMinutes(1)), Idle(Now.AddMinutes(4)));

        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.True(_inverter.Applied[^1]); // a manually requested hold is never released by a mode
    }

    [Fact]
    public async Task AnIdleCarThatNeverChargedDoesNotEndTheMode()
    {
        // Plugged in and waiting (its own schedule, or still negotiating) for well past the dwell.
        await RunAsync(Idle(Now), Idle(Now.AddMinutes(5)), Idle(Now.AddMinutes(10)));

        Assert.Equal(ChargeControlMode.FastNoBattery, _mode.Mode);
        Assert.Equal(16, LastTarget);
    }

    [Fact]
    public async Task WithoutTheHoldFeatureItStillChargesAtTheMaximum()
    {
        // BatteryHold:Enabled false -- the mode can't keep its promise about the battery (it warns), but
        // a select option that silently did nothing would be worse.
        await RunAsync([Charging(Now), Charging(Now.AddMinutes(1))], batteryHoldEnabled: false);

        Assert.Equal(16, LastTarget);
        Assert.Empty(_inverter.Applied);
    }

    [Fact]
    public async Task InOffTheChargerAndTheInverterAreLeftAlone()
    {
        _mode.Set(ChargeControlMode.Off, "test");

        await RunAsync(Charging(Now), Charging(Now.AddMinutes(1)));

        Assert.Empty(_writes);
        Assert.All(_inverter.Applied, hold => Assert.False(hold));
    }

    private Task RunAsync(params EnergyState[] states) => RunAsync(states, batteryHoldEnabled: true);

    // Drives the real poll loop over a scripted telemetry sequence, then stops it. The service parks on
    // the reader once the script runs out, so exactly these polls happen -- no timing assumptions.
    private async Task RunAsync(EnergyState[] states, bool batteryHoldEnabled)
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

        var coordinator = new ChargingControlCoordinator(
            new Dictionary<ChargeControlMode, IChargingController>
            {
                [ChargeControlMode.FastNoBattery] = new FastChargingController(
                    chargeControl.MaxChargingCurrentAmps, chargeControl.CompletionDwell),
            },
            _charger,
            new SurplusMovingAverage(TimeSpan.FromMinutes(3)),
            pauseCurrentAmps: chargeControl.PauseCurrentAmps,
            idlePowerThresholdWatts: chargeControl.CompletionPowerThresholdWatts,
            NullLogger<ChargingControlCoordinator>.Instance);

        var forecastOptions = Options.Create(new ForecastChargeOptions());
        var dayPlan = new DayPlanProvider(
            forecast,
            forecastOptions,
            Options.Create(chargeControl),
            power,
            NullLogger<DayPlanProvider>.Instance);

        var reader = new ScriptedEnergyStateReader(states);
        var service = new SolaxPollingService(
            reader,
            forecast,
            coordinator,
            dayPlan,
            _mode,
            _manualHold,
            _inverter,
            _status,
            power,
            Options.Create(new SolaxOptions
            {
                Inverter = new DeviceConfig { Host = "127.0.0.1" },
                EvCharger = new DeviceConfig { Host = "127.0.0.1" },
                PollIntervalSeconds = 0,
            }),
            Options.Create(chargeControl),
            Options.Create(new BatteryHoldOptions { Enabled = batteryHoldEnabled, DryRun = true }),
            forecastOptions,
            NullLogger<SolaxPollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await reader.Exhausted.WaitAsync(TimeSpan.FromSeconds(10));
        _writes = [.. _charger.CurrentWrites];
        await service.StopAsync(CancellationToken.None);
    }

    private static EnergyState Charging(DateTimeOffset at) =>
        new(at, BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 11040,
            EvChargerStatus.Charging, EvChargerPowerWatts: 11040);

    // Plugged in, drawing nothing.
    private static EnergyState Idle(DateTimeOffset at) =>
        new(at, BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 300,
            EvChargerStatus.SuspendedEv, EvChargerPowerWatts: 0);
}

/// <summary>Replays a fixed telemetry sequence, then parks until the service is stopped.</summary>
internal sealed class ScriptedEnergyStateReader(params EnergyState[] states) : IEnergyStateReader
{
    private readonly Queue<EnergyState> _states = new(states);
    private readonly TaskCompletionSource _exhausted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once every scripted reading has been polled.</summary>
    public Task Exhausted => _exhausted.Task;

    public async Task<EnergyState> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_states.TryDequeue(out var state))
        {
            return state;
        }

        _exhausted.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>Records every hold reconciliation and reports back what it was asked for.</summary>
internal sealed class FakeBatteryDischargeControl : IBatteryDischargeControl
{
    /// <summary>The <c>hold</c> argument of every ApplyAsync call, in order.</summary>
    public List<bool> Applied { get; } = [];

    public Task<BatteryHoldState> ApplyAsync(
        bool hold, double activePowerTargetWatts, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Applied.Add(hold);
        return Task.FromResult(new BatteryHoldState(hold, hold ? activePowerTargetWatts : null, hold ? now : null, Wrote: true));
    }
}

/// <summary>No forecast at all: the day plan is unusable, as it is on a cold start or a Solcast outage.</summary>
internal sealed class NoForecastService : ISolarForecastService
{
    public SolarForecast? GetForecastForToday() => null;

    public SolarForecast? GetForecast(DateTimeOffset from, DateTimeOffset to) => null;
}
