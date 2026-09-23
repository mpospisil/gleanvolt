namespace Gleanvolt.Core.Models;

/// <summary>
/// What the rest of today looks like, computed fresh every poll from the solar forecast and live
/// telemetry (see <see cref="Strategies.SolarDayPlanner"/>). Pure data: the controller decides from
/// it, the worker logs it, and Home Assistant reports it.
///
/// <para>All energies are watt-hours <b>from now until <see cref="Deadline"/></b>, at the confidence
/// band the planner was configured with, already scaled by <see cref="BiasFactor"/>.</para>
/// </summary>
/// <param name="RemainingPvWh">Forecast PV production still to come today.</param>
/// <param name="ShoulderEnergyWh">
/// Of that, the part arriving at a surplus <b>below</b> the charger's minimum power — morning and
/// late-afternoon production the car physically cannot use. The home battery is its only consumer.
/// </param>
/// <param name="PlateauEnergyWh">
/// The part arriving at a surplus at or above the charger's minimum power — the only energy the car
/// can take, and the only window in which it can charge at all.
/// </param>
/// <param name="PlateauClaimedByBatteryWh">
/// How much of the plateau the battery had to claim because the shoulders alone couldn't fill it.
/// Zero on a good day, which is what releases the whole plateau to the car.
/// </param>
/// <param name="ExpectedHouseWh">Expected household consumption over the same window (EV excluded).</param>
/// <param name="BatteryToFullWh">Energy needed to bring the home battery to 100%, charge losses included.</param>
/// <param name="EvBudgetWh">
/// The energy-only budget: what is left of the forecast once the house and the battery are served.
/// It says nothing about whether the car can physically absorb it — see <see cref="FeasibleEvEnergyWh"/>.
/// </param>
/// <param name="FeasibleEvEnergyWh">
/// The <b>deliverable</b> budget: <see cref="EvBudgetWh"/> restricted to periods where the surplus
/// actually clears the charger's minimum power. This is the number that matters, because the car
/// cannot sip a budget slowly.
/// </param>
/// <param name="NextFeasibleWindow">
/// The next contiguous stretch (start, end) in which charging is possible and sustained for at least
/// the configured minimum viable window, or null when today offers none.
/// </param>
/// <param name="RequiredSocFloorPercent">
/// The SOC the battery must not fall below <em>right now</em> if the energy still coming its way is to
/// return it to 100% by <see cref="Deadline"/>. Rises towards 100% as the day runs out, which is what
/// squeezes the car out of the late afternoon without any scheduling code.
/// </param>
/// <param name="TrajectorySocFloorPercent">
/// The same figure <em>before</em> the configured hard clamp is applied — what the forecast alone
/// says the battery may fall to. Below <see cref="RequiredSocFloorPercent"/> exactly when the clamp
/// is the binding constraint, which is the normal case on a sunny morning: the sun can still recover
/// a much deeper discharge, and the 50% floor is the owner's preference rather than the physics. The
/// controller treats a breach of the two differently — see
/// <see cref="Strategies.ForecastedChargingController"/>.
/// </param>
/// <param name="SpillWh">
/// Remaining surplus the home battery has <b>no room for</b>: the sum of every slice's surplus less
/// <see cref="BatteryToFullWh"/>, floored at zero. On a pack already at 100% that is the whole of the
/// remaining surplus, and it is the honest statement of the situation that makes a battery loan free
/// — the energy is leaving the house whatever happens, so lending against it buys back watts that
/// were going to be exported rather than spending watts the pack would have kept (issue #223).
/// </param>
/// <param name="LoanableWh">
/// How much energy sits between the current SOC and <see cref="RequiredSocFloorPercent"/> — what the
/// pack may lend and still be repaid to 100% by <see cref="Deadline"/>, by the definition of that
/// floor. The controller's own margin comes off this before it lends.
/// </param>
/// <param name="ShortfallWh">
/// How far the forecast falls short of house + battery-to-full. Positive means the evening 100% is at
/// risk; the battery keeps priority over the car regardless.
/// </param>
/// <param name="BiasFactor">
/// The realised forecast bias applied to the remaining forecast (actual ÷ forecast so far today,
/// clamped). 1.0 when there aren't enough samples yet to judge.
/// </param>
/// <param name="Deadline">The instant the battery is required to be at 100%.</param>
/// <param name="ForecastAsOf">When the underlying forecast was fetched.</param>
/// <param name="IsUsable">
/// False when there is no forecast, it is stale, or its accuracy has broken the trust band. Callers
/// must fall back to live-solar behaviour rather than trusting the numbers in this record.
/// </param>
/// <param name="Reason">A short human-readable summary for logging and Home Assistant.</param>
/// <param name="Timeline">
/// Today's remaining forecast, one <see cref="SolarDayPlanTimelinePoint"/> per sliced period,
/// chronological. Exists for the web UI's plan timeline (issue #50) — nothing in charge control
/// reads it. Empty when the plan isn't usable.
/// </param>
public sealed record SolarDayPlan(
    double RemainingPvWh,
    double ShoulderEnergyWh,
    double PlateauEnergyWh,
    double PlateauClaimedByBatteryWh,
    double ExpectedHouseWh,
    double BatteryToFullWh,
    double EvBudgetWh,
    double FeasibleEvEnergyWh,
    (DateTimeOffset Start, DateTimeOffset End)? NextFeasibleWindow,
    double RequiredSocFloorPercent,
    double TrajectorySocFloorPercent,
    double SpillWh,
    double LoanableWh,
    double ShortfallWh,
    double BiasFactor,
    DateTimeOffset Deadline,
    DateTimeOffset? ForecastAsOf,
    bool IsUsable,
    string Reason,
    IReadOnlyList<SolarDayPlanTimelinePoint> Timeline)
{
    /// <summary>
    /// Whether the day cannot cover the house and the battery to 100% together. A reading for the log
    /// and the dashboard, and no longer a gate on anything: it used to veto the battery loan, which on a
    /// full pack vetoed exactly the case the loan exists for (issue #223). What stops a session on a day
    /// that cannot refill the pack is <see cref="TrajectorySocFloorPercent"/> rising above the SOC, which
    /// is the same fact stated where it can be acted on.
    /// </summary>
    public bool HasShortfall => ShortfallWh > 0;

    /// <summary>
    /// Whether the rest of today makes more surplus than the pack can absorb. The question the battery
    /// loan actually turns on: not "is this surplus real?" but "has this energy anywhere else to go?".
    /// </summary>
    public bool WillSpill => SpillWh > 0;

    /// <summary>
    /// What the pack may lend right now for free: the smaller of what it may lend at all and what it
    /// could not have kept anyway. Zero either when SOC is already on the floor or when every
    /// remaining watt has a home in the pack — reported so "why is nothing being lent?" is one figure
    /// rather than a code read.
    /// </summary>
    public double LoanHeadroomWh => Math.Max(0, Math.Min(LoanableWh, SpillWh));

    /// <summary>
    /// Whether the floor in force is the owner's configured clamp rather than the forecast's own
    /// trajectory. True through most of a sunny day, and the reason a dip under the floor in the
    /// morning is not the same event as one at four in the afternoon.
    /// </summary>
    public bool FloorIsClamped => RequiredSocFloorPercent > TrajectorySocFloorPercent;

    /// <summary>
    /// An unusable plan: no forecast, a stale one, or one whose accuracy has broken the trust band.
    /// Everything is zeroed, so a caller that ignores <see cref="IsUsable"/> gets "no budget" rather
    /// than an optimistic guess.
    /// </summary>
    public static SolarDayPlan Unavailable(DateTimeOffset deadline, string reason) => new(
        RemainingPvWh: 0,
        ShoulderEnergyWh: 0,
        PlateauEnergyWh: 0,
        PlateauClaimedByBatteryWh: 0,
        ExpectedHouseWh: 0,
        BatteryToFullWh: 0,
        EvBudgetWh: 0,
        FeasibleEvEnergyWh: 0,
        NextFeasibleWindow: null,
        RequiredSocFloorPercent: 100,
        TrajectorySocFloorPercent: 100,
        SpillWh: 0,
        LoanableWh: 0,
        ShortfallWh: 0,
        BiasFactor: 1,
        Deadline: deadline,
        ForecastAsOf: null,
        Timeline: [],
        IsUsable: false,
        Reason: reason);
}
