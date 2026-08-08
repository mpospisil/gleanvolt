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

    /// <summary>
    /// As <see cref="Solar"/>, but the fixed battery-full gate is replaced by a forecast-driven day
    /// plan: the Solcast forecast decides how much of today's remaining sun the car may have, so the
    /// home battery still reaches 100% by the configured evening deadline. Sub-minimum ("shoulder")
    /// power is left to the house and battery, the midday plateau is released to the car, and the
    /// battery may lend power briefly when the forecast can repay it. Falls back to
    /// <see cref="Solar"/> behaviour whenever no usable forecast is available.
    /// </summary>
    Forecasted,

    /// <summary>
    /// Charge the car as fast as the installation allows, and keep the home battery out of it: the
    /// current setpoint is pinned at the configured maximum whatever the sun is doing (PV covers what
    /// it can, the grid covers the rest) and the battery discharge hold is armed for as long as the
    /// mode is selected. When the car stops drawing because it has reached its own charge limit, the
    /// setpoint drops to the pause current and the mode returns itself to <see cref="Off"/>, which
    /// releases the hold. Like the other modes it only acts while the charger's own use-mode is Fast.
    /// </summary>
    FastNoBattery,
}
