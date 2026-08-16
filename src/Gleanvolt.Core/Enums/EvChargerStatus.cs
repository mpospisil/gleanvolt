namespace Gleanvolt.Core.Enums;

// Values match the SolaX EV charger's RunMode/EVSE_State register (0-13) exactly,
// so mapping is a direct cast rather than a lookup table.
public enum EvChargerStatus
{
    Unknown = -1,
    Available = 0,
    Preparing = 1,
    Charging = 2,
    Finishing = 3,
    Faulted = 4,
    Unavailable = 5,
    Reserved = 6,
    SuspendedEv = 7,
    SuspendedEvse = 8,
    Update = 9,
    CardActivation = 10,
    StartDelay = 11,
    ChargePaused = 12,
    Stopping = 13,
}

public static class EvChargerStatusMapping
{
    public static EvChargerStatus FromRaw(ushort raw) =>
        raw <= (ushort)EvChargerStatus.Stopping ? (EvChargerStatus)raw : EvChargerStatus.Unknown;
}
