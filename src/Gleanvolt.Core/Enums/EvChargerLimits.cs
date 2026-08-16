namespace Gleanvolt.Core.Enums;

/// <summary>
/// Absolute current limits the SolaX charger hardware accepts, per the X1/X3-HAC protocol
/// (charge-current registers are documented as 6-32 A; 6 A is also the IEC 61851 EVSE minimum).
///
/// These bound everything: the controller clamps its configured min/max into this range so it can
/// never even target an illegal setpoint, and the write path clamps again as a final guard.
/// </summary>
public static class EvChargerLimits
{
    public const int MinCurrentAmps = 6;

    public const int MaxCurrentAmps = 32;
}
