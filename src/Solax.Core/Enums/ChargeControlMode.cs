namespace Solax.Core.Enums;

/// <summary>The charge-control mode, selectable at runtime (e.g. from Home Assistant).</summary>
public enum ChargeControlMode
{
    /// <summary>Don't touch the charger; leave it entirely to the owner.</summary>
    Off,

    /// <summary>Automatic solar charging: charge on live surplus while the battery is full.</summary>
    Solar,

    /// <summary>
    /// Manual override: charge now at the maximum allowed current, ignoring the surplus and the
    /// battery-SOC gate. This may pull from the grid or discharge the battery.
    /// </summary>
    Force,
}
