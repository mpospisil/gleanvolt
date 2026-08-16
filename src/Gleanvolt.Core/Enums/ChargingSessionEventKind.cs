namespace Gleanvolt.Core.Enums;

/// <summary>
/// The sparse, human-readable things that happen inside a session. Samples say what the numbers were;
/// events say what changed and why, which is what makes a session chart legible rather than a set of
/// unexplained steps.
/// </summary>
public enum ChargingSessionEventKind
{
    /// <summary>The session opened.</summary>
    SessionStarted,

    /// <summary>The charge-control mode changed without the session ending (e.g. Forecasted → FastNoBattery).</summary>
    ModeChanged,

    /// <summary>Control state moved to <see cref="ChargeControlState.Charging"/>.</summary>
    ChargingStarted,

    /// <summary>Control state moved to <see cref="ChargeControlState.Paused"/>.</summary>
    ChargingPaused,

    /// <summary>The commanded current setpoint changed.</summary>
    TargetCurrentChanged,

    /// <summary>The battery discharge hold was armed.</summary>
    BatteryHoldArmed,

    /// <summary>The battery discharge hold was released.</summary>
    BatteryHoldReleased,

    /// <summary>The forecast-driven day plan became unusable (no forecast, stale, or out of the trust band).</summary>
    PlanUnusable,

    /// <summary>A usable day plan became available again.</summary>
    PlanUsable,

    /// <summary>The session closed.</summary>
    SessionEnded,
}
