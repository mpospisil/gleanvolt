using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// The safety argument for lending down to the plan's floor, run rather than asserted (issue #223).
///
/// <para>The claim the change rests on is that no new machinery is needed to repay a loan: lending
/// drops SOC, which grows <c>BatteryToFullWh</c> on the next poll, which books more of the late
/// production for the pack, which raises the floor, which squeezes the car out earlier. The loop is
/// already there and already self-correcting. This drives the real planner and the real controller
/// through a whole day at the poll cadence and checks the pack still ends it full.</para>
///
/// <para>Deliberately a simulation and not a unit test of one decision: every individual rule here was
/// already covered, and the thing that was never checked is what they do to each other over six
/// hours.</para>
/// </summary>
public class BatteryLoanDaySimulationTests
{
    private const double CapacityWh = 9000;
    private const double ChargeEfficiency = 0.95;
    private const double HouseWatts = 500;
    private const double WattsPerAmp = 690;
    private const double MinChargePowerWatts = 6 * WattsPerAmp;

    private static readonly TimeSpan Step = TimeSpan.FromSeconds(300);
    private static readonly DateTimeOffset Start = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Dusk = new(2026, 7, 27, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Deadline = new(2026, 7, 27, 19, 0, 0, TimeSpan.Zero);

    private sealed class FlatHouseLoad(double watts) : IHouseLoadProfile
    {
        public double ExpectedWattsAt(DateTimeOffset instant) => watts;
    }

    private sealed class NeverCharges : IChargingController
    {
        public ChargingControlDecision Decide(ChargingControlInput input) =>
            new(ChargingControlAction.Pause, null, "no plan");
    }

    // A thin overcast afternoon: 2kW on the roof against a 500W house is 1.5kW of surplus, which is a
    // third of the charger's three-phase floor and every watt of it export while the pack is full.
    private static SolarForecast Overcast(double pvWatts = 2000)
    {
        var periods = new List<SolarForecastPeriod>();
        for (var at = Start.AddMinutes(30); at <= Dusk; at = at.AddMinutes(30))
        {
            periods.Add(new SolarForecastPeriod(at, TimeSpan.FromMinutes(30), pvWatts, EstimatedPowerWattsP10: pvWatts));
        }

        return new SolarForecast(Start.AddHours(-1), periods);
    }

    private static SolarDayPlannerOptions PlannerOptions() => new(
        BatteryCapacityWh: CapacityWh,
        ChargeEfficiency: ChargeEfficiency,
        MinChargePowerWatts: MinChargePowerWatts,
        MaxLoanPowerWatts: 3800,
        EnableBatteryLoan: true,
        MinBridgeSurplusWatts: 2000,
        SpillBridgeSurplusWatts: 400,
        MinViableWindow: TimeSpan.FromMinutes(30),
        MinBatterySocFloorPercent: 50);

    private static ForecastedChargingOptions ControllerOptions() => new(
        MinChargingCurrentAmps: 6,
        MaxChargingCurrentAmps: 16,
        CurrentStepAmps: 1,
        ResumeHysteresisWatts: 200,
        FloorResumeMarginPercent: 5,
        FloorGuardReserveWatts: 750,
        EnableBatteryLoan: true,
        MaxLoanPowerWatts: 3800,
        MinBridgeSurplusWatts: 2000,
        SpillBridgeSurplusWatts: 400,
        MaxDailyLoanWh: 4000,
        LoanSocMarginPercent: 2,
        MinRunTime: TimeSpan.FromMinutes(10),
        MinPauseTime: TimeSpan.FromMinutes(15),
        FinalGuardBefore: TimeSpan.FromHours(1),
        SessionEnergyTargetWh: 0);

    private sealed record DayResult(
        double EndSocPercent,
        double MinSocPercent,
        double WorstFloorBreachPercent,
        double EvEnergyWh,
        double LentWh,
        double MaxLoanWatts);

    /// <summary>
    /// Runs the day at the poll cadence: plan, decide, then move the energy the decision implies. The
    /// pack takes whatever the sun makes and the car does not, and funds whatever the car takes beyond
    /// it — which is the loan, whether or not anybody called it one.
    /// </summary>
    private static DayResult RunDay(double startSocPercent, double pvWatts = 2000)
    {
        var forecast = Overcast(pvWatts);
        var houseLoad = new FlatHouseLoad(HouseWatts);
        var controller = new ForecastedChargingController(
            new ChargePowerConverter(230, 3), new NeverCharges(), ControllerOptions());
        var ledger = new BatteryLoanLedger();

        var soc = startSocPercent;
        var charging = false;
        var stateChangedAt = Start;
        var minSoc = soc;
        var worstBreach = 0.0;
        var evWh = 0.0;
        var maxLoan = 0.0;

        for (var now = Start; now < Deadline; now = now.Add(Step))
        {
            // The live surplus is the forecast's own figure, so the plan and the moment agree — the
            // question here is what the rules do to each other, not what a forecast error does to them.
            var livePvWatts = now < Dusk ? pvWatts : 0;
            var surplusWatts = livePvWatts - HouseWatts;

            var state = new EnergyState(
                now, soc, BatteryPowerWatts: 0, SolarPowerWatts: livePvWatts, GridPowerWatts: 0,
                EvChargerStatus.Charging, EvChargerPowerWatts: 0);

            var plan = SolarDayPlanner.Plan(state, forecast, Deadline, houseLoad, 1.0, PlannerOptions());

            var decision = controller.Decide(new ChargingControlInput(
                state,
                surplusWatts,
                new EvChargerSettings(EvChargerMode.Fast, 6),
                charging,
                plan,
                TimeInCurrentState: now - stateChangedAt,
                LoanedTodayWh: ledger.LentWattHours,
                ChargedThisMode: evWh > 0,
                LoanOutstandingWh: ledger.OutstandingWattHours));

            var wasCharging = charging;
            charging = decision.Action == ChargingControlAction.Charge;
            if (charging != wasCharging)
            {
                stateChangedAt = now;
            }

            var evWatts = charging ? WattsPerAmp * decision.ChargeCurrentAmps!.Value : 0;
            var batteryWatts = surplusWatts - evWatts;
            var hours = Step.TotalHours;

            evWh += evWatts * hours;
            maxLoan = Math.Max(maxLoan, decision.LoanPowerWatts);
            ledger.Add(now, decision.LoanPowerWatts, batteryWatts);

            worstBreach = Math.Max(worstBreach, plan.RequiredSocFloorPercent - soc);

            // Charging into the pack pays the conversion loss; discharging out of it does not, which is
            // the asymmetry the floor's own arithmetic assumes.
            soc += batteryWatts >= 0
                ? batteryWatts * ChargeEfficiency * hours / CapacityWh * 100
                : batteryWatts * hours / CapacityWh * 100;
            soc = Math.Clamp(soc, 0, 100);
            minSoc = Math.Min(minSoc, soc);
        }

        return new DayResult(soc, minSoc, worstBreach, evWh, ledger.LentWattHours, maxLoan);
    }

    [Fact]
    public void AFullPackLendsIntoAThinAfternoonAndStillEndsTheDayFull()
    {
        var day = RunDay(startSocPercent: 100);

        // The point of the exercise: a day that used to deliver nothing at all -- 1.5kW is under the
        // charger's floor, under the 2kW a loan is normally granted from, and the plan's own budget for
        // it was zero -- now puts kilowatt-hours into the car.
        Assert.True(day.EvEnergyWh > 3000, $"the car should have been charged; it took {day.EvEnergyWh:F0}Wh");
        Assert.True(day.LentWh > 1000, $"the pack should have lent something; it lent {day.LentWh:F0}Wh");

        // ...and the pack is still where the owner's condition says it must be. The tolerance is the
        // dwell timer's doing, not the floor's: a soft breach of the clamp holds the session at 6A for
        // MinRunTime before it stops, so the floor can be undershot by up to that much lending and the
        // day has to make it good afterwards. Anything beyond it would mean the loop does not converge.
        var dwellLendingPercent = day.MaxLoanWatts
            * (ControllerOptions().MinRunTime + Step).TotalHours / CapacityWh * 100;

        Assert.True(
            day.EndSocPercent >= 100 - dwellLendingPercent,
            $"the pack ended the day at {day.EndSocPercent:F1}%, more than {dwellLendingPercent:F1}% short of full");

        // The owner's clamp is the other half of the guarantee, and it is not a soft one: the pack must
        // never be lent down through it beyond the same dwell allowance.
        Assert.True(
            day.MinSocPercent >= 50 - dwellLendingPercent,
            $"the pack reached {day.MinSocPercent:F1}%, below the 50% clamp");

        // The same statement at every poll rather than only at the two extremes: the floor in force was
        // never undershot by more than the dwell timer is entitled to hold for.
        Assert.True(
            day.WorstFloorBreachPercent <= dwellLendingPercent,
            $"the floor was breached by {day.WorstFloorBreachPercent:F1}%, more than the {dwellLendingPercent:F1}% the dwell allows");
    }

    // The acceptance criterion behind BatteryLoanRules: a window the plan draws must be a window the
    // controller enters. The two figures used to be computed separately -- 1.64kW in the planner,
    // 2kW in the controller -- so the forecast page drew a band of weather the charger refused.
    [Fact]
    public void AWindowThePlanDrawsIsOneTheControllerWillEnter()
    {
        var state = new EnergyState(
            Start, 100, BatteryPowerWatts: 0, SolarPowerWatts: 2000, GridPowerWatts: 0,
            EvChargerStatus.Charging, EvChargerPowerWatts: 0);

        var plan = SolarDayPlanner.Plan(
            state, Overcast(), Deadline, new FlatHouseLoad(HouseWatts), 1.0, PlannerOptions());

        Assert.NotNull(plan.NextFeasibleWindow);

        // The same weather the window was drawn from, offered to the controller as live surplus, with the
        // session paused -- the hardest of the two gates, and the one that was closed outright.
        var decision = new ForecastedChargingController(
                new ChargePowerConverter(230, 3), new NeverCharges(), ControllerOptions())
            .Decide(new ChargingControlInput(
                state,
                2000 - HouseWatts,
                new EvChargerSettings(EvChargerMode.Fast, 6),
                Charging: false,
                plan,
                TimeInCurrentState: TimeSpan.FromHours(1),
                ChargedThisMode: true));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.True(decision.LoanPowerWatts > 0);
    }

    [Fact]
    public void APackThatCanAbsorbTheWholeDayLendsNothing()
    {
        // 700W of surplus for six hours, against a pack 45% short of full: it all has a home, so nothing
        // spills, the thin-surplus rules never open and the afternoon is the battery's. The behaviour that
        // was right before the change, preserved where it was right.
        //
        // Note that a *full* pack spills on the 2kW day above even at 70%: the spill is about what the
        // pack can absorb by the deadline, not about SOC, which is the whole point of computing it.
        var day = RunDay(startSocPercent: 55, pvWatts: 1200);

        Assert.Equal(0, day.LentWh, 1);
        Assert.Equal(0, day.EvEnergyWh, 1);
    }
}
