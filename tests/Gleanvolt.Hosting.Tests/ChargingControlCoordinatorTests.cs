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

    // What the poll loop does on every poll while the mode is Off.
    private void OffPoll(EnergyState state)
    {
        _coordinator.TrackWhileReleased(state);
        _coordinator.ReleaseControl();
    }

    /// <summary>
    /// The production sequence behind #283: the mode ends itself on a finished car, the car is unplugged
    /// and plugged back in the next evening all while Off, and only then is a mode picked. The new
    /// session starts from zero and includes what the car took before the mode was picked.
    /// </summary>
    [Fact]
    public async Task UnpluggingWhileOff_StartsTheNextSessionAfresh_AndCountsTheDrawBeforeAModeIsPicked()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 6, "charging");

        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(3)), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.Equal(207, _coordinator.SessionEnergyWh, 0);

        OffPoll(Finished(Now.AddMinutes(3).AddSeconds(1)));
        OffPoll(State(Now.AddMinutes(10)));                // unplugged
        OffPoll(Charging(Now.AddHours(20)));               // plugged in; the charger starts it at Fast by itself
        OffPoll(Charging(Now.AddHours(20).AddMinutes(3)));

        await _coordinator.RunCycleAsync(Charging(Now.AddHours(20).AddMinutes(4)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.Equal(276, _coordinator.SessionEnergyWh, 0); // 4 minutes at 4140 W, none of yesterday's
    }

    [Fact]
    public async Task ACarLeftPluggedInThroughOff_KeepsItsSessionEnergy()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 6, "charging");

        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(3)), ChargeControlMode.Solar, null, CancellationToken.None);

        // A stopped charger with the car still in it reads Finishing, which is still a plugged-in car.
        OffPoll(Finished(Now.AddMinutes(4)));
        OffPoll(Finished(Now.AddMinutes(30)));

        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(31)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.Equal(276, _coordinator.SessionEnergyWh, 0);
    }

    [Fact]
    public async Task ADrawSeenWhileOff_IsNotHandedToTheModePickedNext()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "fast");

        OffPoll(Charging(Now));
        OffPoll(Connected(Now.AddMinutes(1)));

        await _coordinator.RunCycleAsync(Connected(Now.AddMinutes(2)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.False(_controller.LastInput!.EvDrewPower);
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
    public async Task AStandDownIsNotReadAsTheCarLeavingAndComingBack()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);

        // The car charges, so there is session energy and a "has drawn power" verdict worth losing.
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "charge");
        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(3)), ChargeControlMode.Solar, null, CancellationToken.None);
        var energyBefore = _coordinator.SessionEnergyWh;
        Assert.True(energyBefore > 0);

        // The plan defers, so the charger is stood down and now reports Available with the car still
        // attached -- a reading that is about the use-mode, not the plug.
        // The poll that decides to stand down still reads the charger as it was -- the Stop write happens
        // after the decision -- so the Available readings begin on the cycle after.
        _controller.NextDecision = new(ChargingControlAction.StandDown, null, "waiting until 05:27");
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(4)), ChargeControlMode.Solar, null, CancellationToken.None);

        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Stop, 6);
        await _coordinator.RunCycleAsync(StoodDownWithCarAttached(Now.AddMinutes(5)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(StoodDownWithCarAttached(Now.AddHours(7)), ChargeControlMode.Solar, null, CancellationToken.None);

        // The appointment arrives and the charger is armed again. This must not look like a fresh car.
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "starting");
        await _coordinator.RunCycleAsync(Charging(Now.AddHours(7).AddMinutes(1)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.True(_controller.LastInput!.EvDrewPower);
        Assert.True(_coordinator.SessionEnergyWh >= energyBefore);
    }

    [Fact]
    public async Task TheCarFullRuleSeesWhetherTheCarWasDrawingOnThePollBefore()
    {
        // On a full car this wallbox goes from drawing to Finishing / Stop in one poll (#280), so the rule
        // needs the poll before, and a poll the charger did not answer is not one.
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 16);
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "charge");

        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.False(_controller.LastInput!.EvWasDrawing);

        await _coordinator.RunCycleAsync(Unreachable(Now.AddSeconds(8)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Finishing(Now.AddSeconds(16)), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.True(_controller.LastInput!.EvWasDrawing);

        await _coordinator.RunCycleAsync(Finishing(Now.AddSeconds(24)), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.False(_controller.LastInput!.EvWasDrawing);
    }

    [Fact]
    public async Task ReArmingOutOfAWaitForSunLeavesALaterAppointmentToBeKept()
    {
        // A targeted charge stood down overnight for sun, re-armed when it comes, and then due to hold its
        // tail until the afternoon: only leaving an appointment makes the release one-way.
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);

        _controller.NextDecision = new(ChargingControlAction.StandDown, null, "waiting for sun", WaitUntil: Now.AddHours(9));
        var waiting = await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.Equal(Now.AddHours(9), waiting.WaitUntil);
        Assert.True(waiting.ChargerStoodDown);

        _controller.NextDecision = new(ChargingControlAction.Charge, 10, "sun");
        await _coordinator.RunCycleAsync(Charging(Now.AddHours(9)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Charging(Now.AddHours(9).AddMinutes(1)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.Equal(EvChargerMode.Fast, _charger.ModeWrites[^1].Mode);
        Assert.False(_controller.LastInput!.WaitAlreadyReleased);

        // The held tail, then its release: that one latches.
        _controller.NextDecision = new(ChargingControlAction.StandDown, null, "holding the tail", Appointment: true);
        await _coordinator.RunCycleAsync(Charging(Now.AddHours(10)), ChargeControlMode.Solar, null, CancellationToken.None);

        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "tail");
        await _coordinator.RunCycleAsync(Charging(Now.AddHours(14)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Charging(Now.AddHours(14).AddMinutes(1)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.True(_controller.LastInput!.WaitAlreadyReleased);
    }

    [Fact]
    public async Task OnlyAWaitCarriesAWaitUntil()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);

        _controller.NextDecision = new(ChargingControlAction.Pause, null, "dip", WaitUntil: Now.AddMinutes(3));
        Assert.Equal(Now.AddMinutes(3), (await Cycle()).WaitUntil);

        _controller.NextDecision = new(ChargingControlAction.Charge, 6, "charge");
        Assert.Null((await Cycle()).WaitUntil);
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
            ChargingControlAction.Pause, null, "car finished", EndsSession: ChargingSessionEndReason.SessionComplete);

        var result = await Cycle();

        Assert.Equal(0, _charger.LastTarget); // pauseCurrentAmps -- not left armed at 16A
        Assert.Equal(ChargingSessionEndReason.SessionComplete, result.EndsSession);
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

    [Fact]
    public async Task ADroppedChargerReading_IsNotCountedAsTheCarDrawingNothing()
    {
        // The reader fills in 0W for a charger that did not answer. The car drew straight through the
        // blink, so five minutes at 4140W is what the session took -- not four.
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 6, "charging");

        await _coordinator.RunCycleAsync(Charging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(3)), ChargeControlMode.Solar, null, CancellationToken.None);
        await _coordinator.RunCycleAsync(Unreachable(Now.AddMinutes(4)), ChargeControlMode.Solar, null, CancellationToken.None);

        // Nor did the blink start the car's idle clock.
        Assert.Equal(TimeSpan.Zero, _controller.LastInput!.EvIdleFor);

        await _coordinator.RunCycleAsync(Charging(Now.AddMinutes(5)), ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.Equal(345, _coordinator.SessionEnergyWh, 0);
    }

    [Fact]
    public async Task ADroppedChargerReading_StaysOutOfTheSurplusAverage()
    {
        // 2026-09-28 13:01: the car drawing 4.2kW, the charger blinked, and its 0W put the car in the
        // house load -- a -4238W sample that dragged the average under the floor and paused the charge
        // for fifteen minutes of full sun.
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 6, "charging");

        await _coordinator.RunCycleAsync(SunnyCharging(Now), ChargeControlMode.Solar, null, CancellationToken.None);
        Assert.Equal(5000, _controller.LastInput!.SurplusWatts, 0);

        var blink = SunnyCharging(Now.AddSeconds(10)) with { EvChargerStatus = EvChargerStatus.Unknown, EvChargerPowerWatts = 0 };
        await _coordinator.RunCycleAsync(blink, ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.Equal(5000, _controller.LastInput!.SurplusWatts, 0);
    }

    [Fact]
    public async Task ADroppedChargerReading_WithNothingInTheWindow_DecidesOnTheRawSurplus()
    {
        var blink = SunnyCharging(Now) with { EvChargerStatus = EvChargerStatus.Unknown, EvChargerPowerWatts = 0 };

        await _coordinator.RunCycleAsync(blink, ChargeControlMode.Solar, null, CancellationToken.None);

        Assert.Equal(blink.SolarSurplusPowerWatts, _controller.LastInput!.SurplusWatts, 0);
    }

    // 6kW of sun: 1kW to the house, 4.2kW to the car and the last 0.8kW exported.
    private static EnergyState SunnyCharging(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 6000, GridPowerWatts: -800,
            EvChargerStatus.Charging, EvChargerPowerWatts: 4200);

    private static EnergyState State(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Available, EvChargerPowerWatts: 0);

    private static EnergyState Charging(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Charging, EvChargerPowerWatts: 4140);

    // The poll after a full car: the wallbox has closed the session and switched itself to Stop.
    private static EnergyState Finishing(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Finishing, EvChargerPowerWatts: 0);

    // The car says it is done, but hasn't stopped drawing yet.
    private static EnergyState WindingDown(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.SuspendedEv, EvChargerPowerWatts: 1000);

    // The charger stopped answering Modbus: the reader reports Unknown and zero power.
    private static EnergyState Unreachable(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Unknown, EvChargerPowerWatts: 0);

    // What a charger in Stop reports with a car plugged into it -- indistinguishable from an empty
    // socket. Seen on 2026-08-28 at 22:19:04, eight seconds before the same car drew 10966W.
    private static EnergyState StoodDownWithCarAttached(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Available, EvChargerPowerWatts: 0);

    // Plugged in, drawing nothing.
    [Fact]
    public async Task ChargedThisMode_CountsOnlyADrawTheModeAskedFor_AndResetsOnADirectModeSwitch()
    {
        var solar = new StubChargingController();
        var solarGrid = new StubChargingController();
        var coordinator = new ChargingControlCoordinator(
            new Dictionary<ChargeControlMode, IChargingController>
            {
                [ChargeControlMode.Solar] = solar,
                [ChargeControlMode.SolarGrid] = solarGrid,
            },
            _charger,
            new SurplusMovingAverage(TimeSpan.FromMinutes(3)),
            pauseCurrentAmps: 0,
            idlePowerThresholdWatts: 200,
            NullLogger<ChargingControlCoordinator>.Instance);
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 16);
        solar.NextDecision = new(ChargingControlAction.Charge, 6, "charge");
        solarGrid.NextDecision = new(ChargingControlAction.Charge, 6, "charge");

        // The charger started the car by itself at plug-in: drawn power, but nothing this mode asked for.
        await coordinator.RunCycleAsync(Drawing(Now), ChargeControlMode.SolarGrid, plan: null, CancellationToken.None);
        Assert.True(solarGrid.LastInput!.EvDrewPower);
        Assert.False(solarGrid.LastInput.ChargedThisMode);

        // Still drawing on the next poll, now at the mode's own request.
        await coordinator.RunCycleAsync(Drawing(Now.AddSeconds(8)), ChargeControlMode.SolarGrid, plan: null, CancellationToken.None);
        Assert.True(solarGrid.LastInput!.ChargedThisMode);

        // Straight to another controlled mode with no Off in between: it has charged nothing yet.
        await coordinator.RunCycleAsync(Drawing(Now.AddSeconds(16)), ChargeControlMode.Solar, plan: null, CancellationToken.None);
        Assert.False(solar.LastInput!.ChargedThisMode);
        Assert.True(solar.LastInput.EvDrewPower);
    }

    private static EnergyState Drawing(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Charging, EvChargerPowerWatts: 10_900);

    private static EnergyState Connected(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Preparing, EvChargerPowerWatts: 0);

    private static EnergyState Finished(DateTimeOffset at) =>
        new(at, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0,
            EvChargerStatus.Finishing, EvChargerPowerWatts: 0);
}

