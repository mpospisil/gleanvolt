using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Hosting.Tests;

public class ChargingControlCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 11, 0, 0, TimeSpan.Zero);

    private readonly FakeEvChargerControl _charger = new();
    private readonly StubChargingController _controller = new();
    private readonly ChargingControlCoordinator _coordinator;

    public ChargingControlCoordinatorTests()
    {
        _coordinator = new ChargingControlCoordinator(
            new Dictionary<ChargeControlMode, IChargingController> { [ChargeControlMode.Solar] = _controller },
            _charger,
            new SurplusMovingAverage(TimeSpan.FromMinutes(3)),
            pauseCurrentAmps: 0,
            idlePowerThresholdWatts: 200,
            NullLogger<ChargingControlCoordinator>.Instance);
    }

    private static EnergyState State() =>
        new(Now, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0, EvChargerStatus.Available, EvChargerPowerWatts: 0);

    private Task<ChargeControlCycleResult> Cycle() =>
        _coordinator.RunCycleAsync(State(), ChargeControlMode.Solar, plan: null, CancellationToken.None);

    [Fact]
    public async Task ChargeDecision_WritesTheTargetCurrent()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "charge");

        var result = await Cycle();

        var write = Assert.Single(_charger.CurrentWrites);
        Assert.Equal(6, write.Active);
        Assert.Equal(16, write.Target);
        Assert.Equal(ChargeControlState.Charging, result.State);
    }

    [Fact]
    public async Task PauseDecision_WritesThePauseCurrent()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 16);
        _controller.NextDecision = new(ChargingControlAction.Pause, null, "no surplus");

        var result = await Cycle();

        Assert.Equal(0, _charger.LastTarget); // pauseCurrentAmps
        Assert.Equal(ChargeControlState.Paused, result.State);
    }

    [Fact]
    public async Task NoneDecision_WritesNothing()
    {
        _controller.NextDecision = new(ChargingControlAction.None, null, "not Fast");

        await Cycle();

        Assert.Empty(_charger.CurrentWrites);
    }

    [Fact]
    public async Task ChargingState_IsFedBackToTheControllerNextCycle()
    {
        _controller.NextDecision = new(ChargingControlAction.Charge, 10, "charge");

        await Cycle();
        Assert.False(_controller.LastInput!.Charging); // wasn't charging going into the first cycle

        await Cycle();
        Assert.True(_controller.LastInput!.Charging); // charging now, from the previous cycle
    }

    [Fact]
    public async Task ReleaseControl_ResetsChargingState_WithoutWriting()
    {
        _controller.NextDecision = new(ChargingControlAction.Charge, 10, "charge");
        await Cycle(); // charging

        _coordinator.ReleaseControl();
        _charger.CurrentWrites.Clear();

        await Cycle();
        Assert.False(_controller.LastInput!.Charging); // reset; ReleaseControl itself wrote nothing
    }

    [Fact]
    public async Task PauseOnShutdown_PausesOnlyWhenCharging()
    {
        await _coordinator.PauseOnShutdownAsync(CancellationToken.None);
        Assert.Empty(_charger.CurrentWrites); // never charging -> nothing

        _controller.NextDecision = new(ChargingControlAction.Charge, 10, "charge");
        await Cycle();
        _charger.CurrentWrites.Clear();

        await _coordinator.PauseOnShutdownAsync(CancellationToken.None);
        Assert.Equal(0, _charger.LastTarget); // dropped to the pause current
    }

    /// <summary>
    /// A charger that has stopped answering must not be able to hold the shutdown open. Unbounded,
    /// this waits on Modbus timeouts until the container's stop grace period runs out and Docker
    /// SIGKILLs the process — part-way through this very write, with a car still drawing. Giving up on
    /// a deadline is worse for the car and better for everything else, so it is what we do.
    /// </summary>
    [Fact]
    public async Task PauseOnShutdown_GivesUpOnAChargerThatStopsAnswering()
    {
        var charger = new FlakyEvChargerControl();
        var coordinator = new ChargingControlCoordinator(
            new Dictionary<ChargeControlMode, IChargingController> { [ChargeControlMode.Solar] = _controller },
            charger,
            new SurplusMovingAverage(TimeSpan.FromMinutes(3)),
            pauseCurrentAmps: 0,
            idlePowerThresholdWatts: 200,
            NullLogger<ChargingControlCoordinator>.Instance,
            shutdownPauseTimeout: TimeSpan.FromMilliseconds(50));

        // Take control while the charger is healthy, so the shutdown has something to release.
        _controller.NextDecision = new(ChargingControlAction.Charge, 10, "charge");
        await coordinator.RunCycleAsync(State(), ChargeControlMode.Solar, plan: null, CancellationToken.None);

        charger.Answering = false;

        // The assertion is that this returns at all. WaitAsync rather than a bare await: a regression
        // here hangs the test run instead of failing it, which is the least useful way to find out.
        await coordinator.PauseOnShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(charger.PauseWasAttempted); // gave up, rather than never having tried
    }

    /// <summary>A charger that answers until it doesn't — then hangs, the way a silent Modbus device does.</summary>
    private sealed class FlakyEvChargerControl : IEvChargerControl
    {
        private EvChargerSettings _settings = new(EvChargerMode.Fast, 16);

        public bool Answering { get; set; } = true;

        public bool PauseWasAttempted { get; private set; }

        public async Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
        {
            if (Answering)
            {
                return _settings;
            }

            // What a read against a silent device does, minus the five-second wait.
            PauseWasAttempted = true;
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return _settings;
        }

        public async Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default)
        {
            if (!Answering)
            {
                PauseWasAttempted = true;
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            _settings = _settings with { ChargeCurrentAmps = targetAmps };
        }

        // The coordinator never calls this -- the use-mode belongs to the actions, not the poll loop --
        // so a charger double built for the coordinator's tests has nothing to say about it.
        public Task SetModeAsync(EvChargerMode mode, string reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The control loop must not write the charger's use-mode.");
    }

    [Fact]
    public async Task TheModeSelectsWhichControllerDecides()
    {
        var forecasted = new StubChargingController { NextDecision = new(ChargingControlAction.Charge, 10, "forecast") };
        var coordinator = new ChargingControlCoordinator(
            new Dictionary<ChargeControlMode, IChargingController>
            {
                [ChargeControlMode.Solar] = _controller,
                [ChargeControlMode.Forecasted] = forecasted,
            },
            _charger,
            new SurplusMovingAverage(TimeSpan.FromMinutes(3)),
            pauseCurrentAmps: 0,
            idlePowerThresholdWatts: 200,
            NullLogger<ChargingControlCoordinator>.Instance);

        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "solar");

        await coordinator.RunCycleAsync(State(), ChargeControlMode.Forecasted, plan: null, CancellationToken.None);

        var write = Assert.Single(_charger.CurrentWrites);
        Assert.Equal(10, write.Target);
    }

    [Fact]
    public async Task AnUnregisteredModeLeavesTheChargerAlone()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "solar");

        var result = await _coordinator.RunCycleAsync(State(), ChargeControlMode.Forecasted, plan: null, CancellationToken.None);

        Assert.Empty(_charger.CurrentWrites);
        Assert.False(result.HoldingControl);
    }

    [Fact]
    public async Task ALoanIsMeteredIntoTheDailyTotal()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 6, "bridged", LoanPowerWatts: 1200);

        // Two samples three minutes apart: the first sample's loan power counts for the gap that follows.
        await _coordinator.RunCycleAsync(State(), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(State(Now.AddMinutes(3)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.Equal(60, _coordinator.LoanedTodayWh, 1);
    }

    [Fact]
    public async Task SessionEnergyAccumulatesWhileTheCarStaysConnected()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 6, "charging");

        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(3)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.Equal(207, _coordinator.SessionEnergyWh, 0);
    }

    [Fact]
    public async Task UnpluggingTheCarStartsTheSessionEnergyAfresh()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 6, "charging");

        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(3)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(State(Now.AddMinutes(4)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(5)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.Equal(0, _coordinator.SessionEnergyWh, 1);
    }

    [Fact]
    public async Task TheCarsOwnDrawIsTrackedForTheFastMode()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "fast");

        // Plugged in but not drawing yet: idle time accumulates, but "has drawn" stays false, which is
        // what stops the fast mode calling a session finished before it has started.
        await _coordinator.RunCycleAsync(Connected(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Connected(Now.AddMinutes(1)), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.False(_controller.LastInput!.EvDrewPower);
        Assert.Equal(TimeSpan.FromMinutes(1), _controller.LastInput.EvIdleFor);

        // Now it charges: the idle clock resets.
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(2)), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.True(_controller.LastInput!.EvDrewPower);
        Assert.Equal(TimeSpan.Zero, _controller.LastInput.EvIdleFor);

        // And stops again: idle from the first sample that showed no draw, with the draw remembered.
        await _coordinator.RunCycleAsync(Connected(Now.AddMinutes(3)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Connected(Now.AddMinutes(5)), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.True(_controller.LastInput!.EvDrewPower);
        Assert.Equal(TimeSpan.FromMinutes(2), _controller.LastInput.EvIdleFor);
    }

    [Fact]
    public async Task ResumingAfterACommandedPauseDoesNotInheritTheWaitsIdleTime()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);

        // The car draws once, so "has drawn" is armed -- as it is a few seconds into any fast charge
        // that is then deferred to a scheduled start.
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "fast");
        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);

        // Then we hold it at the pause current for 40 minutes, waiting for the appointment. The car
        // draws nothing because we are the ones stopping it.
        _controller.NextDecision = new(ChargingControlAction.Pause, null, "waiting until 07:47");
        await _coordinator.RunCycleAsync(Connected(Now.AddMinutes(1)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Connected(Now.AddMinutes(40)), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(39), _controller.LastInput!.EvIdleFor);

        // The appointment arrives and we resume. The car takes a moment to wake up, and that moment
        // must be judged on its own: inheriting the 40-minute wait would end the charge immediately.
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "starting");
        await _coordinator.RunCycleAsync(Connected(Now.AddMinutes(41)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Connected(Now.AddMinutes(42)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.True(_controller.LastInput!.EvDrewPower);
        Assert.Equal(TimeSpan.FromMinutes(1), _controller.LastInput.EvIdleFor);
    }

    [Fact]
    public async Task TheTimeSpentOutOfFastIsMeasuredAndResetOnReturn()
    {
        // What lets a two-minute-old Stop be believed and an eight-second one ignored. Timed rather than
        // counted, because polls are not evenly spaced.
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Stop, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "charge");

        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, _controller.LastInput!.ChargerNotFastFor);

        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(3)), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(3), _controller.LastInput!.ChargerNotFastFor);

        // Back in Fast: the clock is not merely paused, it is forgotten, so a later blip starts afresh
        // rather than inheriting a stretch that has already been recovered from.
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(4)), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, _controller.LastInput!.ChargerNotFastFor);

        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Stop, 6);
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(5)), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, _controller.LastInput!.ChargerNotFastFor);
    }

    [Fact]
    public async Task ACarAnnouncingItIsDoneCountsAsIdleEvenWhileDrawing()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "fast");

        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);

        // SuspendedEv while still pulling 1kW (conditioning, balancing): waiting for the power alone
        // would never call this finished. The idle clock runs from the first such sample.
        await _coordinator.RunCycleAsync(WindingDown(Now.AddMinutes(2)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(WindingDown(Now.AddMinutes(4)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.True(_controller.LastInput!.EvDrewPower);
        Assert.Equal(TimeSpan.FromMinutes(2), _controller.LastInput.EvIdleFor);
    }

    [Fact]
    public async Task PluggingInAFreshCarForgetsTheLastOnesDraw()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "fast");

        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(State(Now.AddMinutes(1)), ChargeControlMode.Solar, null, CancellationToken.None); // unplugged
        await _coordinator.RunCycleAsync(Connected(Now.AddMinutes(2)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.False(_controller.LastInput!.EvDrewPower);
    }

    [Fact]
    public async Task ReleaseControlForgetsTheCarsDrawToo()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "fast");
        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);

        _coordinator.ReleaseControl();

        // The same car is still plugged in, but a newly selected mode must not inherit the verdict that
        // it has already charged -- it would end itself on the next idle poll.
        await _coordinator.RunCycleAsync(Connected(Now.AddMinutes(1)), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.False(_controller.LastInput!.EvDrewPower);
    }

    [Fact]
    public async Task ACompletedSessionPausesTheChargerAndIsReportedUpwards()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 16);
        _controller.NextDecision = new(
            ChargingControlAction.Pause, null, "car finished", SessionComplete: true);

        var result = await Cycle();

        Assert.Equal(0, _charger.LastTarget); // pauseCurrentAmps -- not left armed at 16A
        Assert.True(result.SessionComplete);
    }

    [Fact]
    public async Task ADroppedChargerReading_DoesNotRestartTheSessionEnergy()
    {
        // Unknown means the charger didn't answer this poll. Read as "no car", it looks like an unplug
        // followed by a replug, which zeroes the session's energy and its "the car has drawn power"
        // verdict -- both of which the strategies meter their decisions against.
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 6, "charging");

        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(3)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Unreachable(Now.AddMinutes(4)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(5)), ChargeControlMode.Solar, null, CancellationToken.None);

        // 207Wh over the first three minutes, and the blink neither zeroed it nor stopped it accruing.
        Assert.True(_coordinator.SessionEnergyWh > 207);
    }

    private static EnergyState State(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Available, EvChargerPowerWatts: 0);

    private static EnergyState Charging(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Charging, EvChargerPowerWatts: 4140);

    // The car says it is done, but hasn't stopped drawing yet.
    private static EnergyState WindingDown(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.SuspendedEv, EvChargerPowerWatts: 1000);

    // The charger stopped answering Modbus: the reader reports Unknown and zero power.
    private static EnergyState Unreachable(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Unknown, EvChargerPowerWatts: 0);

    // Plugged in, drawing nothing.
    private static EnergyState Connected(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Preparing, EvChargerPowerWatts: 0);
}

