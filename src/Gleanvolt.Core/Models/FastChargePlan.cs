namespace Gleanvolt.Core.Models;

/// <summary>
/// When a deferred fast charge has to start, so it finishes just before the owner leaves. The whole of
/// what <see cref="Enums.ChargeControlMode.FastNoBattery"/> plans, and deliberately almost nothing:
/// one division, and the clock.
///
/// <para><b>Not a <see cref="TargetedChargePlan"/>.</b> There are no blocks, no forecast, no split
/// between sun and grid, and no pacing. Everything the targeted planner exists to do is absent by
/// design: this mode charges flat out, and the only question it answers is <em>when to begin</em>. If
/// this record ever grows a second block, the feature has become the targeted mode and should stop.</para>
///
/// <para>Rebuilt on every poll rather than fixed at activation — see
/// <see cref="ChargePowerWatts"/> for the half of that which matters.</para>
///
/// <para>Its absence means "charge now", and means it in both directions: no departure was asked for,
/// or no honest schedule could be made from one. Deferring is the behaviour that can leave a car flat
/// at 07:00, so anything uncertain resolves to charging rather than to waiting.</para>
/// </summary>
/// <param name="DepartBy">When the owner leaves, as they stated it.</param>
/// <param name="ReadyBy">
/// When the charge must be finished: <paramref name="DepartBy"/> less the safety margin. "Ready at
/// 07:00" must not mean "still charging at 07:00" — somebody has to be able to unplug and go.
/// </param>
/// <param name="StartNoLaterThan">
/// The latest instant charging can begin and still reach <paramref name="ReadyBy"/>. Before it the
/// controller pauses; at or after it, the mode is the ordinary fast charge. Never later than
/// <paramref name="ReadyBy"/>, and pushed into the past rather than the future when there is already
/// too little time — "you should have started twenty minutes ago" is a true statement and a useful one.
/// </param>
/// <param name="Duration">How long <see cref="RemainingEnergyWh"/> needs at <paramref name="ChargePowerWatts"/>.</param>
/// <param name="RemainingEnergyWh">What is still to deliver — the figure the duration is computed from.</param>
/// <param name="ChargePowerWatts">
/// The power the plan is computed at, and the field most likely to be wrong in a way that matters.
///
/// <para>Before the car has drawn anything this is the <b>installation's</b> maximum, because nothing
/// better is knowable. Once it draws, it is what the car is <b>actually taking</b>: an 11 kW wallbox
/// in front of a car with a 7.4 kW on-board charger is otherwise a plan half an hour short of the time
/// it needs, and this mode is the one with no slack in it — arriving "just in time" is the whole
/// point. <paramref name="PowerObserved"/> says which of the two this is.</para>
/// </param>
/// <param name="PowerObserved">
/// Whether <paramref name="ChargePowerWatts"/> came from the car (true) or from configuration (false).
/// False means the plan is a well-founded guess, and it is worth saying so out loud rather than
/// presenting an estimate as a measurement.
/// </param>
/// <param name="ShortfallWh">
/// How much of the request will not fit in the time left, at this power. Zero whenever the plan is
/// feasible, which is the ordinary case. Non-zero is not an error and never stops the charge — the
/// charger runs flat out from now and delivers what it can — it is a promise the controller declines
/// to make.
/// </param>
public sealed record FastChargePlan(
    DateTimeOffset DepartBy,
    DateTimeOffset ReadyBy,
    DateTimeOffset StartNoLaterThan,
    TimeSpan Duration,
    double RemainingEnergyWh,
    double ChargePowerWatts,
    bool PowerObserved,
    double ShortfallWh)
{
    /// <summary>Whether the charge should be held back at <paramref name="now"/>.</summary>
    public bool IsWaitingAt(DateTimeOffset now) => now < StartNoLaterThan;

    /// <summary>Whether the departure has been and gone.</summary>
    public bool HasDepartedAt(DateTimeOffset now) => now >= DepartBy;

    /// <summary>Whether the request fits in the time left.</summary>
    public bool IsFeasible => ShortfallWh <= 0;
}
