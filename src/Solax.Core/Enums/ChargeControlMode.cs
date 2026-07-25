namespace Solax.Core.Enums;

/// <summary>The charge-control mode, selectable at runtime (e.g. from Home Assistant).</summary>
public enum ChargeControlMode
{
    /// <summary>Don't control the charger; leave its current setpoint exactly as it is.</summary>
    Off,

    /// <summary>
    /// Modulate the charging current from live solar surplus while the battery is full: set the
    /// current the sun can cover, or pause when there isn't enough. Only acts while the charger's own
    /// use-mode is Fast.
    /// </summary>
    Solar,
}
