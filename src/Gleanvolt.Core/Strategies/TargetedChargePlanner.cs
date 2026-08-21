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

        // Per slice: what the car can take from the sun (s), and what it could take in total if the
        // grid made up the difference (m). Their gap is the room the grid block has to work in.
        var solarWh = new double[slices.Count];
        var maxWh = new double[slices.Count];

        for (var i = 0; i < slices.Count; i++)
        {
            maxWh[i] = maxPowerWatts * slices[i].Hours;

            // A slice whose leftover surplus doesn't clear the charger's minimum is worth nothing to
            // the car however much energy it holds — the car cannot sip a budget slowly.
            solarWh[i] = slices[i].IsChargeable(options.MinChargePowerWatts)
                ? Math.Min(slices[i].AvailableWh, maxWh[i])
                : 0;
        }

        var ceilingWh = maxWh.Sum();
        var availableSolarWh = solarWh.Sum();

        if (needWh <= TargetToleranceWh)
        {
            return Complete(request, now, deadline, deliveredWh, ceilingWh, socFloor, batteryToFullWh, forecast);
        }

        // The car never takes more sun than it still needs, so a bright day stops the session at the
        // target rather than charging on past it.
        var solarTakenWh = Math.Min(availableSolarWh, needWh);
        var blocks = SolarBlocks(slices, solarWh, solarTakenWh);

        // The grid covers what the sun cannot — but never more than the remaining capacity, which is
        // exactly what makes the "not enough time" case fall out of the same arithmetic as the others.
        var gridWh = Math.Min(needWh - solarTakenWh, ceilingWh - availableSolarWh);
        var gridStart = AddGridBlocks(blocks, slices, solarWh, maxWh, maxPowerWatts, gridWh);

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

    /// <summary>
    /// The solar side of the plan, taken <b>earliest-first</b>. Not best-surplus-first: banking energy
    /// early is the robust choice when a forecast may disappoint, and it costs nothing, because nothing
    /// here is committed — a better afternoon than forecast simply shows up as a shrunken grid block on
    /// the next poll.
    /// </summary>
    private static List<TargetedChargeBlock> SolarBlocks(
        List<ForecastSlice> slices,
        double[] solarWh,
        double takeWh)
    {
        var blocks = new List<TargetedChargeBlock>();
        var remaining = takeWh;

        for (var i = 0; i < slices.Count && remaining > 0; i++)
        {
            if (solarWh[i] <= 0)
            {
                continue;
            }

            var powerWatts = solarWh[i] / slices[i].Hours;
            var take = Math.Min(remaining, solarWh[i]);
            remaining -= take;

            // A partly-taken slice ends the block early rather than derating it: the charger runs at
            // the power the sun supports and stops when the target is met.
            blocks.Add(new TargetedChargeBlock(
                slices[i].Start,
                slices[i].Start + TimeSpan.FromHours(take / powerWatts),
                TargetedChargeSource.Solar,
                powerWatts,
                take));
        }

        return blocks;
    }

    /// <summary>
    /// The grid block, placed <b>where the roof is busiest</b>: the slices carrying the most forecast
    /// surplus are filled first, and ties are broken earliest-first.
    ///
    /// <para>That one ordering answers both halves of the question. When the forecast says the sun
    /// will be up, the import runs <em>alongside</em> it, so every watt the roof actually produces at
    /// that moment — including the surplus that sits below the charger's minimum and is worth nothing
    /// to the car on its own, and including everything a P10 forecast underestimated — offsets the
    /// meter instead of being exported. When the window holds no forecast surplus at all, every slice
    /// ties at zero and the tie-break takes over: the block starts <b>now</b>.</para>
    ///
    /// <para>This replaces an earlier backward pass that put the import as late as it could go. Late
    /// placement bought one thing — the chance for a better-than-forecast afternoon to shrink the
    /// block before any of it was drawn — and it is not free: it also guarantees the charger is idle
    /// through the sunniest part of the day and then imports in the dark, where no amount of
    /// unforecast sun can reach it. Running under the sun keeps the deferral value (the plan is still
    /// rebuilt every poll, and the block still shrinks as delivery accrues) and collects the surplus
    /// as well.</para>
    ///
    /// <para>Within a slice the energy is prorated by <em>power</em>, not by time: where the sun is
    /// already giving the car something, the grid only has to supply <c>P_max</c> minus that.</para>
    /// </summary>
    /// <returns>The earliest instant the import starts, or null when none is needed.</returns>
    private static DateTimeOffset? AddGridBlocks(
        List<TargetedChargeBlock> blocks,
        List<ForecastSlice> slices,
        double[] solarWh,
        double[] maxWh,
        double maxPowerWatts,
        double deficitWh)
    {
        var remaining = deficitWh;
        DateTimeOffset? start = null;

        var sunniestFirst = Enumerable.Range(0, slices.Count)
            .OrderByDescending(i => slices[i].AvailableWatts)
            .ThenBy(i => slices[i].Start);

        foreach (var i in sunniestFirst)
        {
            if (remaining <= TargetToleranceWh)
            {
                break;
            }

            var headroomWh = maxWh[i] - solarWh[i];
            if (headroomWh <= 0)
            {
                // The sun alone already fills the charger in this slice; there is no room to import
                // into it. (Not an error — just a slice the pass steps over.)
                continue;
            }

            var take = Math.Min(remaining, headroomWh);
            remaining -= take;

            var gridPowerWatts = maxPowerWatts - (solarWh[i] / slices[i].Hours);

            // Anchored at the slice's start, so slices taken in sequence join up into one run rather
            // than leaving gaps, and a wholly dark window really does begin at "now".
            var blockStart = slices[i].Start;

            blocks.Add(new TargetedChargeBlock(
                blockStart,
                blockStart + TimeSpan.FromHours(take / gridPowerWatts),
                TargetedChargeSource.Grid,
                gridPowerWatts,
                take));

            if (start is null || blockStart < start)
            {
                start = blockStart;
            }
        }

        return start;
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
        SolarForecast? forecast) => new(
            Strategy: TargetedChargeStrategy.Complete,
            Now: now,
            DepartBy: request.DepartBy,
            Deadline: deadline,
            RequiredEnergyWh: request.RequiredEnergyWh,
            DeliveredEnergyWh: Math.Max(0, deliveredWh),
            RemainingEnergyWh: 0,
            SolarEnergyWh: 0,
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

            _ =>
                $"{plan.RemainingEnergyWh / 1000:F1}kWh by {plan.DepartBy.LocalDateTime:ddd HH:mm}: "
                + $"{plan.SolarEnergyWh / 1000:F1}kWh from sun, {plan.GridEnergyWh / 1000:F1}kWh from the grid "
                + $"starting {plan.GridStart?.LocalDateTime:HH:mm}.{blind}",
        };
    }
}
