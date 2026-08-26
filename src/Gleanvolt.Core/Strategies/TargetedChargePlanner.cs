using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Plans backwards from a deadline: put <see cref="TargetedChargeRequest.RequiredEnergyWh"/> into the
/// car by <see cref="TargetedChargeRequest.DepartBy"/>, and use as little grid as possible doing it.
/// Pure and stateless — rebuilt every poll from a refreshed forecast and the <b>measured</b> delivery
/// so far, and never committed to, exactly like <see cref="SolarDayPlanner"/>.
///
/// <para>Everything turns on one comparison: what is still needed against
/// <c>P_max × (deadline − now)</c>, the physical ceiling of the charger running flat out for every
/// remaining minute.</para>
///
/// <list type="bullet">
/// <item><description><b>Not enough time</b> — take everything, sun and grid together, and say so: the
/// plan reports how much will really arrive, how far short of the request that is, and the departure
/// time that <em>would</em> have covered it. Honesty is the whole value of this case.</description></item>
/// <item><description><b>Enough time</b> — the car takes every kilowatt-hour of forecast surplus the
/// home battery has not already claimed, and the grid covers only the remainder, in a block placed
/// over the <b>sunniest</b> part of the window; or, when the window holds no forecast surplus at all,
/// starting straight away.</description></item>
/// </list>
///
/// <para>Placement is the trick the mode exists for. Importing while the roof is producing means the
/// charger soaks up every watt the day actually delivers at that moment — the surplus below the
/// charger's own minimum, which is worth nothing to the car on its own, and everything a P10 forecast
/// underestimated — instead of exporting it and buying the same energy again after dark. And because
/// the plan is rebuilt every poll from the <em>measured</em> delivery, the block keeps shrinking as a
/// better-than-forecast day hands the target over for free.</para>
///
/// <para>With nothing to wait for — a dark window, or no usable forecast at all — the block starts at
/// once. Deferring an import that no amount of sun can undo buys nothing and only leaves less room
/// for a charger dropout, a car that limits itself, or a departure brought forward.</para>
///
/// <para>Two things the mode deliberately does <em>not</em> do: it does not touch the home battery's
/// priority (the pack's need is reserved out of the forecast by the same backward pass
/// <see cref="SolarDayPlanner"/> uses, and the discharge hold arms while the grid block runs), and it
/// does not lend from the pack. The grid is the honest source for the gap.</para>
/// </summary>
public static class TargetedChargePlanner
{
    /// <summary>
    /// How close to the target counts as met. One watt-hour: the delivered figure is integrated from
    /// measured power and crosses the target continuously, so this only exists to keep floating-point
    /// dust out of the <see cref="TargetedChargeStrategy.Complete"/> decision.
    /// </summary>
    private const double TargetToleranceWh = 1;

