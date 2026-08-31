using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Hosting;

/// <summary>
/// Orchestrates one charge-control cycle: reads the charger's settings, asks the
/// <see cref="IChargingController"/> selected by the current mode for the target current, and writes
/// the current setpoint — nothing else. The use-mode is not this loop's business: an action put the
/// charger into Fast when the strategy was started (see <see cref="Core.Interfaces.IChargeActions"/>)
/// and nothing re-asserts it, so all this does is modulate the current under it (or drop it to the
/// pause value).
///
/// Holds every piece of cross-cycle state the strategies need but must not own themselves — whether we
/// are charging, how long we have been in that state, energy delivered this session, and energy lent
/// out of the home battery today — so the controllers stay pure. Registered as a singleton. Hardware
/// errors are caught and logged so a failure never disrupts the polling loop.
/// </summary>
public sealed class ChargingControlCoordinator
{
    private readonly IReadOnlyDictionary<ChargeControlMode, IChargingController> _controllers;
    private readonly IEvChargerControl _chargerControl;
    private readonly SurplusMovingAverage _surplusAverage;
    private readonly int _pauseCurrentAmps;
    private readonly double _idlePowerThresholdWatts;
    private readonly ILogger<ChargingControlCoordinator> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _shutdownPauseTimeout;

    /// <summary>
    /// How long <see cref="PauseOnShutdownAsync"/> may spend trying to release the charger. Generous
    /// against a charger that answers (it needs well under a second) and short against one that does
    /// not: two unanswered Modbus exchanges cost 10 seconds on their own, and the whole shutdown has
    /// to fit inside the container's stop grace period with room for everything else.
    /// </summary>
    public static readonly TimeSpan DefaultShutdownPauseTimeout = TimeSpan.FromSeconds(10);

    // Our own "are we charging?" state (vs paused), used for the controller's hysteresis, plus when it
    // last changed -- the dwell timers that stop the charger flapping are measured from it.
    private bool _charging;
    private DateTimeOffset? _stateChangedAt;

    // Energy the car has taken in the current session (reset when it is unplugged) and energy the home
    // battery has lent it today (reset at local midnight): the two budgets the forecast-driven strategy
    // is metered against.
    private readonly EnergyIntegrator _sessionEnergy = new();
    private readonly EnergyIntegrator _loanedToday = new();
    private DateOnly _loanDay;
    private bool _carWasConnected;

    // What the car itself is doing, as opposed to what we asked for: whether it has drawn power at all
    // this session, and since when it has been drawing nothing. Together they are how the fast mode
    // tells "finished charging" from "hasn't started yet" -- the two are identical on power alone.
    private bool _evDrewPower;
    private bool _stoodDown;
    private DateTimeOffset? _chargerNotFastSince;
    private bool _waitReleased;
    private DateTimeOffset? _evIdleSince;

    /// <param name="idlePowerThresholdWatts">
    /// Below this draw the car counts as not charging. Well above a charger's standby reading and well
    /// below its 6 A floor, so nothing in between is ambiguous.
    /// </param>
    public ChargingControlCoordinator(
        IReadOnlyDictionary<ChargeControlMode, IChargingController> controllers,
        IEvChargerControl chargerControl,
        SurplusMovingAverage surplusAverage,
        int pauseCurrentAmps,
        double idlePowerThresholdWatts,
        ILogger<ChargingControlCoordinator> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? shutdownPauseTimeout = null)
    {
        _controllers = controllers;
        _chargerControl = chargerControl;
        _surplusAverage = surplusAverage;
        _pauseCurrentAmps = Math.Clamp(pauseCurrentAmps, 0, EvChargerLimits.MaxCurrentAmps);
        _idlePowerThresholdWatts = idlePowerThresholdWatts;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _shutdownPauseTimeout = shutdownPauseTimeout ?? DefaultShutdownPauseTimeout;
    }

    /// <summary>Energy delivered to the car in the current session, in watt-hours.</summary>
    public double SessionEnergyWh => _sessionEnergy.EnergyWattHours;

    /// <summary>Energy lent out of the home battery to the car today, in watt-hours.</summary>
    public double LoanedTodayWh => _loanedToday.EnergyWattHours;

