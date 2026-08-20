namespace Gleanvolt.Core.Enums;

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

    /// <summary>
    /// Deliver a stated amount of energy by a stated departure time, with as little grid as possible:
    /// the car takes every kilowatt-hour of forecast surplus the home battery has not already claimed,
    /// and the grid covers only the remainder — in a block placed as <b>late</b> as it can be and still
    /// finish in time, so a sunnier afternoon than forecast shrinks it before any of it is bought. When
    /// there is not enough time left for the request, it charges flat out and reports how far short it
    /// will fall rather than pretending otherwise. The home battery keeps its priority throughout, and
    /// the discharge hold arms while the grid block runs so the pack never feeds the car. The request
    /// does not survive a restart. Like the other modes it only acts while the charger's own use-mode
    /// is Fast.
    /// </summary>
    Targeted,
}
