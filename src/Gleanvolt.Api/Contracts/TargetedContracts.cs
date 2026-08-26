using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Contracts;

/// <summary>
/// What to put in the car, and by when. The input to both the preview and a targeted start — the same
/// shape either way, so what you quote is what you commit to.
///
/// <para>Say it either as energy at the charger or as a state of charge for the car, not
/// both. A state of charge needs a vehicle feed with a reading and a configured pack size
/// (<c>Vehicle:BatteryCapacityKWh</c>); without them, ask in kilowatt-hours. The conversion happens
/// once, when the request is made, and is never re-derived from a later reading.</para>
/// </summary>
/// <param name="EnergyKWh">
/// The energy to deliver, measured at the charger — not the energy that reaches the cells. Omit when
/// asking in state of charge.
/// </param>
/// <param name="TargetSocPercent">
/// The state of charge to reach, 0-100. Converted to energy from what the car last reported. Omit when
/// asking in kilowatt-hours.
/// </param>
/// <param name="DepartBy">
/// When the energy has to be in the car. An ISO-8601 timestamp with an offset: this is a
/// local-time promise, and a bare instant is one of the two ways to be an hour wrong.
/// </param>
/// <param name="Priority">
/// What to optimise while delivering it. <c>cheapest</c> — the default — paces the charge across the
/// whole window and takes every watt of sun above that pace, so the target is usually met well before
/// the deadline. <c>justInTime</c> holds the last stretch back so the car reaches its target shortly
/// before departure instead of sitting full all night; it may cost grid, and the preview says how much.
/// </param>
/// <param name="RestSocPercent">
/// Where a <c>justInTime</c> hold parks the car before the last stretch is released. Defaults to the
/// installation's configured rest level. Means nothing under <c>cheapest</c>.
/// </param>
/// <param name="Editable">
/// Limits on <em>how</em> the request may be met (issue #128): when the charger may run, and how much
/// may be bought. Omit for none, which is what every request that says nothing gets.
///
/// <para>This is the field a quoted plan's <c>editable</c> goes back into — edit a bound, send the
/// whole thing back, and quote it again or commit to it. The same body does both, which is what makes
/// "what you quote is what you commit to" true of the limits as well as of the target.</para>
/// </param>
public sealed record TargetedChargeRequestBody(
    double? EnergyKWh,
    double? TargetSocPercent,
    DateTimeOffset DepartBy,
    TargetedChargePriority Priority = TargetedChargePriority.Cheapest,
    double? RestSocPercent = null,
    EditablePlanBody? Editable = null);

/// <summary>The request the controller is working to, described in the terms it was asked in.</summary>
/// <param name="RequiredEnergyWh">The energy asked for, at the charger.</param>
/// <param name="DepartBy">When it has to be there.</param>
/// <param name="ActivatedAt">When the request was made. Delivery is metered from here, not from when the car plugged in.</param>
/// <param name="TargetSocPercent">The state of charge asked for, when it was asked that way round.</param>
/// <param name="VehicleSocPercentAtRequest">What the car was reporting when the conversion was made.</param>
/// <param name="Priority">What is being optimised.</param>
/// <param name="TailEnergyWh">How much of the request is the held tail. Zero under <c>cheapest</c>.</param>
/// <param name="RestSocPercent">The state of charge the tail was measured down to, when one was.</param>
public sealed record TargetedRequestResponse(
    double RequiredEnergyWh,
    DateTimeOffset DepartBy,
    DateTimeOffset ActivatedAt,
    double? TargetSocPercent,
    double? VehicleSocPercentAtRequest,
    TargetedChargePriority Priority,
    double TailEnergyWh,
    double? RestSocPercent)
{
    internal static TargetedRequestResponse From(TargetedChargeRequest request) => new(
        request.RequiredEnergyWh,
        request.DepartBy,
        request.ActivatedAt,
        request.TargetSocPercent,
        request.VehicleSocPercentAtRequest,
        request.Priority,
        request.TailEnergyWh,
        request.RestSocPercent);
}