    /// <param name="state">The latest telemetry reading.</param>
    /// <param name="mode">The mode selected at runtime; picks which controller decides this cycle.</param>
    /// <param name="plan">The forecast-driven day plan, or null when the mode doesn't use one.</param>
    /// <param name="targetedPlan">The energy-by-departure plan, or null when the mode doesn't use one.</param>
    /// <param name="fastCharge">
    /// How a limited fast charge is going, or null when there is no limit — which covers both the other
    /// modes and a fast charge asked for as Full.
    /// </param>
    public async Task<ChargeControlCycleResult> RunCycleAsync(
        EnergyState state,
        ChargeControlMode mode,
        SolarDayPlan? plan,
        CancellationToken cancellationToken,
        TargetedChargePlan? targetedPlan = null,
        FastChargeProgress? fastCharge = null)
    {
        if (!_controllers.TryGetValue(mode, out var controller))
        {
            _logger.LogWarning("No charging controller registered for mode {Mode}; leaving the charger alone.", mode);
            return new ChargeControlCycleResult(ChargeControlState.Idle, null, null, HoldingControl: false);
        }

        TrackSession(state);

        try
        {
            var settings = await _chargerControl.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

            // Timed rather than counted, because polls are not evenly spaced and a stretch of failed
            // reads must not look like a short one.
            if (settings.Mode == EvChargerMode.Fast)
            {
                _chargerNotFastSince = null;
            }
            else
            {
                _chargerNotFastSince ??= state.Timestamp;
            }

            // Decide on the smoothed surplus, not the instantaneous value, so a passing cloud can't
            // interrupt a long charging session.
            var rawSurplus = state.SolarSurplusPowerWatts;
            var averagedSurplus = _surplusAverage.Add(state.Timestamp, rawSurplus);

            var decision = controller.Decide(new ChargingControlInput(
                state,
                averagedSurplus,
                settings,
                _charging,
                plan,
                targetedPlan,
                TimeInCurrentState(state.Timestamp),
                _sessionEnergy.EnergyWattHours,
                _loanedToday.EnergyWattHours,
                _evDrewPower,
                EvIdleFor(state.Timestamp),
                fastCharge,
                _stoodDown,
                ChargerNotFastFor(state.Timestamp),
                _waitReleased));

            _logger.LogInformation(
                "Charge control: Mode={Mode} ChargerMode={ChargerMode} Surplus={RawSurplusWatts:F0}W Avg={AveragedSurplusWatts:F0}W "
                + "({SampleCount} samples) Setpoint={SetpointAmps}A Action={Action} Target={TargetAmps} Loan={LoanWatts:F0}W "
                + "Bridge={BridgeWatts:F0}W Session={SessionKWh:F1}kWh LoanedToday={LoanedKWh:F1}kWh. {Reason}",
                mode,
                settings.Mode,
                rawSurplus,
                averagedSurplus,
                _surplusAverage.Count,
                settings.ChargeCurrentAmps,
                decision.Action,
                decision.ChargeCurrentAmps is int amps ? $"{amps}A" : "n/a",
                decision.LoanPowerWatts,
                decision.GridBridgeWatts,
                _sessionEnergy.EnergyWattHours / 1000,
                _loanedToday.EnergyWattHours / 1000,
                decision.Reason);

            switch (decision.Action)
            {
                case ChargingControlAction.Charge:
                    // Re-arm first if we had stood the charger down: it is in Stop, and a current
                    // written to a stopped wallbox charges nothing. Use-mode before setpoint, the same
                    // order ChargeActions.StartAsync uses, so the charger is never briefly Fast at a
                    // stale current.
                    if (_stoodDown)
                    {
                        await _chargerControl.SetModeAsync(EvChargerMode.Fast, decision.Reason, cancellationToken).ConfigureAwait(false);
                        _stoodDown = false;

                        // Latched here rather than on any Charge at all, and that is what keeps a
                        // targeted charge's day-then-hold-then-tail shape working: only leaving a
                        // stand-down counts, and the day's charging never enters one.
                        _waitReleased = true;
                    }

                    await _chargerControl.SetCurrentAsync(settings.ChargeCurrentAmps, decision.ChargeCurrentAmps!.Value, decision.Reason, cancellationToken).ConfigureAwait(false);
                    SetCharging(true, state.Timestamp);
                    break;

                case ChargingControlAction.StandDown:
                    // Idempotent: written once on the transition, not every poll for the whole wait.
                    // SetModeAsync has no read-back and no hysteresis, so re-writing Stop hourly would
                    // be pure noise on a Modbus link that is already dropping ~45 times a day.
                    if (!_stoodDown)
                    {
                        await _chargerControl.SetModeAsync(EvChargerMode.Stop, decision.Reason, cancellationToken).ConfigureAwait(false);
                        _stoodDown = true;
                    }

                    SetCharging(false, state.Timestamp);
                    break;

                case ChargingControlAction.Pause:
                    await _chargerControl.SetCurrentAsync(settings.ChargeCurrentAmps, _pauseCurrentAmps, decision.Reason, cancellationToken).ConfigureAwait(false);
                    SetCharging(false, state.Timestamp);
                    break;

                case ChargingControlAction.None:
                    // Not in Fast mode -- we aren't controlling; leave the setpoint as it is.
                    SetCharging(false, state.Timestamp);
                    break;
            }

            // Metered on what was commanded rather than on the battery's measured discharge: the loan is
            // our own decision, while the battery's actual power also carries house load and PV swings.
            _loanedToday.Add(state.Timestamp, decision.LoanPowerWatts);

            var reportedState = decision.Action switch
            {
                ChargingControlAction.Charge => ChargeControlState.Charging,
                ChargingControlAction.Pause => ChargeControlState.Paused,
                ChargingControlAction.StandDown => ChargeControlState.Paused,
                _ => ChargeControlState.Idle,
            };

            return new ChargeControlCycleResult(
                reportedState, averagedSurplus, decision.ChargeCurrentAmps, _charging, decision.LoanPowerWatts,
                decision.SessionComplete, decision.GridBridgeWatts, _stoodDown);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Charge-control cycle failed; will retry next poll.");
            return new ChargeControlCycleResult(_charging ? ChargeControlState.Charging : ChargeControlState.Idle, null, null, _charging);
        }
    }

