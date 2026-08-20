namespace Gleanvolt.Core.Enums;

/// <summary>
/// What a <see cref="Models.TargetedChargePlan"/> has concluded it must do to put the requested
/// energy in the car by the requested time. The four cases fall out of one comparison: what is still
/// needed against the physical ceiling of the charger running flat out for every remaining minute.
/// </summary>
public enum TargetedChargeStrategy
{
    /// <summary>The target has already been delivered; there is nothing left to do.</summary>
    Complete,

    /// <summary>
    /// Forecast surplus alone covers what is still needed, so no grid import is planned at all. The
    /// car takes the earliest chargeable sun and stops when the target is met.
    /// </summary>
    Solar,

    /// <summary>
    /// The sun covers part of it and the grid covers the rest, in a block placed as <b>late</b> as it
    /// can possibly be. Because the plan is rebuilt every poll, an afternoon that turns out sunnier
    /// than forecast shrinks that block — often to nothing — before a single watt of it is drawn.
    /// </summary>
    SolarPlusGrid,

    /// <summary>
    /// There is not enough time left: even flat out from now until departure the request cannot be
    /// met. The charger takes everything, sun and grid together, and the plan reports how much will
    /// actually arrive and the departure time that would have covered the request.
    /// </summary>
    Maximum,
}
