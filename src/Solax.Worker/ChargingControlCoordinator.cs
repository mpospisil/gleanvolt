using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Worker;

/// <summary>
/// Orchestrates one charge-control cycle: reads the charger's settings, asks the
/// <see cref="IChargingController"/> for the target current, and writes the current setpoint —
/// nothing else. It never changes the charger's use-mode or starts/stops the session; the owner keeps
/// the charger in Fast mode and this only modulates the current (or drops it to the pause value).
///
/// Tracks whether we are currently charging (vs paused) across cycles for the hysteresis, so it is
/// registered as a singleton. Hardware errors are caught and logged so a failure never disrupts the
/// polling loop.
/// </summary>
public sealed class ChargingControlCoordinator
{
    private readonly IChargingController _controller;
    private readonly IEvChargerControl _chargerControl;
    private readonly SurplusMovingAverage _surplusAverage;
    private readonly int _pauseCurrentAmps;
    private readonly ILogger<ChargingControlCoordinator> _logger;

    // Our own "are we charging?" state (vs paused), used for the controller's hysteresis.
    private bool _charging;

    public ChargingControlCoordinator(
        IChargingController controller,
        IEvChargerControl chargerControl,
        SurplusMovingAverage surplusAverage,
        int pauseCurrentAmps,
        ILogger<ChargingControlCoordinator> logger)
    {
        _controller = controller;
        _chargerControl = chargerControl;
        _surplusAverage = surplusAverage;
        _pauseCurrentAmps = Math.Clamp(pauseCurrentAmps, 0, EvChargerLimits.MaxCurrentAmps);
        _logger = logger;
    }

    public async Task<ChargeControlCycleResult> RunCycleAsync(EnergyState state, CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _chargerControl.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

            // Decide on the smoothed surplus, not the instantaneous value, so a passing cloud can't
            // interrupt a long charging session.
            var rawSurplus = state.SolarSurplusPowerWatts;
            var averagedSurplus = _surplusAverage.Add(state.Timestamp, rawSurplus);

            var decision = _controller.Decide(new ChargingControlInput(state, averagedSurplus, settings, _charging));

            _logger.LogInformation(
                "Charge control: Mode={ChargerMode} Surplus={RawSurplusWatts:F0}W Avg={AveragedSurplusWatts:F0}W ({SampleCount} samples) Setpoint={SetpointAmps}A Action={Action} Target={TargetAmps}. {Reason}",
                settings.Mode,
                rawSurplus,
                averagedSurplus,
                _surplusAverage.Count,
                settings.ChargeCurrentAmps,
                decision.Action,
                decision.ChargeCurrentAmps is int amps ? $"{amps}A" : "n/a",
                decision.Reason);

            switch (decision.Action)
            {
                case ChargingControlAction.Charge:
                    await _chargerControl.SetCurrentAsync(settings.ChargeCurrentAmps, decision.ChargeCurrentAmps!.Value, decision.Reason, cancellationToken).ConfigureAwait(false);
                    _charging = true;
                    break;

                case ChargingControlAction.Pause:
                    await _chargerControl.SetCurrentAsync(settings.ChargeCurrentAmps, _pauseCurrentAmps, decision.Reason, cancellationToken).ConfigureAwait(false);
                    _charging = false;
                    break;

                case ChargingControlAction.None:
                    // Not in Fast mode -- we aren't controlling; leave the setpoint as it is.
                    _charging = false;
                    break;
            }

            var reportedState = decision.Action switch
            {
                ChargingControlAction.Charge => ChargeControlState.Charging,
                ChargingControlAction.Pause => ChargeControlState.Paused,
                _ => ChargeControlState.Idle,
            };

            return new ChargeControlCycleResult(reportedState, averagedSurplus, decision.ChargeCurrentAmps, _charging);
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
    /// as it is. Only resets our internal charging state so re-entering Solar starts fresh.
    /// </summary>
    public void ReleaseControl()
    {
        _charging = false;
        _surplusAverage.Reset();
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
}
