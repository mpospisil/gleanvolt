using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Worker;

/// <summary>
/// Orchestrates one charge-control cycle: reads the charger's settings, asks the
/// <see cref="IChargingController"/> selected by the current mode for the target current, and writes
/// the current setpoint — nothing else. It never changes the charger's use-mode or starts/stops the
/// session; the owner keeps the charger in Fast mode and this only modulates the current (or drops it
/// to the pause value).
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
        TimeProvider? timeProvider = null)
    {
        _controllers = controllers;
        _chargerControl = chargerControl;
        _surplusAverage = surplusAverage;
        _pauseCurrentAmps = Math.Clamp(pauseCurrentAmps, 0, EvChargerLimits.MaxCurrentAmps);
        _idlePowerThresholdWatts = idlePowerThresholdWatts;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Energy delivered to the car in the current session, in watt-hours.</summary>
    public double SessionEnergyWh => _sessionEnergy.EnergyWattHours;

    /// <summary>Energy lent out of the home battery to the car today, in watt-hours.</summary>
    public double LoanedTodayWh => _loanedToday.EnergyWattHours;

    /// <param name="state">The latest telemetry reading.</param>
    /// <param name="mode">The mode selected at runtime; picks which controller decides this cycle.</param>
    /// <param name="plan">The forecast-driven day plan, or null when the mode doesn't use one.</param>
    public async Task<ChargeControlCycleResult> RunCycleAsync(
        EnergyState state,
        ChargeControlMode mode,
        SolarDayPlan? plan,
        CancellationToken cancellationToken)
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
                TimeInCurrentState(state.Timestamp),
                _sessionEnergy.EnergyWattHours,
                _loanedToday.EnergyWattHours,
                _evDrewPower,
                EvIdleFor(state.Timestamp)));

            _logger.LogInformation(
                "Charge control: Mode={Mode} ChargerMode={ChargerMode} Surplus={RawSurplusWatts:F0}W Avg={AveragedSurplusWatts:F0}W "
                + "({SampleCount} samples) Setpoint={SetpointAmps}A Action={Action} Target={TargetAmps} Loan={LoanWatts:F0}W "
                + "Session={SessionKWh:F1}kWh LoanedToday={LoanedKWh:F1}kWh. {Reason}",
                mode,
                settings.Mode,
                rawSurplus,
                averagedSurplus,
                _surplusAverage.Count,
                settings.ChargeCurrentAmps,
                decision.Action,
                decision.ChargeCurrentAmps is int amps ? $"{amps}A" : "n/a",
                decision.LoanPowerWatts,
                _sessionEnergy.EnergyWattHours / 1000,
                _loanedToday.EnergyWattHours / 1000,
                decision.Reason);

            switch (decision.Action)
            {
                case ChargingControlAction.Charge:
                    await _chargerControl.SetCurrentAsync(settings.ChargeCurrentAmps, decision.ChargeCurrentAmps!.Value, decision.Reason, cancellationToken).ConfigureAwait(false);
                    SetCharging(true, state.Timestamp);
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
                _ => ChargeControlState.Idle,
            };

            return new ChargeControlCycleResult(
                reportedState, averagedSurplus, decision.ChargeCurrentAmps, _charging, decision.LoanPowerWatts, decision.SessionComplete);
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
    }

    /// <summary>
    /// Pauses charging on shutdown if we were driving it, so we don't strand the charger at a fixed
    /// current after the service stops. No-op when we weren't charging; failures are logged.
    /// </summary>
    public async Task PauseOnShutdownAsync(CancellationToken cancellationToken)
    {
        if (!_charging)
        {
            return;
        }

        try
        {
            var settings = await _chargerControl.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            await _chargerControl.SetCurrentAsync(settings.ChargeCurrentAmps, _pauseCurrentAmps, "Service stopping.", cancellationToken).ConfigureAwait(false);
            _charging = false;
            _logger.LogInformation("Paused charging on shutdown (current setpoint dropped to {Amps}A).", _pauseCurrentAmps);
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
        var connected = state.EvChargerStatus.IsCarConnected();
        if (connected && !_carWasConnected)
        {
            _sessionEnergy.Reset();
            _evDrewPower = false;
            _evIdleSince = null;
        }

        _carWasConnected = connected;
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

        _charging = charging;
    }
}
