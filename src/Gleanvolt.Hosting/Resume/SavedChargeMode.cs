using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Resume;

/// <summary>
/// The mode that was running and what it needs to be taken up again after a restart (#413): what
/// <see cref="ChargeModeFile"/> keeps on disk and <see cref="ChargeModeResume"/> decides on.
/// </summary>
/// <param name="Mode">The mode the poll loop was running.</param>
/// <param name="SavedAt">
/// When this was last written. Refreshed every few minutes while a mode runs and once more on a clean
/// shutdown, so it is also the last moment Gleanvolt is known to have been up.
/// </param>
/// <param name="UntilFull">Forecasted only: whether the session runs until the car is full (#279).</param>
/// <param name="FastLimit">FastNoBattery only: the limit, or null for a charge to full.</param>
/// <param name="FastDeliveredWh">FastNoBattery only: what the charger had delivered against the limit.</param>
/// <param name="TargetedRequest">Targeted only: the target.</param>
/// <param name="TargetedDeliveredWh">Targeted only: what the charger had delivered against it.</param>
public sealed record SavedChargeMode(
    ChargeControlMode Mode,
    DateTimeOffset SavedAt,
    bool UntilFull = false,
    FastChargeLimit? FastLimit = null,
    double FastDeliveredWh = 0,
    TargetedChargeRequest? TargetedRequest = null,
    double TargetedDeliveredWh = 0)
{
    /// <summary>
    /// Whether <paramref name="other"/> is a different mode or different parameters: a change worth
    /// writing at once. The delivered energy and the time are not part of it; they move every poll.
    /// </summary>
    public bool DiffersFrom(SavedChargeMode? other) =>
        other is null
        || Mode != other.Mode
        || UntilFull != other.UntilFull
        || FastLimit != other.FastLimit
        || !SameRequest(TargetedRequest, other.TargetedRequest);

    // The record's own equality compares the constraints' window list by reference, which a request
    // read back from disk never shares with the one in memory.
    private static bool SameRequest(TargetedChargeRequest? a, TargetedChargeRequest? b) =>
        a is null || b is null
            ? a is null && b is null
            : a with { Constraints = null } == b with { Constraints = null }
              && SameConstraints(a.Constraints, b.Constraints);

    private static bool SameConstraints(TargetedChargeConstraints? a, TargetedChargeConstraints? b) =>
        a is null || b is null
            ? a is null && b is null
            : a with { ForbiddenWindows = null } == b with { ForbiddenWindows = null }
              && (a.ForbiddenWindows ?? []).SequenceEqual(b.ForbiddenWindows ?? []);
}
