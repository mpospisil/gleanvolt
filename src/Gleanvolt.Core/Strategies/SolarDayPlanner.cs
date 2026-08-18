using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Turns the solar forecast and a live telemetry reading into a <see cref="SolarDayPlan"/>: how much
/// of today's remaining sun the car may have, when it may have it, and how far the home battery may
/// be drawn down and still reach 100% by the evening deadline. Pure and stateless — recomputed every
/// poll, so it self-corrects rather than committing to a plan.
///
/// <para>The central idea is that a kilowatt-hour at 08:00 is <b>not</b> interchangeable with one at
/// 13:00. Production splits into two kinds:</para>
/// <list type="bullet">
/// <item><description><b>Shoulder</b> — surplus below the charger's minimum power. The car cannot use
/// it at all (6 A on three phases is ~4.2 kW, and the charger has no phase switching), so the home
/// battery is its only consumer.</description></item>
/// <item><description><b>Plateau</b> — surplus at or above that minimum. The only energy the car can
/// take, and the only window in which it can charge.</description></item>
/// </list>
/// <para>So the two consumers are not really competing: filling the battery from the plateau wastes
/// the one scarce window, while filling it from the shoulders costs the car nothing. The battery's
/// need is therefore booked by a <b>backward pass from the deadline</b> — reserving the latest
/// production first — and whatever is left, at or above the minimum power, is the car's.</para>
/// </summary>
public static class SolarDayPlanner
{
    /// <summary>Below this the car's remaining expectation is treated as nothing worth planning for.</summary>
    private const double NegligibleEnergyWh = 100;

    /// <summary>
    /// Builds the plan. <paramref name="forecast"/> should be today's periods; pass null (or an empty
    /// forecast) when none has been fetched yet and the caller gets an unusable plan back.
    /// </summary>
    /// <param name="state">The live telemetry reading this plan is anchored to.</param>
    /// <param name="forecast">Today's forecast periods.</param>
    /// <param name="deadline">When the home battery is required to be at 100%.</param>
    /// <param name="houseLoad">Expected household load excluding the EV, per instant (see <see cref="IHouseLoadProfile"/>).</param>
    /// <param name="biasFactor">Realised forecast bias to scale the remaining forecast by (1.0 = trust it as-is).</param>
    /// <param name="evDeliveredTodayWh">How much the car has already had today, for the shortfall maths.</param>
    /// <param name="previousOutlook">
    /// The outlook reported last cycle. Used only for hysteresis around the classification
    /// thresholds: without it the outlook flips between Tight and Shortfall on a rounding wobble --
    /// three changes inside three minutes were observed in validation.
    /// </param>
    /// <param name="options">Planning parameters.</param>
    public static SolarDayPlan Plan(
        EnergyState state,
        SolarForecast? forecast,
        DateTimeOffset deadline,
        IHouseLoadProfile houseLoad,
        double biasFactor,
        double evDeliveredTodayWh,
        SolarDayPlannerOptions options,
        DayOutlook previousOutlook = DayOutlook.Unknown)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(houseLoad);
        ArgumentNullException.ThrowIfNull(options);

        if (forecast is null || forecast.Periods.Count == 0)
        {
            return SolarDayPlan.Unavailable(deadline, "No solar forecast available; falling back to live-solar behaviour.");
        }

        var now = state.Timestamp;

        // Past the deadline the battery's evening constraint is already settled one way or the other,
        // so planning stops at the end of the day's production instead: any sun still to come should
        // reach the car rather than be withheld by a deadline that has passed. The battery keeps its
        // priority regardless, because the reservation below still runs first.
        var lastPeriodEnd = forecast.Periods.Max(p => p.PeriodEnd);
        var horizon = now < deadline ? deadline : lastPeriodEnd;

        var slices = SliceForecast(forecast, now, horizon, houseLoad, biasFactor, options);
        var remainingPvWh = slices.Sum(s => s.PvWh);
        var expectedHouseWh = slices.Sum(s => s.HouseWh);

        // What the sun must deliver to finish the battery, charge losses included.
        var batteryToFullWh = Math.Max(0, (100 - state.BatterySocPercent) / 100 * options.BatteryCapacityWh)
            / Math.Clamp(options.ChargeEfficiency, 0.1, 1.0);

        Reserve(slices, batteryToFullWh);