    /// <summary>
    /// Builds the plan.
    /// </summary>
    /// <param name="state">The live telemetry reading this plan is anchored to; its timestamp is "now".</param>
    /// <param name="request">What the owner asked for.</param>
    /// <param name="deliveredWh">
    /// What the charger has <b>measurably</b> delivered since the request was activated. Measured, not
    /// commanded: a car that limits itself to less than we asked for simply gets a longer grid block
    /// on the next poll, with no special case anywhere.
    /// </param>
    /// <param name="forecast">
    /// The forecast covering the window, or null when none has been fetched. Null is not a failure
    /// here — see <see cref="TargetedChargePlan.IsUsable"/>.
    /// </param>
    /// <param name="batteryFullBy">
    /// When the home battery is required to be at 100%. Its need is booked backwards from here, before
    /// the car is offered anything.
    /// </param>
    /// <param name="houseLoad">Expected household load excluding the EV, per instant.</param>
    /// <param name="biasFactor">Realised forecast bias to scale the remaining forecast by (1.0 = trust it as-is).</param>
    /// <param name="options">Planning parameters.</param>
    public static TargetedChargePlan Plan(
        EnergyState state,
        TargetedChargeRequest request,
        double deliveredWh,
        SolarForecast? forecast,
        DateTimeOffset batteryFullBy,
        IHouseLoadProfile houseLoad,
        double biasFactor,
        TargetedChargePlannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(houseLoad);
        ArgumentNullException.ThrowIfNull(options);

        var now = state.Timestamp;

        // The finish line, pulled in from the departure itself: "ready at 07:00" must not mean "still
        // charging at 07:00".
        var deadline = request.DepartBy - options.SafetyMargin;

        var needWh = Math.Max(0, request.RequiredEnergyWh - Math.Max(0, deliveredWh));
        var maxPowerWatts = Math.Max(0, options.MaxChargePowerWatts);

        // What the sun must deliver to finish the battery, charge losses included — the same figure,
        // computed the same way, as in the forecast-driven day plan.
        var batteryToFullWh = Math.Max(0, (100 - state.BatterySocPercent) / 100 * options.BatteryCapacityWh)
            / Math.Clamp(options.ChargeEfficiency, 0.1, 1.0);

        // Contiguous, not merely sliced: an overnight target reaches past the last forecast period, and
        // those dark hours are not unplannable — the grid can deliver at full power in every one of them.
        var slices = deadline > now
            ? ForecastSlicer.SliceContiguous(
                forecast, now, deadline, houseLoad, biasFactor, options.MinChargePowerWatts, options.Confidence)
            : [];

        // Before the constraints are applied, and that order matters: the home battery's claim on the
        // day is not the car's window to narrow. Reserving inside a shrunken window would concentrate
        // the whole of the pack's need into the hours the owner happens to have allowed the car, and
        // read as a day with no surplus in it.
        ForecastSlicer.Reserve(ReservableFor(slices, batteryFullBy), batteryToFullWh);

        // ...and after it, the car sees only the stretches it is allowed to run in (#128). Everything
        // below is computed from `slices`: the pace, the blocks, the solar/grid split, the ceiling and
        // therefore the shortfall. Narrowing the list here is the whole of honouring a constraint, and
        // is why a constraint cannot make the plan lie -- there is no second path that ignores it.
        var constraints = request.Constraints ?? TargetedChargeConstraints.None;
        slices = Allowed(slices, constraints);

        // Against the *pack's* horizon, not the car's. The two deadlines are unrelated: the car leaves
        // at 13:45, the battery has until evening to reach 100%, and there is a long sunny afternoon in
        // between that belongs to the pack's recovery whether or not the car is still plugged in.
        //
        // Measured live on 2026-08-23. Computing this over the request window meant the floor climbed as
        // the departure approached -- 50% at 10:26, 78% at 12:47, 84% at 12:58 -- because the shrinking
        // window recovered less and less. The pack was at 70%, so the last stretch of every session fell
        // below the floor, the car was cut off from a sunny lunchtime, and the remainder was bought.
        var recoveryWh = batteryFullBy > now
            ? ForecastSlicer.SliceContiguous(
                    forecast, now, batteryFullBy, houseLoad, biasFactor, options.MinChargePowerWatts, options.Confidence)
                .Sum(s => s.SurplusWh)
            : slices.Sum(s => s.SurplusWh);

        var socFloor = SocFloor(recoveryWh, options);

        // What the window's sun offers the car once the house and the pack have had theirs — reported
        // whether or not any of it clears the charger's floor. Without it a weak-sun plan reads as a
        // dark one, which is the opposite of why the import is about to be placed under the sun.
        var forecastSurplusWh = slices.Sum(s => s.AvailableWh);

        // The physical ceiling, and the only per-slice precompute the plan still needs: the charger
        // flat out for every remaining minute. Everything else now falls out of the paced pass.
        var ceilingWh = slices.Sum(s => maxPowerWatts * s.Hours);

        if (needWh <= TargetToleranceWh)
        {
            return Complete(
                request, now, deadline, deliveredWh, ceilingWh, socFloor, batteryToFullWh, forecastSurplusWh, forecast);
        }

        // One target in, one whole plan out, so the grid cap below can simply ask for less and get a
        // consistent answer -- rather than trying to subtract energy from blocks after the fact.
        var gridBudgetWh = constraints.MaxGridEnergyWh is { } cap ? Math.Max(0, cap) : (double?)null;

        var hold = PlanHold(request, slices, now, deadline, needWh, maxPowerWatts, options);

        PacedPlan paced;
        if (hold is { } held)
        {
            // The budget is one budget across both passes, so the free part spends first and the held
            // tail gets what is left -- rather than each half quietly being allowed the whole cap.
            var free = PaceOverWindow(
                held.FreeSlices, held.Release, needWh - held.TailWh,
                options.MinChargePowerWatts, maxPowerWatts, gridBudgetWh);

            var tail = PaceOverWindow(
                held.TailSlices, deadline, held.TailWh,
                options.MinChargePowerWatts, maxPowerWatts,
                gridBudgetWh is { } remaining ? Math.Max(0, remaining - free.GridWh) : null);

            paced = Merge(free, tail);
        }
        else
        {
            paced = PaceOverWindow(
                slices, deadline, needWh, options.MinChargePowerWatts, maxPowerWatts, gridBudgetWh);
        }

        var paceWatts = paced.PaceWatts;
        var solarTakenWh = paced.SolarWh;
        var gridWh = paced.GridWh;
        var blocks = paced.Blocks;
        var gridStart = paced.GridStart;

        var expectedWh = solarTakenWh + Math.Max(0, gridWh);
        var shortfallWh = Math.Max(0, needWh - expectedWh);

        var strategy = needWh > ceilingWh ? TargetedChargeStrategy.Maximum
            : gridWh > TargetToleranceWh ? TargetedChargeStrategy.SolarPlusGrid
            : TargetedChargeStrategy.Solar;

        // What the request would have needed to be possible: the extra minutes at full power, added to
        // the departure the owner asked for.
        var feasibleDeparture = strategy == TargetedChargeStrategy.Maximum && maxPowerWatts > 0
            ? (DateTimeOffset?)(request.DepartBy + TimeSpan.FromHours((needWh - ceilingWh) / maxPowerWatts))
            : null;

        var usable = IsForecastUsable(forecast, now, deadline);

        var plan = new TargetedChargePlan(
            Strategy: strategy,
            Now: now,
            DepartBy: request.DepartBy,
            Deadline: deadline,
            RequiredEnergyWh: request.RequiredEnergyWh,
            DeliveredEnergyWh: Math.Max(0, deliveredWh),
            RemainingEnergyWh: needWh,
            SolarEnergyWh: solarTakenWh,
            ForecastSurplusWh: forecastSurplusWh,
            RequiredPaceWatts: paceWatts,
            GridEnergyWh: Math.Max(0, gridWh),
            CeilingEnergyWh: ceilingWh,
            ExpectedEnergyWh: expectedWh,
            ShortfallWh: shortfallWh,
            GridStart: gridStart,
            FeasibleDeparture: feasibleDeparture,
            SocFloorPercent: socFloor,
            BatteryToFullWh: batteryToFullWh,
            Blocks: [.. blocks.OrderBy(b => b.Start).ThenBy(b => b.Source)],
            ForecastAsOf: forecast?.RetrievedAt,
            IsUsable: usable,
            TailEnergyWh: hold?.TailWh ?? 0,
            HoldUntil: hold?.Release,
            Reason: string.Empty);

        return plan with { Reason = Describe(plan, usable) };
    }