/// <summary>
/// How the requested energy is going to reach the car by the requested time. Rebuilt from a refreshed
/// forecast and the measured delivery on every poll, so a sunnier afternoon than forecast shrinks the
/// grid block — often to nothing.
/// </summary>
/// <param name="Strategy">
/// Which case this is: <c>complete</c> (delivered), <c>solar</c> (forecast surplus covers what is
/// left), <c>solarPlusGrid</c> (an import block over the sunniest hours covers the rest), or
/// <c>maximum</c> (not enough time — see <c>shortfallWh</c> and <c>feasibleDeparture</c>).
/// </param>
/// <param name="Now">The telemetry reading this plan is anchored to.</param>
/// <param name="DepartBy">The departure that was asked for.</param>
/// <param name="Deadline">The finish line the plan works to: the departure less the safety margin.</param>
/// <param name="RequiredEnergyWh">The energy asked for.</param>
/// <param name="DeliveredEnergyWh">What the charger has measurably delivered since the request was activated. Zero in a preview.</param>
/// <param name="RemainingEnergyWh">What is still needed.</param>
/// <param name="SolarEnergyWh">
/// Of what is still needed, the part expected to arrive as a solar block — the car charging on the sun
/// alone. Zero does not mean "no sun": see <c>forecastSurplusWh</c>.
/// </param>
/// <param name="ForecastSurplusWh">
/// All the surplus the window is forecast to hold, whether or not the car can charge on it unaided.
/// A gap between this and <c>solarEnergyWh</c> is the weak-sun case: real sun, in half-hours that never
/// clear the charger's floor, which is exactly where the import is then placed so the roof pays part of it.
/// </param>
/// <param name="RequiredPaceWatts">
/// The average power the charger must hold from now to the deadline to keep the promise. A floor on the
/// rate, not a setpoint: sun above it is taken in full, and lowers the pace for every minute after.
/// </param>
/// <param name="GridEnergyWh">The part planned to come from the grid. An upper bound where the import runs over sub-floor surplus.</param>
/// <param name="CeilingEnergyWh">The physical ceiling: the charger at maximum power for every remaining minute.</param>
/// <param name="ExpectedEnergyWh">What will actually reach the car by the deadline.</param>
/// <param name="ShortfallWh">How far that falls short of what was asked. Zero unless the strategy is <c>maximum</c>.</param>
/// <param name="GridStart">When the earliest grid block begins, or null when none is planned.</param>
/// <param name="FeasibleDeparture">The departure time that would have covered the request, when this one cannot.</param>
/// <param name="SocFloorPercent">The SOC the home battery must not be drawn below while the car takes solar.</param>
/// <param name="BatteryToFullWh">What the home battery still needs to reach 100%. Reserved before the car sees any forecast surplus.</param>
/// <param name="TailEnergyWh">The part being held back to land at the deadline under <c>justInTime</c>. Zero otherwise.</param>
/// <param name="HoldUntil">When the held tail is released, or null when nothing is being held.</param>
/// <param name="ForecastAsOf">When the forecast behind this plan was fetched, or null when there was none.</param>
/// <param name="IsUsable">
/// Whether a usable forecast went into it. False does not invalidate the plan: this mode degrades
/// towards keeping its promise — every solar term goes to zero, the plan becomes grid-only, and the
/// target is still met. It is a caveat to report, not a failure.
/// </param>
/// <param name="Reason">A short human-readable summary of what the plan concluded.</param>
/// <param name="Blocks">The plan as a timeline, chronological by start. Solar and grid blocks may overlap.</param>
public sealed record TargetedPlanResponse(
    TargetedChargeStrategy Strategy,
    DateTimeOffset Now,
    DateTimeOffset DepartBy,
    DateTimeOffset Deadline,
    double RequiredEnergyWh,
    double DeliveredEnergyWh,
    double RemainingEnergyWh,
    double SolarEnergyWh,
    double ForecastSurplusWh,
    double RequiredPaceWatts,
    double GridEnergyWh,
    double CeilingEnergyWh,
    double ExpectedEnergyWh,
    double ShortfallWh,
    DateTimeOffset? GridStart,
    DateTimeOffset? FeasibleDeparture,
    double SocFloorPercent,
    double BatteryToFullWh,
    double TailEnergyWh,
    DateTimeOffset? HoldUntil,
    DateTimeOffset? ForecastAsOf,
    bool IsUsable,
    string Reason,
    IReadOnlyList<TargetedBlockResponse> Blocks)
{
    internal static TargetedPlanResponse From(TargetedChargePlan plan) => new(
        plan.Strategy,
        plan.Now,
        plan.DepartBy,
        plan.Deadline,
        plan.RequiredEnergyWh,
        plan.DeliveredEnergyWh,
        plan.RemainingEnergyWh,
        plan.SolarEnergyWh,
        plan.ForecastSurplusWh,
        plan.RequiredPaceWatts,
        plan.GridEnergyWh,
        plan.CeilingEnergyWh,
        plan.ExpectedEnergyWh,
        plan.ShortfallWh,
        plan.GridStart,
        plan.FeasibleDeparture,
        plan.SocFloorPercent,
        plan.BatteryToFullWh,
        plan.TailEnergyWh,
        plan.HoldUntil,
        plan.ForecastAsOf,
        plan.IsUsable,
        plan.Reason,
        [.. plan.Blocks.Select(TargetedBlockResponse.From)]);
}