    /// <summary>
    /// Called when control is switched Off: we stop controlling but leave the charger's setpoint exactly
    /// as it is. Only resets our internal charging state so re-entering a controlled mode starts fresh.
    /// </summary>
    public void ReleaseControl()
    {
        _charging = false;
        _stateChangedAt = null;
        _surplusAverage.Reset();

        // A mode that ended (including one that ended itself on a finished charge) must not hand its
        // "the car has already charged" verdict to the next one selected on the same plugged-in car.
        _evDrewPower = false;
        _evIdleSince = null;

        // Not a command to the charger -- ReleaseControl deliberately leaves the hardware exactly as it
        // is -- only our claim on the Stop we wrote. Whoever selects the next mode arms the charger
        // through ChargeActions, and a stale claim would let that mode drive a charger its owner had
        // since stopped by hand.
        _stoodDown = false;
        _chargerNotFastSince = null;
        _waitReleased = false;
    }

    /// <summary>
    /// Pauses charging on shutdown if we were driving it, so we don't strand the charger at a fixed
    /// current after the service stops. No-op when we weren't charging; failures are logged.
    ///
    /// <para><b>Bounded on purpose.</b> This is a read followed by a write against a charger that may
    /// have stopped answering, and each unanswered exchange costs a 5-second Modbus timeout (see
    /// <c>ModbusTcpClient</c>). Unbounded, that is exactly the wrong thing to do here: the container's
    /// <c>stop_grace_period</c> is finite, and a shutdown that spends it all waiting on a dead charger
    /// gets SIGKILLed part-way through this very write — with a car still drawing. Better to give up
    /// on a stated deadline and say so in the log, which is at least a fact an operator can act on.
    /// A charger that is answering completes this in well under a second.</para>
    /// </summary>
    public async Task PauseOnShutdownAsync(CancellationToken cancellationToken)
    {
        if (!_charging)
        {
            return;
        }

        // Linked, so the host's own shutdown deadline still wins if it is the shorter of the two.
        using var deadline = new CancellationTokenSource(_shutdownPauseTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
            var settings = await _chargerControl.ReadSettingsAsync(linked.Token).ConfigureAwait(false);
            await _chargerControl.SetCurrentAsync(settings.ChargeCurrentAmps, _pauseCurrentAmps, "Service stopping.", linked.Token).ConfigureAwait(false);
            _charging = false;
            _logger.LogInformation("Paused charging on shutdown (current setpoint dropped to {Amps}A).", _pauseCurrentAmps);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Deliberately a warning with no exception: this is not a bug in progress, it is the
            // charger not answering, and the sentence is the whole point of the line.
            _logger.LogWarning(
                "Gave up pausing the charger on shutdown after {Timeout} — it is not answering. It may "
                + "still be charging under our last setpoint until something else changes it.",
                _shutdownPauseTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pause the charger on shutdown; it may still be charging under our last setpoint.");
        }
    }

    // Session energy belongs to one plugged-in car: unplugging ends the session, so the next car (or the
    // next evening) starts its energy ceiling from zero. The daily loan budget rolls at local midnight.
    private void TrackSession(EnergyState state)
    {
        // A charger that isn't answering reports Unknown, which is no news about the plug. Carrying the
        // last known state through it keeps a blink from reading as unplug-and-replug -- which would
        // reset the session's energy and its "the car has drawn power" verdict half way through a charge.
        // ...and not while we have stood the charger down either. A charger in Stop reports Available
        // or Finishing with a car plugged into it exactly as it does with none, so the reading stops
        // being about the plug at all. Believing it would mark the car gone during the wait and then
        // read the appointment's re-arm as a fresh car -- resetting the session's energy and its "has
        // drawn power" verdict in the middle of the very charge the wait was scheduling.
        if (state.EvChargerStatus.IsConnectionKnown() && !_stoodDown)
        {
            var connected = state.EvChargerStatus.IsCarConnected();
            if (connected && !_carWasConnected)
            {
                _sessionEnergy.Reset();
                _evDrewPower = false;
                _evIdleSince = null;
            }

            _carWasConnected = connected;
        }

        _sessionEnergy.Add(state.Timestamp, Math.Max(0, state.EvChargerPowerWatts));

        // The status is consulted alongside the power because a car can announce it is done while
        // still drawing a trickle (conditioning, cell balancing); waiting for the power alone would
        // then never call the session finished.
        var drawing = state.EvChargerPowerWatts > _idlePowerThresholdWatts && !state.EvChargerStatus.IsChargeWindingDown();
        if (drawing)
        {
            _evDrewPower = true;
            _evIdleSince = null;
        }
        else
        {
            _evIdleSince ??= state.Timestamp;
        }

        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(state.Timestamp, _timeProvider.LocalTimeZone).DateTime);
        if (day != _loanDay)
        {
            _loanDay = day;
            _loanedToday.Reset();
        }
    }

