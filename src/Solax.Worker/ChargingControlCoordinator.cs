using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Worker;

/// <summary>
/// Orchestrates one charge-control cycle: reads the charger's current settings, asks the
/// <see cref="IChargingController"/> what to do, and applies it — starting a session on the
/// transition into charging, and pausing (never terminating) it when the surplus runs out.
///
/// Holds the "do we currently drive the charger" flag across cycles, so it is registered as a
/// singleton. All hardware errors are caught and logged so a control failure never disrupts the
/// polling loop. Only invoked when <c>ChargeControl:Enabled</c> is true.
/// </summary>
public sealed class ChargingControlCoordinator
{
    private readonly IChargingController _controller;
    private readonly IEvChargerControl _chargerControl;
    private readonly SurplusMovingAverage _surplusAverage;
    private readonly ILogger<ChargingControlCoordinator> _logger;

    // True once we've overridden the charger, until it has been reset back to the idle state.
    private bool _hasControl;

    public ChargingControlCoordinator(
        IChargingController controller,
        IEvChargerControl chargerControl,
        SurplusMovingAverage surplusAverage,
        ILogger<ChargingControlCoordinator> logger)
    {
        _controller = controller;
        _chargerControl = chargerControl;
        _surplusAverage = surplusAverage;
        _logger = logger;
    }

    public async Task<ChargeControlCycleResult> RunCycleAsync(EnergyState state, CancellationToken cancellationToken)
    {
        try
        {
            var current = await _chargerControl.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

            // Decide on the smoothed surplus, not the instantaneous value, so a passing cloud can't
            // interrupt a long charging session.
            var rawSurplus = state.SolarSurplusPowerWatts;
            var averagedSurplus = _surplusAverage.Add(state.Timestamp, rawSurplus);

            var input = new ChargingControlInput(
                state,
                SurplusWatts: averagedSurplus,
                CurrentSettings: current,
                HasControl: _hasControl);

            var decision = _controller.Decide(input);

            _logger.LogInformation(
                "Charge control: Surplus={RawSurplusWatts:F0}W Avg={AveragedSurplusWatts:F0}W ({SampleCount} samples) Setpoint={SetpointAmps}A Target={TargetAmps} Action={Action}. {Reason}",
                rawSurplus,
                averagedSurplus,
                _surplusAverage.Count,
                current.ChargeCurrentAmps,
                decision.TargetSettings is null ? "n/a" : $"{decision.TargetSettings.ChargeCurrentAmps}A",
                decision.Action,
                decision.Reason);

            switch (decision.Action)
            {
                case ChargingControlAction.None:
                    break;

                case ChargingControlAction.Charge:
                    await ChargeAsync(current, decision, cancellationToken).ConfigureAwait(false);
                    break;

                case ChargingControlAction.Pause:
                    await PauseChargingAsync(decision.Reason, cancellationToken).ConfigureAwait(false);
                    break;
            }

            var reportedState = decision.Action switch
            {
                ChargingControlAction.Charge => ChargeControlState.Charging,
                ChargingControlAction.Pause => ChargeControlState.Paused,
                _ => ChargeControlState.Idle,
            };

            return new ChargeControlCycleResult(
                reportedState,
                averagedSurplus,
                decision.TargetSettings?.ChargeCurrentAmps,
                _hasControl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Charge-control cycle failed; will retry next poll.");
            return new ChargeControlCycleResult(
                _hasControl ? ChargeControlState.Charging : ChargeControlState.Idle,
                SurplusWatts: null,
                TargetCurrentAmps: null,
                HoldingControl: _hasControl);
        }
    }

    /// <summary>
    /// Releases control by pausing charging — used when control is switched off at runtime or the
    /// service is shutting down, so we never leave the charger drawing under our override. No-op when
    /// we don't hold control; failures are logged rather than thrown.
    /// </summary>
    public async Task ReleaseControlAsync(string reason, CancellationToken cancellationToken)
    {
        if (!_hasControl)
        {
            return;
        }

        try
        {
            await PauseChargingAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release charge control; the charger may still be charging under our override.");
        }
    }

    private async Task ChargeAsync(
        EvChargerSettings current,
        ChargingControlDecision decision,
        CancellationToken cancellationToken)
    {
        var starting = !_hasControl;

        // ApplyAsync writes only the fields that differ and logs each change.
        await _chargerControl
            .ApplyAsync(current, decision.TargetSettings!, decision.Reason, cancellationToken)
            .ConfigureAwait(false);

        // The charger may be sitting idle/stopped (e.g. after our own reset), so a mode change alone
        // won't necessarily begin a session -- issue the start command, but only on the transition so
        // we don't re-send it every poll while charging.
        if (starting)
        {
            await _chargerControl
                .SendCommandAsync(EvChargerControlCommand.StartCharging, decision.Reason, cancellationToken)
                .ConfigureAwait(false);
            _hasControl = true;
            _logger.LogInformation("Took charge control; started charging from live solar surplus.");
        }
    }

    private async Task PauseChargingAsync(string reason, CancellationToken cancellationToken)
    {
        if (!_hasControl)
        {
            return;
        }

        await _chargerControl.PauseAsync(reason, cancellationToken).ConfigureAwait(false);

        // Only released once the pause writes succeeded, so a failed pause is retried next cycle.
        _hasControl = false;
        _logger.LogInformation(
            "Paused charging; charger suspended at {Amps}A. The session is left intact so it can resume.",
            EvChargerLimits.MinCurrentAmps);
    }
}
