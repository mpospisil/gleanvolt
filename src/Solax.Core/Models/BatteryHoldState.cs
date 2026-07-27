namespace Solax.Core.Models;

/// <summary>
/// What the battery discharge hold believes the inverter is doing, after a reconciliation cycle.
/// Reported state, not a device read-back — the command register cannot be read back (see
/// <see cref="Interfaces.IBatteryDischargeControl"/>).
/// </summary>
/// <param name="Held">Whether a hold command is currently armed.</param>
/// <param name="ActivePowerTargetWatts">The target last written, or null when not held.</param>
/// <param name="ArmedAt">When the currently-armed command was written, or null when not held.</param>
/// <param name="Wrote">Whether this cycle issued a write (true on arm, release, retarget and renewal).</param>
public readonly record struct BatteryHoldState(
    bool Held,
    double? ActivePowerTargetWatts,
    DateTimeOffset? ArmedAt,
    bool Wrote);
