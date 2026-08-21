namespace Gleanvolt.Core.Enums;

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
    /// Whether the charger told us anything at all. <see cref="EvChargerStatus.Unknown"/> is what a
    /// <b>failed read</b> produces — the charger stopped answering Modbus, or answered with a value
    /// outside the register's range (see <c>EnergyStateReader</c> and <c>EvChargerStatusMapping</c>).
    /// It is the absence of information, not a fact about the car.
    /// </summary>
    public static bool IsConnectionKnown(this EvChargerStatus status) => status != EvChargerStatus.Unknown;

    /// <summary>
    /// Whether the charger <b>positively reports</b> that no car is plugged in. The distinction from
    /// <c>!IsCarConnected()</c> is the whole point: that expression is also true for
    /// <see cref="EvChargerStatus.Unknown"/>, so a single dropped Modbus read reads as an unplugged
    /// car — which is exactly what happened in production on 2026-08-21, ending a running session and
    /// returning the mode to Off one poll after the charger blinked. Anything that <em>ends</em>
    /// something must ask this question, never the negation of the other one.
    /// </summary>
    public static bool IsCarKnownDisconnected(this EvChargerStatus status) =>
        status.IsConnectionKnown() && !status.IsCarConnected();

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
