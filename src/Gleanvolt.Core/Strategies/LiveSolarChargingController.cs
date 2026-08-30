using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Decides the charging <em>current</em> from live solar surplus, while the home battery is full. It
/// decides nothing about the use-mode: starting this strategy is what put the charger into Fast (see
/// <see cref="IChargeActions"/>), and all this does is modulate the current setpoint under it (or drop
/// it to the pause value when there isn't enough sun).
///
/// It only acts while the charger's own use-mode is <see cref="EvChargerMode.Fast"/> — in any other
/// mode (Green/Eco/Stop) it returns <see cref="ChargingControlAction.None"/> and leaves the charger
/// alone. Nothing writes Fast back: a charger changed at the wallbox mid-session belongs to whoever
/// changed it. Above the battery-SOC gate it sets the current the surplus can cover (phase-aware, whole
/// amps, clamped to min/max), with resume hysteresis; below the gate or below the minimum it pauses.
/// </summary>
public sealed class LiveSolarChargingController : IChargingController
{
    private readonly ChargePowerConverter _power;
    private readonly int _minChargingCurrentAmps;
    private readonly int _maxChargingCurrentAmps;
    private readonly int _currentStepAmps;
    private readonly double _hysteresisWatts;
    private readonly double _fullSocPercent;
    private readonly double _releaseSocPercent;

    public LiveSolarChargingController(
        ChargePowerConverter powerConverter,
        int minChargingCurrentAmps,
        int maxChargingCurrentAmps,
        int currentStepAmps,
        double hysteresisWatts,
        double fullSocPercent,
        double releaseSocPercent)
    {
        if (currentStepAmps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentStepAmps), currentStepAmps, "Current step must be positive.");
        }

        if (releaseSocPercent > fullSocPercent)
        {
            throw new ArgumentException(
                $"Release SOC ({releaseSocPercent}%) must not exceed the full SOC ({fullSocPercent}%).",
                nameof(releaseSocPercent));
        }

        _power = powerConverter ?? throw new ArgumentNullException(nameof(powerConverter));

        // Constrain the configured range to what the hardware accepts, so we never target (or log) a
        // current the charger would reject.
        _minChargingCurrentAmps = Math.Clamp(minChargingCurrentAmps, EvChargerLimits.MinCurrentAmps, EvChargerLimits.MaxCurrentAmps);
        _maxChargingCurrentAmps = Math.Clamp(maxChargingCurrentAmps, _minChargingCurrentAmps, EvChargerLimits.MaxCurrentAmps);
        _currentStepAmps = currentStepAmps;
        _hysteresisWatts = hysteresisWatts;
        _fullSocPercent = fullSocPercent;
        _releaseSocPercent = releaseSocPercent;
    }

    public ChargingControlDecision Decide(ChargingControlInput input)
    {
        // Precondition: only modulate the current while the charger is in Fast. Starting the mode wrote
        // Fast once; if the charger has left it since, somebody changed it at the wallbox and we don't
        // control it at all -- nothing here writes Fast back.
        if (ChargerOwnership.NotOurs(input) is { } notOurs)
        {
            return notOurs;
        }

        // "Are we charging?" for the hysteresis is our own state (driven by the HA mode), not a read of
        // the charger's registers.
        var charging = input.Charging;

        // Battery-SOC gate with hysteresis: engage only at/above the full threshold, but once engaged
        // keep going down to the release threshold, so EV load dipping the battery doesn't immediately
        // close the gate.
        var soc = input.State.BatterySocPercent;
        var gateOpen = charging ? soc >= _releaseSocPercent : soc >= _fullSocPercent;
        if (!gateOpen)
        {
            var threshold = charging ? _releaseSocPercent : _fullSocPercent;
            return Pause($"Battery {soc:F0}% below {threshold:F0}% full-battery gate.");
        }

        // The smoothed (moving-average) surplus, so a passing cloud doesn't interrupt the session.
        var availableWatts = input.SurplusWatts;
        var minWatts = _power.AmpsToWatts(_minChargingCurrentAmps);

        // Asymmetric threshold: keep charging down to the minimum, but only (re)start once we're a
        // hysteresis margin above it.
        var startThresholdWatts = charging ? minWatts : minWatts + _hysteresisWatts;
        if (availableWatts < startThresholdWatts)
        {
            return Pause($"Live surplus {availableWatts:F0}W below {(charging ? "minimum" : "start")} threshold {startThresholdWatts:F0}W.");
        }

        var targetAmps = ToHardwareCurrent(availableWatts);
        if (targetAmps < _minChargingCurrentAmps)
        {
            return Pause($"Live surplus {availableWatts:F0}W quantises below minimum {_minChargingCurrentAmps}A.");
        }

        return new ChargingControlDecision(
            ChargingControlAction.Charge, targetAmps, $"Live surplus {availableWatts:F0}W -> charge at {targetAmps}A.");
    }

    private static ChargingControlDecision Pause(string reason) =>
        new(ChargingControlAction.Pause, null, reason);

    // Converts available watts to a whole-amp setpoint the charger accepts: convert to amps
    // (phase-aware), floor to the current step, then clamp to the charger's max. May return below the
    // minimum, which the caller treats as "pause".
    private int ToHardwareCurrent(double availableWatts)
    {
        var rawAmps = _power.WattsToAmps(availableWatts);
        var steppedAmps = (int)Math.Floor(rawAmps / _currentStepAmps) * _currentStepAmps;
        return Math.Min(steppedAmps, _maxChargingCurrentAmps);
    }
}