/// <summary>One stretch of a targeted plan: when the car charges, on what, and how much it gets.</summary>
/// <param name="Start">When the block begins.</param>
/// <param name="End">When it ends.</param>
/// <param name="Source">Where the energy comes from: forecast surplus, or the grid.</param>
/// <param name="PowerWatts">The power the block is planned at.</param>
/// <param name="EnergyWh">The energy it is expected to deliver.</param>
public sealed record TargetedBlockResponse(
    DateTimeOffset Start,
    DateTimeOffset End,
    TargetedChargeSource Source,
    double PowerWatts,
    double EnergyWh)
{
    internal static TargetedBlockResponse From(TargetedChargeBlock block) =>
        new(block.Start, block.End, block.Source, block.PowerWatts, block.EnergyWh);
}

/// <summary>
/// A plan for a request nobody has made: what would happen if this were started now, built from the
/// same telemetry and the same forecast the poll loop is working from, having written to nothing.
/// </summary>
/// <param name="Request">The request as it would be recorded, including the conversions made composing it.</param>
/// <param name="Plan">The plan itself.</param>
/// <param name="CheapestPlan">
/// The same energy by the same time under <c>cheapest</c>, present only when the request actually holds
/// a tail back. The difference in <c>gridEnergyWh</c> between the two is what just-in-time costs.
/// </param>
/// <param name="Editable">
/// The parts of this plan you may change and send back, in the shape the start endpoint takes. Round
/// -tripping it unchanged is a no-op — you get the plan you were quoted.
/// </param>
public sealed record TargetedPreviewResponse(
    TargetedRequestResponse Request,
    TargetedPlanResponse Plan,
    TargetedPlanResponse? CheapestPlan,
    EditablePlanBody Editable);

/// <summary>
/// What a caller may change about a quoted plan (issue #128), and the whole of it.
///
/// <para><b>Limits, not a schedule</b> — and the distinction is the feature, not a simplification of
/// it. The plan is rebuilt on every poll from a refreshed forecast and the measured delivery, which is
/// what lets a sunnier afternoon than forecast shrink the grid block before any of it is bought. Hand
/// back a list of blocks to replay and that stops happening; worse, the blocks go on being executed
/// against a delivered-energy figure that stopped being true the moment they were quoted. So what you
/// edit are the bounds, and the planner keeps planning inside them.</para>
///
/// <para>Every field is optional and every one of them can only ever <em>narrow</em> the plan. Sending
/// none of them is the same as not sending this at all.</para>
/// </summary>
/// <param name="PlanId">
/// Identifies the plan this was quoted from. <b>Advisory only</b>: send it back and the start response
/// will tell you whether the forecast has moved since, which is worth knowing and is never a lock —
/// nothing is stored server-side and a start never fails because of it.
/// </param>
/// <param name="NotBefore">The charger may not run before this. Null for no lower bound.</param>
/// <param name="NotAfter">
/// The charger may not run after this. Null for no upper bound; the deadline applies regardless, so
/// this can only pull the window in.
/// </param>
/// <param name="ForbiddenWindows">
/// Stretches that must stay idle — a tariff's peak hours, a neighbour asleep the other side of the
/// wall. Overlapping and out-of-order entries are fine; what matters is the union.
/// </param>
/// <param name="MaxGridEnergyWh">
/// The most that may be bought over the whole plan. Null for no cap. <b>Zero is a real value</b> and
/// means sun only: the request is met from the roof or not at all, and the rest is reported as
/// shortfall rather than quietly imported.
/// </param>
public sealed record EditablePlanBody(
    string? PlanId = null,
    DateTimeOffset? NotBefore = null,
    DateTimeOffset? NotAfter = null,
    IReadOnlyList<TimeWindowBody>? ForbiddenWindows = null,
    double? MaxGridEnergyWh = null)
{
    /// <summary>What the planner takes, or null when nothing here narrows anything.</summary>
    internal TargetedChargeConstraints? ToConstraints()
    {
        var constraints = new TargetedChargeConstraints(
            NotBefore,
            NotAfter,
            ForbiddenWindows?.Select(window => new TimeWindow(window.Start, window.End)).ToList(),
            MaxGridEnergyWh);

        return constraints.IsEmpty ? null : constraints;
    }

    /// <summary>
    /// The constraints a plan was built under, echoed back in the shape they were sent — so a caller
    /// can change one field and return the rest untouched without reconstructing them.
    /// </summary>
    internal static EditablePlanBody From(TargetedChargePlan plan, TargetedChargeConstraints? constraints) => new(
        PlanId: PlanIdentity.For(plan),
        NotBefore: constraints?.NotBefore,
        NotAfter: constraints?.NotAfter,
        ForbiddenWindows: constraints?.ForbiddenWindows?
            .Select(window => new TimeWindowBody(window.Start, window.End)).ToList(),
        MaxGridEnergyWh: constraints?.MaxGridEnergyWh);
}

/// <summary>A stretch of time the charger must stay idle in, <c>[start, end)</c>.</summary>
/// <param name="Start">When it begins.</param>
/// <param name="End">When it ends. At or before <paramref name="Start"/> covers nothing.</param>
public sealed record TimeWindowBody(DateTimeOffset Start, DateTimeOffset End);