    /// <summary>
    /// The slices the car is allowed to charge in, with the parts that fall outside the owner's limits
    /// removed (#128).
    ///
    /// <para>A slice straddling a boundary is <b>trimmed, not dropped</b>. The forecast arrives in
    /// half-hour periods and a limit lands wherever the owner put it; dropping a slice that is
    /// three-quarters allowed would quietly cost the car most of an hour of sun it is entitled to, and
    /// dropping enough of them turns a feasible plan into a reported shortfall that is not real.</para>
    ///
    /// <para>Every energy on a slice is proportional to its length, so a trim scales them all by the
    /// same fraction — including <c>ReservedWh</c>, so the pack keeps the share of this slice it had
    /// already booked rather than gaining or losing some by the trim.</para>
    /// </summary>
    private static List<ForecastSlice> Allowed(List<ForecastSlice> slices, TargetedChargeConstraints constraints)
    {
        if (constraints.IsEmpty || slices.Count == 0)
        {
            return slices;
        }

        var allowed = new List<ForecastSlice>(slices.Count);

        // Through Within, which already prorates a slice straddling an edge and is the same operation
        // the just-in-time hold performs to split the window. A second implementation of it would be a
        // second place for "energies scale, powers do not" to be got wrong.
        foreach (var (start, end) in Permitted(slices[0].Start, slices[^1].End, constraints))
        {
            allowed.AddRange(Within(slices, start, end));
        }

        return [.. allowed.OrderBy(slice => slice.Start)];
    }