        var shoulderWh = slices.Where(s => !s.IsPlateau).Sum(s => s.SurplusWh);
        var plateauWh = slices.Where(s => s.IsPlateau).Sum(s => s.SurplusWh);
        var claimedFromPlateauWh = slices.Where(s => s.IsPlateau).Sum(s => s.ReservedWh);

        // What the battery could still absorb today if it had to: every remaining watt of surplus, not
        // merely the part booked above. The booking is sized to the need the battery has *right now* —
        // but the floor answers a different question ("how far may SOC drop and still recover?"), and a
        // deeper discharge simply grows the need, which the battery outranks the car to satisfy.
        var batteryRefillableWh = slices.Sum(s => s.SurplusWh);

        var trajectoryFloor = TrajectorySocFloor(batteryRefillableWh, options);
        var socFloor = ClampFloor(trajectoryFloor, options);

        var feasibleEnergyWh = slices.Where(s => s.IsFeasible(options)).Sum(s => s.AvailableWh);
        var evBudgetWh = Math.Max(0, remainingPvWh - expectedHouseWh - batteryToFullWh);
        var usableEvWh = Math.Min(evBudgetWh, feasibleEnergyWh);
        var window = FirstViableWindow(slices, options);

        var evWantRemainingWh = Math.Max(0, options.DailyEvTargetWh - evDeliveredTodayWh);
        var shortfallWh = Math.Max(0, expectedHouseWh + batteryToFullWh + evWantRemainingWh - remainingPvWh);
        var evExpectedRemainingWh = window is null ? 0 : Math.Min(evWantRemainingWh, usableEvWh);
        var outlook = Classify(evWantRemainingWh, evExpectedRemainingWh, window, previousOutlook, options);

