namespace Solax.Core.Enums;

public static class EvChargerStatusExtensions
{
    /// <summary>
    /// Whether a vehicle is plugged in. Available means the charger is ready with no car; the other
    /// active states (Preparing/Charging/Suspended/ChargePaused/Finishing) mean a car is connected;
    /// fault/unavailable/unknown states mean it isn't usable.
    /// </summary>
    public static bool IsCarConnected(this EvChargerStatus status) => status switch
    {
        EvChargerStatus.Preparing
            or EvChargerStatus.Charging
            or EvChargerStatus.SuspendedEv
            or EvChargerStatus.SuspendedEvse
            or EvChargerStatus.ChargePaused
            or EvChargerStatus.Finishing => true,
        _ => false,
    };

    /// <summary>
    /// Whether the charger reports the session ending on the <em>car's</em> initiative rather than
    /// still delivering: <see cref="EvChargerStatus.SuspendedEv"/> is the EV side stopping the draw
    /// (typically its target SOC), <see cref="EvChargerStatus.Finishing"/> is the session closing.
    ///
    /// <para><see cref="EvChargerStatus.ChargePaused"/> and <see cref="EvChargerStatus.SuspendedEvse"/>
    /// are deliberately excluded: those are the charger's own doing, which is what our pause write
    /// produces — treating them as "the car is done" would let the controller mistake its own pause
    /// for a finished charge.</para>
    /// </summary>
    public static bool IsChargeWindingDown(this EvChargerStatus status) =>
        status is EvChargerStatus.SuspendedEv or EvChargerStatus.Finishing;
}
