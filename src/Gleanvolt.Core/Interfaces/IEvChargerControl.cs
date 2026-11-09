using Gleanvolt.Devices;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The control loop's view of the charger: reads its settings, writes its charging-current setpoint, and —
/// only when an action asks for it — lets it charge or stops it. A thin adapter over the charger's
/// <see cref="IEvChargerDriver"/> (issue #348), which maps start and stop onto its family's own control.
///
/// <para>The two writes belong to different callers on purpose. The <b>control loop</b> writes the
/// current setpoint and nothing else, exactly as it always has. <b>Start and stop</b> are written once
/// per action: allowed when a strategy is started, stopped when it is switched off. Nothing re-asserts
/// it afterwards, so a charger changed at the wallbox mid-session leaves the controllers idle rather
/// than being fought for.</para>
/// </summary>
public interface IEvChargerControl
{
    /// <summary>Reads the charger's currently active settings (charging allowed, and the current setpoint).</summary>
    Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the charging-current setpoint, but only if it differs from <paramref name="activeAmps"/>
    /// (the freshly-read active value) by at least the configured change threshold — so the charger
    /// isn't re-commanded on every small fluctuation. The change is logged. A value of 0 (or the
    /// configured pause current) pauses; charging values are within the hardware range.
    /// </summary>
    Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets the charger charge, or stops it (SolaX: its use-mode, Fast or Stop). Unconditional — unlike
    /// <see cref="SetCurrentAsync"/> there is no hysteresis and no read-back comparison, because "stop
    /// charging" has to stop charging whatever the charger already said. Throws if the charger does not
    /// accept the write; the caller reports that rather than pretending the state was entered.
    ///
    /// <para><b>Callers must write on a transition, not on every poll.</b> This was once reached only
    /// from an action the owner had just taken, which made that free; the control loop now calls it too,
    /// to stand a charger down for a deferred charge's wait
    /// (<see cref="Enums.ChargingControlAction.StandDown"/>) and to arm it again at the appointment.
    /// With no read-back to make a repeat cheap, re-writing the same state each cycle would be steady
    /// traffic on a link that is not always healthy.</para>
    /// </summary>
    Task SetChargingAllowedAsync(bool allowed, string reason, CancellationToken cancellationToken = default);
}
