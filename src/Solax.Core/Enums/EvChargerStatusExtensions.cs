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
}
