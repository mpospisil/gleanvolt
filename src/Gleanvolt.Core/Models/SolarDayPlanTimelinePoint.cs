namespace Gleanvolt.Core.Models;

/// <summary>
/// One sliced forecast period, carrying the same required-SOC-floor projection
/// <see cref="Strategies.SolarDayPlanner"/> computes for "now" — evaluated instead at
/// <see cref="Start"/>, as if that instant were the one the plan was built from. It is the data
/// behind <see cref="SolarDayPlan.Timeline"/> (issue #50's plan timeline): the same formula, just
/// read at more than one point in the day rather than only the current one.
/// </summary>
/// <param name="Start">The (inclusive) start of the period.</param>
/// <param name="End">The (exclusive) end of the period.</param>
/// <param name="SurplusWatts">Forecast PV surplus over the period, at the plan's confidence band. May be negative.</param>
/// <param name="IsPlateau">Whether the surplus clears the charger's minimum power — the only periods the car can use.</param>
/// <param name="RequiredSocFloorPercent">
/// What <see cref="SolarDayPlan.RequiredSocFloorPercent"/> would read if <see cref="Start"/> were the
/// instant the plan was built from: the SOC the battery must not be below at that point for the sun
/// still to come after it to return the pack to 100% by the deadline.
/// </param>
public sealed record SolarDayPlanTimelinePoint(
    DateTimeOffset Start,
    DateTimeOffset End,
    double SurplusWatts,
    bool IsPlateau,
    double RequiredSocFloorPercent);
