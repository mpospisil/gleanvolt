namespace Solax.Core.Enums;

/// <summary>
/// What the <see cref="Interfaces.IChargingController"/> wants done to the charger this cycle.
/// The controller only decides intent; applying it (reading current settings, writing only on
/// change, backup/restore, logging) is the orchestrator's job.
/// </summary>
public enum ChargingControlAction
{
    /// <summary>Do nothing — leave the charger's current setpoint as it is (e.g. it's not in Fast mode).</summary>
    None,

    /// <summary>Charge: set the current setpoint to the decision's <c>ChargeCurrentAmps</c>.</summary>
    Charge,

    /// <summary>Pause: drop the current setpoint to the pause value so the car stops drawing.</summary>
    Pause,
}