    private TimeSpan ChargerNotFastFor(DateTimeOffset now) =>
        _chargerNotFastSince is { } since && now > since ? now - since : TimeSpan.Zero;

    private TimeSpan EvIdleFor(DateTimeOffset now) =>
        _evIdleSince is { } since && now > since ? now - since : TimeSpan.Zero;

    private TimeSpan TimeInCurrentState(DateTimeOffset now) =>
        _stateChangedAt is { } since && now > since ? now - since : TimeSpan.Zero;

    private void SetCharging(bool charging, DateTimeOffset now)
    {
        if (_charging != charging || _stateChangedAt is null)
        {
            _stateChangedAt = now;
        }

        // Resuming restarts the idle window. The clock measures how long the car has declined power we
        // were actually offering, so time spent at the pause current -- between a targeted mode's
        // blocks, or through a fast charge's wait for its scheduled start -- must not be carried into
        // the judgement. Without this the controllers' Charging guard only moves the failure one poll
        // later: the decision to resume is made while still paused, and the very next poll finds the
        // car not yet drawing with a dwell inherited from the wait. The car gets a fresh dwell to spin
        // up, which is exactly what the dwell is for.
        // Only ever rebased, never started here: a null clock means this poll saw the car drawing, and
        // manufacturing an idle window for a car that is charging is what the test for a winding-down
        // car catches.
        if (charging && !_charging && _evIdleSince is not null)
        {
            _evIdleSince = now;
        }

        _charging = charging;
    }
}
