using Solax.Core.Enums;

namespace Solax.Core.Models;

/// <summary>
/// The input a <see cref="Interfaces.IChargingController"/> needs to decide the charging current this
/// cycle.
/// </summary>
/// <param name="State">The latest energy snapshot (carries SOC and the solar surplus source).</param>
/// <param name="SurplusWatts">
/// The solar surplus to decide on — the <em>smoothed</em> (moving-average) value, not the
/// instantaneous <see cref="EnergyState.SolarSurplusPowerWatts"/>, so a momentary cloud doesn't
/// interrupt a long charging session.
/// </param>
/// <param name="CurrentSettings">The charger's currently active settings (use-mode + current setpoint), read from the hardware.</param>
/// <param name="Charging">Whether we are currently charging (vs paused) — our own state, used for hysteresis.</param>
public sealed record ChargingControlInput(
    EnergyState State,
    double SurplusWatts,
    EvChargerSettings CurrentSettings,
    bool Charging);

/// <summary>
/// The controller's intent for this cycle. <see cref="ChargeCurrentAmps"/> is populated only for
/// <see cref="ChargingControlAction.Charge"/> (the current to set); <see cref="ChargingControlAction.Pause"/>
/// and <see cref="ChargingControlAction.None"/> leave it null. <see cref="Reason"/> is a short
/// human-readable explanation for logging.
/// </summary>
public sealed record ChargingControlDecision(
    ChargingControlAction Action,
    int? ChargeCurrentAmps,
    string Reason);
