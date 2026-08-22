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

        ForecastSlicer.Reserve(ReservableFor(slices, batteryFullBy), batteryToFullWh);

        var socFloor = SocFloor(slices.Sum(s => s.SurplusWh), options);

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

        var paced = PaceOverWindow(slices, deadline, needWh, options.MinChargePowerWatts, maxPowerWatts);

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
            Reason: string.Empty);

        return plan with { Reason = Describe(plan, usable) };
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
    /// The SOC floor in force while this plan runs: the forecast's own trajectory — how far the pack
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
    private static PacedPlan PaceOverWindow(
        List<ForecastSlice> slices,
        DateTimeOffset deadline,
        double needWh,
        double minPowerWatts,
        double maxPowerWatts)
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

        // How much the sun could still put in the car from each slice onwards <b>unaided</b>. Built once,
        // backwards, and read as "is there enough sun ahead to finish without buying anything?".
        //
        // Only slices whose surplus clears the charger's floor count. A half-hour of 3.5kW cannot run
        // the charger by itself, so promising it here would defer the start on the strength of sun that
        // can never arrive alone -- and then arrive at the deadline short. (Once the grid *is* paying,
        // that same 3.5kW is used in full; it just cannot be counted on to remove the need for a pace.)
        var solarAheadWh = new double[slices.Count + 1];
        for (var i = slices.Count - 1; i >= 0; i--)
        {
            var unaidedWatts = slices[i].AvailableWatts >= minPowerWatts
                ? Math.Min(slices[i].AvailableWatts, maxPowerWatts)
                : 0;

            solarAheadWh[i] = solarAheadWh[i + 1] + (unaidedWatts * slices[i].Hours);
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

            // The pace is what the *grid* must sustain, not what the charger must: whatever the rest of
            // the window's sun is forecast to deliver is subtracted first. Without that subtraction a
            // pace would import through a cloudy morning to hit an average the afternoon was going to
            // cover for nothing — buying energy the day was about to hand over free, which is the exact
            // mistake this mode exists to avoid.
            var deficitWh = Math.Max(0, remaining - solarAheadWh[index]);
            var gridPaceWatts = deficitWh / hoursLeft;

            // Sum, not max. The pace covers what the sun *cannot* reach -- the look-ahead already
            // subtracted every watt it can -- so the two are additive. Taking the greater of them would
            // let a sunny slice swallow the grid's share and quietly arrive at the deadline short.
            var wantWatts = surplusWatts + gridPaceWatts;

            // Nothing owed to the grid and not enough sun to run on: wait for the plateau. Bumping a
            // 500W shoulder up to the charger's floor would import 3.6kW to harvest 500W, on a day the
            // forecast says the sun finishes the job by itself.
            if (deficitWh <= TargetToleranceWh && surplusWatts < minPowerWatts)
            {
                continue;
            }

            var chargeWatts = Math.Clamp(wantWatts, minPowerWatts, maxPowerWatts);
            if (chargeWatts <= 0)
            {
                continue;
            }

            // Where the wanted rate is under the charger's floor this is shorter than the slice: run at
            // the floor for part of it rather than pretend the car can trickle.
            var takeWh = Math.Min(remaining, Math.Min(wantWatts * slice.Hours, chargeWatts * slice.Hours));
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
            ? Math.Max(0, needWh - solarAheadWh[0]) / hoursToDeadline
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
            Reason: $"Target met: {Math.Max(0, deliveredWh) / 1000:F1}kWh of {request.RequiredEnergyWh / 1000:F1}kWh delivered.");

    private static string Describe(TargetedChargePlan plan, bool usable)
    {
        var blind = usable ? string.Empty : " No usable forecast; planned as grid-only.";

        return plan.Strategy switch
        {
            TargetedChargeStrategy.Maximum =>
                $"Not enough time: charging flat out until {plan.Deadline.LocalDateTime:HH:mm} delivers "
                + $"{plan.ExpectedEnergyWh / 1000:F1}kWh of the {plan.RemainingEnergyWh / 1000:F1}kWh still needed "
                + $"({plan.ShortfallWh / 1000:F1}kWh short); leaving at "
                + $"{plan.FeasibleDeparture?.LocalDateTime:HH:mm} would cover it.{blind}",

            TargetedChargeStrategy.Solar =>
                $"{plan.RemainingEnergyWh / 1000:F1}kWh by {plan.DepartBy.LocalDateTime:ddd HH:mm} from forecast surplus alone; "
                + $"no grid import planned.{blind}",

            // Named explicitly, because "0.0kWh from sun" on a day with 6kWh of forecast surplus reads
            // as a broken plan rather than as the weak-sun case the placement is answering.
            _ when plan.HasUnusableSurplus =>
                $"{plan.RemainingEnergyWh / 1000:F1}kWh by {plan.DepartBy.LocalDateTime:ddd HH:mm}: "
                + $"{plan.ForecastSurplusWh / 1000:F1}kWh of surplus is forecast but none of it clears the charger's "
                + $"minimum, so the grid covers up to {plan.GridEnergyWh / 1000:F1}kWh from "
                + $"{plan.GridStart?.LocalDateTime:HH:mm} — placed over the best of that sun, which pays for part "
                + $"of it.{blind}",

            _ =>
                $"{plan.RemainingEnergyWh / 1000:F1}kWh by {plan.DepartBy.LocalDateTime:ddd HH:mm}: "
                + $"{plan.SolarEnergyWh / 1000:F1}kWh from sun, {plan.GridEnergyWh / 1000:F1}kWh from the grid "
                + $"starting {plan.GridStart?.LocalDateTime:HH:mm}.{blind}",
        };
    }
}
