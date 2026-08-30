namespace Gleanvolt.Core.Enums;

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

    /// <summary>
    /// Stand the charger down entirely -- write its use-mode to <see cref="EvChargerMode.Stop"/> rather
    /// than holding it at the pause current.
    ///
    /// <para>For waits measured in hours, not the seconds-to-minutes <see cref="Pause"/> is for. A SolaX
    /// HAC will not sit in Fast at 0A indefinitely: it decides the session is finished, reports
    /// <c>Finishing</c>, and reverts its own use-mode to Stop -- observed after about seven minutes on
    /// 2026-08-28, which left a deferred overnight charge inert for 8.5 hours. Stop is the state the
    /// wallbox actually has for "not charging now", so a long wait is expressed in its own vocabulary
    /// instead of parked in one it treats as an anomaly.</para>
    /// </summary>
    StandDown,
}