    /// <summary>
    /// What is left of <c>[start, end)</c> once the bounds are applied and every forbidden window is
    /// cut out of it — which can be nothing, one stretch, or several.
    /// </summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> Permitted(
        DateTimeOffset start, DateTimeOffset end, TargetedChargeConstraints constraints)
    {
        start = constraints.NotBefore is { } before && before > start ? before : start;
        end = constraints.NotAfter is { } after && after < end ? after : end;

        if (end <= start)
        {
            return [];
        }

        var remaining = new List<(DateTimeOffset Start, DateTimeOffset End)> { (start, end) };

        foreach (var window in constraints.ForbiddenWindows ?? [])
        {
            var next = new List<(DateTimeOffset Start, DateTimeOffset End)>(remaining.Count + 1);

            foreach (var (from, to) in remaining)
            {
                if (!window.Overlaps(from, to))
                {
                    next.Add((from, to));
                    continue;
                }

                // A window biting the middle leaves a piece either side; one biting an end leaves one.
                if (window.Start > from)
                {
                    next.Add((from, window.Start));
                }

                if (window.End < to)
                {
                    next.Add((window.End, to));
                }
            }

            remaining = next;
        }

        return remaining;
    }

    /// <summary>
    /// The slices the home battery may book against: those finishing before its own deadline. When the
    /// window lies entirely beyond that deadline the battery books against all of them instead —
    /// having missed one evening's 100% is no reason to hand the whole of the next day to the car.
    /// </summary>
    private static IReadOnlyList<ForecastSlice> ReservableFor(List<ForecastSlice> slices, DateTimeOffset batteryFullBy)
    {
        var before = slices.Where(s => s.End <= batteryFullBy).ToList();
        return before.Count > 0 ? before : slices;
    }

    /// <summary>
    /// The SOC floor in force while this plan runs, from the surplus the pack can still recover from
    /// <b>by its own deadline</b> — see the call site for why that is not the request's. Otherwise: the
    /// forecast's own trajectory — how far the pack
    /// may fall and still recover from the surplus still coming its way — raised to the owner's
    /// configured minimum. The same formula the forecast-driven day plan uses, so the two modes agree
    /// about the battery.
    /// </summary>
    private static double SocFloor(double refillableWh, TargetedChargePlannerOptions options)
    {
        var recoverablePercent = refillableWh * Math.Clamp(options.ChargeEfficiency, 0.1, 1.0)
            / options.BatteryCapacityWh * 100;

        return Math.Max(
            Math.Clamp(100 - recoverablePercent, 0, 100),
            Math.Clamp(options.MinBatterySocFloorPercent, 0, 100));
    }

    /// <summary>
    /// The window split in two by a <see cref="TargetedChargePriority.JustInTime"/> hold: everything
    /// below the rest point is delivered before <see cref="Release"/>, and the tail after it.
    /// </summary>
    private readonly record struct HeldTail(
        double TailWh,
        DateTimeOffset Release,
        List<ForecastSlice> FreeSlices,
        List<ForecastSlice> TailSlices);

    /// <summary>
    /// Works out where — and whether — to hold the tail back.
    ///
    /// <para>The release point is arithmetic and nothing more: <c>deadline − tail ÷ P_max − slack</c>.
    /// No taper model, no charge curve, no reading of what the car says its limit is. If the tail runs
    /// slower than the charger's maximum the next poll simply finds a higher pace, and if the car stops
    /// short of the target on its own limit the controller's completion path says so. Predicting either
    /// in advance would add a way to be wrong without adding a way to be right.</para>
    ///
    /// <para>Returns null — no hold, today's single pass over the whole window — in four cases, and
    /// they are all the same case wearing different hats: <b>the promise outranks the preference</b>.
    /// The owner did not ask for a hold; there is nothing above the rest point to hold; the release
    /// point has already passed, so there is no room left to wait in; or the energy below the rest
    /// point could not fit in the shortened window that holding would leave it, which would trade a
    /// full battery at 06:45 for a flat one.</para>
    /// </summary>
    private static HeldTail? PlanHold(
        TargetedChargeRequest request,
        List<ForecastSlice> slices,
        DateTimeOffset now,
        DateTimeOffset deadline,
        double needWh,
        double maxPowerWatts,
        TargetedChargePlannerOptions options)
    {
        if (!request.HoldsTail || maxPowerWatts <= 0)
        {
            return null;
        }

        // Never more than is still owed. As delivery eats into the request the free part goes first, so
        // the tail is the last thing standing — at which point need and tail are the same number and
        // the charger has nothing to do but wait, which is precisely the state being aimed at.
        var tailWh = Math.Min(request.TailEnergyWh, needWh);
        if (tailWh <= TargetToleranceWh)
        {
            return null;
        }

        var release = deadline - TimeSpan.FromHours(tailWh / maxPowerWatts) - options.ReleaseSlack;
        if (release <= now)
        {
            return null;
        }

        var freeSlices = Within(slices, now, release);
        var tailSlices = Within(slices, release, deadline);

        // Would holding leave enough room for everything below the rest point? Flat out for every
        // minute before the release is the most that window can take, and if the free part needs more
        // than that then the hold is what makes the target unreachable. Give it up rather than arrive
        // short: the deadline was the promise, the timing was only a preference.
        var freeWh = needWh - tailWh;
        var freeCeilingWh = freeSlices.Sum(s => maxPowerWatts * s.Hours);
        if (freeWh > freeCeilingWh)
        {
            return null;
        }

        return new HeldTail(tailWh, release, freeSlices, tailSlices);
    }

    /// <summary>
    /// The part of each slice falling inside <c>[from, to]</c>, prorated across a slice that straddles
    /// an edge. Energies scale with the fraction taken; powers do not, so
    /// <see cref="ForecastSlice.AvailableWatts"/> survives the cut unchanged — which is what lets the
    /// same paced pass run over a sub-window without knowing it is one.
    ///
    /// <para>The home battery's booking is prorated with everything else, so the pack keeps exactly the
    /// share of each slice it had already claimed.</para>
    /// </summary>
    private static List<ForecastSlice> Within(List<ForecastSlice> slices, DateTimeOffset from, DateTimeOffset to)
    {
        var within = new List<ForecastSlice>();

        foreach (var slice in slices)
        {
            var start = slice.Start > from ? slice.Start : from;
            var end = slice.End < to ? slice.End : to;
            var hours = (end - start).TotalHours;
            if (hours <= 0)
            {
                continue;
            }

            var fraction = slice.Hours > 0 ? hours / slice.Hours : 0;

            within.Add(new ForecastSlice
            {
                Start = start,
                End = end,
                Hours = hours,
                SurplusWatts = slice.SurplusWatts,
                PvWh = slice.PvWh * fraction,
                HouseWh = slice.HouseWh * fraction,
                SurplusWh = slice.SurplusWh * fraction,
                IsPlateau = slice.IsPlateau,
                ReservedWh = slice.ReservedWh * fraction,
            });
        }

        return within;
    }

    /// <summary>
    /// Two paced passes read as one plan. The pace is the <b>free</b> window's, not the tail's, because
    /// the pace is what the controller works to right now and right now is before the release — and a
    /// hold is only ever planned when the release is still ahead.
    /// </summary>
    private static PacedPlan Merge(PacedPlan free, PacedPlan tail) => new(
        [.. free.Blocks, .. tail.Blocks],
        free.SolarWh + tail.SolarWh,
        free.GridWh + tail.GridWh,
        free.GridStart ?? tail.GridStart,
        free.PaceWatts);

    /// <summary>What one paced pass over the window produced.</summary>
    private readonly record struct PacedPlan(
        List<TargetedChargeBlock> Blocks,
        double SolarWh,
        double GridWh,
        DateTimeOffset? GridStart,
        double PaceWatts);

    /// <summary>
    /// Spreads the charge across the <b>whole window</b> at the slowest rate that still meets the
    /// deadline, taking every watt of sun above that rate for free.
    ///
    /// <para>This replaces placing a full-power block somewhere in the window, and the reason is
    /// arithmetic the site demonstrated on the first day: 13 kWh drawn at 10.7 kW is over in 87
    /// minutes, and for all 87 of them the car outruns the roof by 7 kW — so 9 of the 13 kWh came off
    /// the meter on a day that later peaked at 8.5 kW. The same 13 kWh paced across a four-hour window
    /// is 3.3 kW, which the roof matches for much of it. <b>Rate, not placement, decides the solar
    /// share</b>, because the charger can only ever use the sun that is shining while it runs.</para>
    ///
    /// <para>Per slice: want <c>max(surplus, pace)</c>. The pace is the floor — the promise has to be
    /// kept — and the sun raises it whenever it can, which is free energy and lowers the pace for
    /// every slice after it. Clamped into the charger's range: below its ~4.14 kW minimum the block
    /// runs at the minimum for a <em>fraction</em> of the slice instead, because the car cannot sip;
    /// above its maximum the slice is simply flat out, which is what makes the "not enough time" case
    /// fall out of this same loop with no special case.</para>
    ///
    /// <para>The two blocks a slice emits overlap in time on purpose: they are one charging period
    /// with two contributors, the sun supplying what it has and the grid making up the difference.</para>
    /// </summary>
    /// <param name="gridBudgetWh">
    /// The most that may be bought over this pass, or null for no cap (#128). Spent as the pass goes
    /// rather than applied to the total afterwards, and deliberately so: a cap enforced by asking for
    /// less energy overall would cut the <em>sun's</em> share too, which is the opposite of what
    /// "buy no more than this" means.
    /// </param>
    private static PacedPlan PaceOverWindow(
        List<ForecastSlice> slices,
        DateTimeOffset deadline,
        double needWh,
        double minPowerWatts,
        double maxPowerWatts,
        double? gridBudgetWh = null)
    {
        var blocks = new List<TargetedChargeBlock>();
        var remaining = needWh;
        double solarTotalWh = 0;
        double gridTotalWh = 0;
        DateTimeOffset? gridStart = null;

        if (maxPowerWatts <= 0)
        {
            return new PacedPlan(blocks, 0, 0, null, 0);
        }

        // The pace is spread over the window that actually exists, which is not always the window that
        // was asked for: a constraint (#128) can end the charging window well before the deadline, and
        // pacing to the deadline then computes an average over hours the charger is not allowed to run
        // in -- delivering a fraction of the target and calling it paced. Identical to `deadline`
        // whenever nothing has been cut, because the slices are contiguous up to it.
        if (slices.Count > 0 && slices[^1].End < deadline)
        {
            deadline = slices[^1].End;
        }

        // Two look-aheads, because the sun answers two different questions and conflating them gets one
        // of them badly wrong.
        //
        // <b>Unaided</b> counts only slices whose surplus clears the charger's floor -- sun that can run
        // the car by itself. It answers "need we buy anything at all?", and has to be the strict
        // measure: promising sub-floor sun here would defer the start on strength that never arrives
        // alone, and finish short.
        //
        // <b>Assisted</b> counts every watt of surplus. It answers "once the charger is running, how
        // much of this does the roof actually cover?", and has to be the generous measure: a 3.2kW
        // half-hour genuinely does supply 3.2kW to a charger held at its 4.14kW floor.
        //
        // Using the strict figure for both was a real defect. A 4.2kW day carrying 17.5kWh of surplus
        // against a 15kWh target scored ~4kWh of "usable" sun and bought 11kWh from the grid, starting
        // at 02:00 in the dark, on a day the roof could have covered outright.
        var unaidedAheadWh = new double[slices.Count + 1];
        var assistedAheadWh = new double[slices.Count + 1];
        for (var i = slices.Count - 1; i >= 0; i--)
        {
            var reachableWh = Math.Min(Math.Max(0, slices[i].AvailableWatts), maxPowerWatts) * slices[i].Hours;

            assistedAheadWh[i] = assistedAheadWh[i + 1] + reachableWh;
            unaidedAheadWh[i] = unaidedAheadWh[i + 1]
                + (slices[i].AvailableWatts >= minPowerWatts ? reachableWh : 0);
        }

        for (var index = 0; index < slices.Count; index++)
        {
            var slice = slices[index];

            if (remaining <= TargetToleranceWh || slice.Hours <= 0)
            {
                continue;
            }

            var hoursLeft = (deadline - slice.Start).TotalHours;
            if (hoursLeft <= 0)
            {
                continue;
            }

            // Not gated on clearing the charger's minimum. Under a pace the charger is running anyway,
            // so a surplus far below its floor still comes off the meter — which is the whole reason
            // sub-minimum sun stopped being worthless.
            var surplusWatts = Math.Max(0, slice.AvailableWatts);

            // Can the sun finish this on its own from here? If so nothing is owed to the grid, and a
            // sub-floor slice is not worth touching: bumping a 500W shoulder to the charger's floor
            // would import 3.6kW to harvest 500W on a day the roof covers outright.
            var unaidedCanFinish = remaining <= unaidedAheadWh[index] + TargetToleranceWh;
            if (unaidedCanFinish && surplusWatts < minPowerWatts)
            {
                continue;
            }

            // The pace is what the *grid* must sustain, not what the charger must: every watt the roof
            // will actually reach the car with is subtracted first. Without that a pace imports through
            // the night to hit an average the day was going to cover for nothing.
            var deficitWh = Math.Max(0, remaining - assistedAheadWh[index]);
            var gridPaceWatts = deficitWh / hoursLeft;

            // Sum, not max. The pace covers what the sun *cannot* reach -- the look-ahead already
            // subtracted every watt it can -- so the two are additive. Taking the greater of them would
            // let a sunny slice swallow the grid's share and quietly arrive at the deadline short.
            var wantWatts = surplusWatts + gridPaceWatts;
            var chargeWatts = Math.Clamp(wantWatts, minPowerWatts, maxPowerWatts);
            if (chargeWatts <= 0)
            {
                continue;
            }

            // What is left of the grid budget, converted into a power this slice may add on top of the
            // sun. The sun's own contribution is never reduced by it: a cap says what may be bought,
            // not how much of the roof may be used.
            if (gridBudgetWh is { } budget)
            {
                var fromSun = Math.Min(surplusWatts, chargeWatts);
                var roomWatts = Math.Max(0, budget - gridTotalWh) / slice.Hours;
                var capped = fromSun + Math.Min(chargeWatts - fromSun, roomWatts);

                // Below the charger's floor there is no way to run this slice inside the budget at all.
                // Skipping it is what turns the unbought energy into a reported shortfall rather than a
                // quiet overspend.
                if (capped + TargetToleranceWh < minPowerWatts)
                {
                    continue;
                }

                chargeWatts = capped;
            }

            // Once the sun cannot finish alone, a slice carrying any surplus at all is worth running
            // right through at the charger's floor: the roof supplies what it has, and only the
            // shortfall to the floor is bought. Harvesting a 3.2kW half-hour costs 0.9kW of grid;
            // skipping it and buying the same energy after dark costs the whole 4.14kW.
            var harvest = !unaidedCanFinish && surplusWatts > 0;
            var wantWh = harvest ? chargeWatts * slice.Hours : wantWatts * slice.Hours;

            var takeWh = Math.Min(remaining, Math.Min(wantWh, chargeWatts * slice.Hours));
            if (takeWh <= 0)
            {
                continue;
            }

            var hours = takeWh / chargeWatts;
            var end = slice.Start + TimeSpan.FromHours(hours);

            var solarWatts = Math.Min(surplusWatts, chargeWatts);
            var solarWh = solarWatts * hours;
            var gridWh = Math.Max(0, takeWh - solarWh);

            if (solarWh > 0)
            {
                blocks.Add(new TargetedChargeBlock(slice.Start, end, TargetedChargeSource.Solar, solarWatts, solarWh));
                solarTotalWh += solarWh;
            }

            if (gridWh > TargetToleranceWh)
            {
                blocks.Add(new TargetedChargeBlock(
                    slice.Start, end, TargetedChargeSource.Grid, chargeWatts - solarWatts, gridWh));
                gridTotalWh += gridWh;
                gridStart ??= slice.Start;
            }

            remaining -= takeWh;
        }

        // The pace as it stands right now, which is what the controller works to: the part of the need
        // the window's sun is not forecast to cover, spread over the time left. Zero on a plan the sun
        // covers outright -- there is nothing for the grid to keep up with.
        var hoursToDeadline = slices.Count > 0 ? (deadline - slices[0].Start).TotalHours : 0;
        var paceWatts = hoursToDeadline > 0
            ? Math.Max(0, needWh - assistedAheadWh[0]) / hoursToDeadline
            : 0;

        return new PacedPlan(blocks, solarTotalWh, gridTotalWh, gridStart, paceWatts);
    }

    /// <summary>
    /// Whether a forecast actually informed this plan. False means "grid-only, because we are planning
    /// blind" — a caveat to report, not a fallback to take: the target is still met either way, and any
    /// surplus that does appear is used opportunistically by the controller regardless.
    /// </summary>
    private static bool IsForecastUsable(SolarForecast? forecast, DateTimeOffset now, DateTimeOffset deadline) =>
        forecast is not null && forecast.Periods.Any(p => p.PeriodEnd > now && p.PeriodStart < deadline);

    private static TargetedChargePlan Complete(
        TargetedChargeRequest request,
        DateTimeOffset now,
        DateTimeOffset deadline,
        double deliveredWh,
        double ceilingWh,
        double socFloorPercent,
        double batteryToFullWh,
        double forecastSurplusWh,
        SolarForecast? forecast) => new(
            Strategy: TargetedChargeStrategy.Complete,
            Now: now,
            DepartBy: request.DepartBy,
            Deadline: deadline,
            RequiredEnergyWh: request.RequiredEnergyWh,
            DeliveredEnergyWh: Math.Max(0, deliveredWh),
            RemainingEnergyWh: 0,
            SolarEnergyWh: 0,
            ForecastSurplusWh: forecastSurplusWh,
            RequiredPaceWatts: 0,
            GridEnergyWh: 0,
            CeilingEnergyWh: ceilingWh,
            ExpectedEnergyWh: 0,
            ShortfallWh: 0,
            GridStart: null,
            FeasibleDeparture: null,
            SocFloorPercent: socFloorPercent,
            BatteryToFullWh: batteryToFullWh,
            Blocks: [],
            ForecastAsOf: forecast?.RetrievedAt,
            IsUsable: true,
            TailEnergyWh: 0,
            HoldUntil: null,
            Reason: $"Target met: {Math.Max(0, deliveredWh) / 1000:F1}kWh of {request.RequiredEnergyWh / 1000:F1}kWh delivered.");

    private static string Describe(TargetedChargePlan plan, bool usable)
    {
        var blind = usable ? string.Empty : " No usable forecast; planned as grid-only.";

        // Said first, because it is the sentence that answers the question an idle charger provokes. A
        // plan holding 6kWh back until 04:10 looks identical to a broken one until this is read.
        var held = plan.HoldUntil is { } release
            ? $" Holding the last {plan.TailEnergyWh / 1000:F1}kWh until {release.LocalDateTime:HH:mm} so the car is "
                + "full just before departure."
            : string.Empty;

        return plan.Strategy switch
        {
            TargetedChargeStrategy.Maximum =>
                $"Not enough time: charging flat out until {plan.Deadline.LocalDateTime:HH:mm} delivers "
                + $"{plan.ExpectedEnergyWh / 1000:F1}kWh of the {plan.RemainingEnergyWh / 1000:F1}kWh still needed "
                + $"({plan.ShortfallWh / 1000:F1}kWh short); leaving at "
                + $"{plan.FeasibleDeparture?.LocalDateTime:HH:mm} would cover it.{blind}",

            TargetedChargeStrategy.Solar =>
                $"{plan.RemainingEnergyWh / 1000:F1}kWh by {plan.DepartBy.LocalDateTime:ddd HH:mm} from forecast surplus alone; "
                + $"no grid import planned.{held}{blind}",

            // Named explicitly, because "0.0kWh from sun" on a day with 6kWh of forecast surplus reads
            // as a broken plan rather than as the weak-sun case the placement is answering.
            _ when plan.HasUnusableSurplus =>
                $"{plan.RemainingEnergyWh / 1000:F1}kWh by {plan.DepartBy.LocalDateTime:ddd HH:mm}: "
                + $"{plan.ForecastSurplusWh / 1000:F1}kWh of surplus is forecast but none of it clears the charger's "
                + $"minimum, so the grid covers up to {plan.GridEnergyWh / 1000:F1}kWh from "
                + $"{plan.GridStart?.LocalDateTime:HH:mm} — placed over the best of that sun, which pays for part "
                + $"of it.{held}{blind}",

            _ =>
                $"{plan.RemainingEnergyWh / 1000:F1}kWh by {plan.DepartBy.LocalDateTime:ddd HH:mm}: "
                + $"{plan.SolarEnergyWh / 1000:F1}kWh from sun, {plan.GridEnergyWh / 1000:F1}kWh from the grid "
                + $"starting {plan.GridStart?.LocalDateTime:HH:mm}.{held}{blind}",
        };
    }
}
