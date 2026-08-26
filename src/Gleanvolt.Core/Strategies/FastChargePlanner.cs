using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Works out when a deferred fast charge has to start. Pure, and small enough to read in one sitting,
/// which is the point: <c>remaining ÷ power</c>, subtracted from the departure.
///
/// <para><b>What it deliberately does not do.</b> No forecast, no surplus, no house load, no battery,
/// no pacing, no blocks. <see cref="TargetedChargePlanner"/> exists for all of that and is two orders
/// of magnitude larger for the reason. The moment this file needs any of them, the feature has stopped
/// being a fast charge with a start time on it.</para>
/// </summary>
public static class FastChargePlanner
{
    /// <summary>
    /// The plan as it stands, or null when there is nothing to plan — no departure was asked for, so
    /// the charge starts when it is pressed.
    /// </summary>
    /// <param name="limit">What was asked for, and by when.</param>
    /// <param name="deliveredWh">What has been delivered against it so far.</param>
    /// <param name="observedPowerWatts">
    /// What the car has been seen to take on this charge, or null before it has taken anything. Used in
    /// preference to <paramref name="maxPowerWatts"/> whenever it exists: the car's own limit is the
    /// one that decides how long this takes, and it is not knowable in advance.
    /// </param>
    /// <param name="maxPowerWatts">What the installation can deliver — the fallback, and the ceiling.</param>
    /// <param name="safetyMargin">How long before the departure the charge must be finished.</param>
    /// <param name="now">The instant to plan at.</param>
    public static FastChargePlan? Plan(
        FastChargeLimit limit,
        double deliveredWh,
        double? observedPowerWatts,
        double maxPowerWatts,
        TimeSpan safetyMargin,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(limit);

        if (limit.DepartBy is not { } departBy)
        {
            return null;
        }

        // Never above what the installation can deliver: a spurious high reading must not be able to
        // promise a charge faster than the wallbox can physically run.
        var observed = observedPowerWatts is { } seen && seen > 0
            ? Math.Min(seen, maxPowerWatts)
            : (double?)null;

        // Guarded rather than trusted, and the fallback is "no schedule" rather than a nominal power:
        // dividing the remaining energy by something near zero defers the charge to the end of time,
        // which is the one failure here that produces no error and no charge. Without a plan the mode
        // charges immediately, which is the safe direction to be wrong in.
        var power = observed ?? maxPowerWatts;
        if (double.IsNaN(power) || power <= 0)
        {
            return null;
        }

        var remainingWh = Math.Max(0, limit.RequiredEnergyWh - deliveredWh);
        var duration = TimeSpan.FromHours(remainingWh / power);

        // Negative margins are somebody's typo, not an instruction to finish after the departure.
        var readyBy = departBy - (safetyMargin > TimeSpan.Zero ? safetyMargin : TimeSpan.Zero);
        var startNoLaterThan = readyBy - duration;

        // Deliberately not clamped to `now`: a start time in the past is how "there is not enough time
        // left" is expressed, and the shortfall below is computed from the same comparison. Clamping it
        // forward would make a late plan look punctual.
        var available = readyBy - now;
        var deliverableWh = available > TimeSpan.Zero ? power * available.TotalHours : 0;
        var shortfallWh = Math.Max(0, remainingWh - deliverableWh);

        return new FastChargePlan(
            DepartBy: departBy,
            ReadyBy: readyBy,
            StartNoLaterThan: startNoLaterThan,
            Duration: duration,
            RemainingEnergyWh: remainingWh,
            ChargePowerWatts: power,
            PowerObserved: observed is not null,
            ShortfallWh: shortfallWh);
    }
}
