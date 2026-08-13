using Solax.Core.Enums;

namespace Solax.Core.Models;

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
/// <param name="ShortfallWh">
/// How far the forecast falls short of house + battery-to-full + the day's EV target. Positive means
/// the car will not get everything it wanted; the battery keeps priority regardless.
/// </param>
/// <param name="EvExpectedTodayWh">What the car can realistically expect to receive today in total.</param>
/// <param name="EvTargetWh">The day's EV target the shortfall is measured against.</param>
/// <param name="Outlook">The coarse, reportable summary of the four figures above.</param>
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
    double ShortfallWh,
    double EvExpectedTodayWh,
    double EvTargetWh,
    DayOutlook Outlook,
    double BiasFactor,
    DateTimeOffset Deadline,
    DateTimeOffset? ForecastAsOf,
    bool IsUsable,
    string Reason,
    IReadOnlyList<SolarDayPlanTimelinePoint> Timeline)
{
    /// <summary>Whether the day cannot cover the house, the battery and the car's target together.</summary>
    public bool HasShortfall => ShortfallWh > 0;

    /// <summary>
    /// An unusable plan: no forecast, a stale one, or one whose accuracy has broken the trust band.
    /// Everything is zeroed and <see cref="Outlook"/> is <see cref="DayOutlook.Unknown"/>, so a caller
    /// that ignores <see cref="IsUsable"/> gets "no budget" rather than an optimistic guess.
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
        ShortfallWh: 0,
        EvExpectedTodayWh: 0,
        EvTargetWh: 0,
        Outlook: DayOutlook.Unknown,
        BiasFactor: 1,
        Deadline: deadline,
        ForecastAsOf: null,
        Timeline: [],
        IsUsable: false,
        Reason: reason);
}