        return new SolarDayPlan(
            RemainingPvWh: remainingPvWh,
            ShoulderEnergyWh: shoulderWh,
            PlateauEnergyWh: plateauWh,
            PlateauClaimedByBatteryWh: claimedFromPlateauWh,
            ExpectedHouseWh: expectedHouseWh,
            BatteryToFullWh: batteryToFullWh,
            EvBudgetWh: evBudgetWh,
            FeasibleEvEnergyWh: usableEvWh,
            NextFeasibleWindow: window,
            RequiredSocFloorPercent: socFloor,
            TrajectorySocFloorPercent: trajectoryFloor,
            ShortfallWh: shortfallWh,
            EvExpectedTodayWh: evDeliveredTodayWh + evExpectedRemainingWh,
            EvTargetWh: options.DailyEvTargetWh,
            Outlook: outlook,
            BiasFactor: biasFactor,
            Deadline: deadline,
            ForecastAsOf: forecast.RetrievedAt,
            IsUsable: true,
            Reason: Describe(outlook, usableEvWh, shortfallWh, socFloor, window),
            Timeline: BuildTimeline(slices, options));
    }

    /// <summary>
    /// One <see cref="SolarDayPlanTimelinePoint"/> per slice, each carrying what the required SOC
    /// floor would be if that slice's start were "now" — <see cref="TrajectorySocFloor"/> clamped, the
    /// same formula the live plan uses, evaluated at every point instead of only the current one. Built
    /// back-to-front because each point needs the surplus of every slice from it to the end, which is
    /// exactly the running total a reverse pass accumulates for free.
    /// </summary>
    private static List<SolarDayPlanTimelinePoint> BuildTimeline(List<Slice> slices, SolarDayPlannerOptions options)
    {
        var timeline = new List<SolarDayPlanTimelinePoint>(slices.Count);
        var suffixSurplusWh = 0.0;

        for (var i = slices.Count - 1; i >= 0; i--)
        {
            suffixSurplusWh += slices[i].SurplusWh;
            timeline.Add(new SolarDayPlanTimelinePoint(
                slices[i].Start,
                slices[i].End,
                slices[i].SurplusWatts,
                slices[i].IsPlateau,
                ClampFloor(TrajectorySocFloor(suffixSurplusWh, options), options)));
        }

        timeline.Reverse();
        return timeline;
    }

    /// <summary>
    /// The SOC the battery must not fall below now if the evening deadline is still to be met.
    /// Derived purely from the energy still coming its way: as the day runs out that approaches zero,
    /// so the floor approaches 100% and the car is squeezed out of the late afternoon — the evening
    /// guarantee, with no scheduling code.
    ///
    /// <para>This is the trajectory on its own, <b>unclamped</b>. On a sunny morning it sits far below
    /// the owner's configured minimum, and the difference matters: falling through the trajectory
    /// costs the evening 100%, while falling through the clamp above it costs only a preference the
    /// day still has hours to make good.</para>
    /// </summary>
    private static double TrajectorySocFloor(double batteryRefillableWh, SolarDayPlannerOptions options)
    {
        var recoverablePercent = batteryRefillableWh * Math.Clamp(options.ChargeEfficiency, 0.1, 1.0)
            / options.BatteryCapacityWh * 100;

        return Math.Clamp(100 - recoverablePercent, 0, 100);
    }

    /// <summary>The trajectory raised to the owner's hard minimum — the floor actually in force.</summary>
    private static double ClampFloor(double trajectoryFloorPercent, SolarDayPlannerOptions options) =>
        Math.Max(trajectoryFloorPercent, Math.Clamp(options.MinBatterySocFloorPercent, 0, 100));

    // Cuts the forecast into the part of each period that falls inside (now, horizon]. Periods
    // straddling either edge are prorated, so a plan built at 12:47 doesn't count the whole 12:30-13:00
    // period as still to come.
    private static List<Slice> SliceForecast(
        SolarForecast forecast,
        DateTimeOffset now,
        DateTimeOffset horizon,
        IHouseLoadProfile houseLoad,
        double biasFactor,
        SolarDayPlannerOptions options)
    {
        var slices = new List<Slice>();

        foreach (var period in forecast.Periods.OrderBy(p => p.PeriodEnd))
        {
            var start = period.PeriodStart > now ? period.PeriodStart : now;
            var end = period.PeriodEnd < horizon ? period.PeriodEnd : horizon;
            var hours = (end - start).TotalHours;
            if (hours <= 0)
            {
                continue;
            }

            var pvWatts = Math.Max(0, period.PowerWatts(options.Confidence) * biasFactor);

            // Per slice, not one figure for the whole day: the house has a shape, and charging the car
            // is decided by what the house will draw *then*, not by what it happens to draw now.
            var houseWatts = Math.Max(0, houseLoad.ExpectedWattsAt(start));
            var surplusWatts = pvWatts - houseWatts;

            slices.Add(new Slice
            {
                Start = start,
                End = end,
                Hours = hours,
                SurplusWatts = surplusWatts,
                PvWh = pvWatts * hours,
                HouseWh = houseWatts * hours,
                SurplusWh = Math.Max(0, surplusWatts) * hours,
                IsPlateau = surplusWatts >= options.MinChargePowerWatts,
            });
        }

        return slices;
    }

    // Books the battery's need against the LATEST production first. That is what "100% by evening,
    // not by lunchtime" means: the battery takes the afternoon shoulder and, only if it must, the tail
    // of the plateau — leaving the car the earliest chargeable weather, when it is most likely to be
    // plugged in and when a forecast error still has the rest of the day to correct itself.
    private static void Reserve(List<Slice> slices, double batteryNeedWh)
    {
        var remaining = batteryNeedWh;

        for (var i = slices.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var take = Math.Min(remaining, slices[i].SurplusWh);
            slices[i].ReservedWh = take;
            remaining -= take;
        }
    }

    // The first stretch of chargeable weather long enough to be worth starting a session for. Slices
    // are contiguous by construction (they come from consecutive forecast periods), so a run breaks at
    // the first infeasible slice or a gap in the timeline.
    private static (DateTimeOffset Start, DateTimeOffset End)? FirstViableWindow(
        List<Slice> slices,
        SolarDayPlannerOptions options)
    {
        DateTimeOffset? runStart = null;
        DateTimeOffset runEnd = default;

        bool RunIsViable() => runStart is not null && runEnd - runStart.Value >= options.MinViableWindow;

        foreach (var slice in slices)
        {
            if (!slice.IsFeasible(options))
            {
                if (RunIsViable())
                {
                    return (runStart!.Value, runEnd);
                }

                runStart = null;
                continue;
            }

            // A feasible slice that doesn't butt up against the current run starts a new one — but
            // only after the run it interrupted has had its chance to qualify.
            if (runStart is not null && slice.Start > runEnd)
            {
                if (RunIsViable())
                {
                    return (runStart.Value, runEnd);
                }

                runStart = null;
            }

            runStart ??= slice.Start;
            runEnd = slice.End;
        }

        return RunIsViable() ? (runStart!.Value, runEnd) : null;
    }

    // Classification with hysteresis: a threshold crossed by a rounding wobble must not flip the
    // reported outlook, because the outlook is what notifications and dashboards key off. Leaving a
    // state needs the margin; entering it does not.
    private static DayOutlook Classify(
        double evWantRemainingWh,
        double evExpectedRemainingWh,
        (DateTimeOffset Start, DateTimeOffset End)? window,
        DayOutlook previous,
        SolarDayPlannerOptions options)
    {
        if (evWantRemainingWh <= NegligibleEnergyWh)
        {
            // Nothing left to want: the car has had its target already.
            return DayOutlook.Surplus;
        }

        if (window is null || evExpectedRemainingWh < NegligibleEnergyWh)
        {
            return DayOutlook.NoChargeToday;
        }

        if (evExpectedRemainingWh >= evWantRemainingWh - NegligibleEnergyWh)
        {
            return DayOutlook.Surplus;
        }

        var fraction = evExpectedRemainingWh / evWantRemainingWh;
        var margin = Math.Max(0, options.OutlookHysteresisFraction);

        // Sitting exactly on the boundary (the common case: half the target) is what chattered, so the
        // threshold moves depending on which side we were on last cycle.
        var threshold = previous == DayOutlook.Tight
            ? options.TightOutlookFraction - margin
            : options.TightOutlookFraction + margin;

        return fraction >= threshold ? DayOutlook.Tight : DayOutlook.Shortfall;
    }

    private static string Describe(
        DayOutlook outlook,
        double usableEvWh,
        double shortfallWh,
        double socFloorPercent,
        (DateTimeOffset Start, DateTimeOffset End)? window)
    {
        var windowText = window is null
            ? "no chargeable window"
            : $"window {window.Value.Start.LocalDateTime:HH:mm}-{window.Value.End.LocalDateTime:HH:mm}";

        return outlook switch
        {
            DayOutlook.NoChargeToday =>
                $"No EV charging today ({windowText}); the battery has priority. Short {shortfallWh / 1000:F1}kWh.",
            DayOutlook.Shortfall =>
                $"Shortfall {shortfallWh / 1000:F1}kWh: the car gets {usableEvWh / 1000:F1}kWh, {windowText}, SOC floor {socFloorPercent:F0}%.",
            DayOutlook.Tight =>
                $"Tight day: {usableEvWh / 1000:F1}kWh for the car, {windowText}, SOC floor {socFloorPercent:F0}%.",
            _ =>
                $"{usableEvWh / 1000:F1}kWh available for the car, {windowText}, SOC floor {socFloorPercent:F0}%.",
        };
    }

    // One prorated forecast period. Mutable on purpose: the reservation pass writes back into it.
    private sealed class Slice
    {
        public required DateTimeOffset Start { get; init; }
        public required DateTimeOffset End { get; init; }
        public required double Hours { get; init; }
        public required double SurplusWatts { get; init; }
        public required double PvWh { get; init; }
        public required double HouseWh { get; init; }
        public required double SurplusWh { get; init; }
        public required bool IsPlateau { get; init; }

        /// <summary>How much of this period's surplus the battery has booked.</summary>
        public double ReservedWh { get; set; }

        /// <summary>What is left for the car after the battery's booking.</summary>
        public double AvailableWh => Math.Max(0, SurplusWh - ReservedWh);

        /// <summary>
        /// Whether the car could actually charge in this period: the power left after the battery's
        /// booking must clear the charger's minimum — or come within a loan's reach of it, when the
        /// battery is allowed to bridge the gap.
        /// </summary>
        public bool IsFeasible(SolarDayPlannerOptions options)
        {
            if (Hours <= 0 || AvailableWh <= 0)
            {
                return false;
            }

            var threshold = options.EnableBatteryLoan
                ? options.MinChargePowerWatts - options.MaxLoanPowerWatts
                : options.MinChargePowerWatts;

            return AvailableWh / Hours >= Math.Max(0, threshold);
        }
    }
}
