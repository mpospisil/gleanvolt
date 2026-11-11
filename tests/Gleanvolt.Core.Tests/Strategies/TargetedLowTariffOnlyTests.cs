using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Devices;
using Gleanvolt.Tests;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// <b>Low tariff only</b> on a targeted charge (issue #382): the constraint the request carries, how it
/// becomes forbidden windows for the planner, and the controller waiting through high tariff exactly as
/// Fast does (#274) — sun or not, plan short of time or not.
/// </summary>
public class TargetedLowTariffOnlyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 20, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset DepartBy = Now.AddHours(11);

    private static readonly TargetedChargingController Controller = new(
        new ChargePowerConverter(230, 3),
        new TargetedChargingOptions(
            MinChargingCurrentAmps: 6,
            MaxChargingCurrentAmps: 16,
            CurrentStepAmps: 1,
            ResumeHysteresisWatts: 200,
            MinRunTime: TimeSpan.FromMinutes(10),
            MinPauseTime: TimeSpan.FromMinutes(15),
            CompletionDwell: TimeSpan.FromMinutes(2)));

    private static TargetedChargePlan Plan(
        TargetedChargeStrategy strategy = TargetedChargeStrategy.SolarPlusGrid,
        double requiredWh = 22_000,
        double deliveredWh = 0,
        double paceWatts = 6_000) =>
        new(
            Strategy: strategy,
            Now: Now,
            DepartBy: DepartBy,
            Deadline: DepartBy.AddMinutes(-15),
            RequiredEnergyWh: requiredWh,
            DeliveredEnergyWh: deliveredWh,
            RemainingEnergyWh: Math.Max(0, requiredWh - deliveredWh),
            SolarEnergyWh: 0,
            ForecastSurplusWh: 0,
            RequiredPaceWatts: paceWatts,
            GridEnergyWh: 22_000,
            CeilingEnergyWh: 90_000,
            ExpectedEnergyWh: 22_000,
            ShortfallWh: 0,
            GridStart: Now,
            FeasibleDeparture: null,
            SocFloorPercent: 50,
            BatteryToFullWh: 0,
            Blocks: [],
            ForecastAsOf: Now,
            IsUsable: true,
            TailEnergyWh: 0,
            HoldUntil: null,
            Reason: "test");

    private static ChargingControlInput Input(TargetedChargePlan plan, TariffGate? tariff, double surplusWatts = 0, DateTimeOffset? now = null) =>
        new(
            new EnergyState(now ?? Now, 80, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0, EvChargerStatus.Charging, EvChargerPowerWatts: 0),
            surplusWatts,
            SolaxMode.Fast.At(6),
            Charging: true,
            TargetedPlan: plan,
            EvDrewPower: true,
            LowTariffOnly: tariff);

    private static TariffGate High(TimeSpan untilLow) => new(TariffRate.High, Now + untilLow);

    // -- The controller

    [Fact]
    public void Stands_down_through_a_long_high_tariff_and_says_until_when()
    {
        var decision = Controller.Decide(Input(Plan(), High(TimeSpan.FromHours(2))));

        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.False(decision.SessionComplete);
        Assert.Equal(Now.AddHours(2), decision.WaitUntil);
        Assert.Contains("waiting for low tariff (Targeted, low tariff only)", decision.Reason);
    }

    [Fact]
    public void Waits_through_high_tariff_even_with_sun_to_take()
    {
        // The Fast rule, sun or not: the option pauses the charger, not just the import.
        var decision = Controller.Decide(Input(Plan(paceWatts: 0), High(TimeSpan.FromHours(2)), surplusWatts: 8_000));

        Assert.NotEqual(ChargingControlAction.Charge, decision.Action);
    }

    [Fact]
    public void Waits_through_high_tariff_even_with_no_time_left()
    {
        // The owner chose the tariff over the deadline; the plan reports the shortfall.
        var decision = Controller.Decide(Input(Plan(TargetedChargeStrategy.Maximum), High(TimeSpan.FromHours(2))));

        Assert.NotEqual(ChargingControlAction.Charge, decision.Action);
        Assert.False(decision.SessionComplete);
    }

    [Fact]
    public void Charges_to_the_plan_in_low_tariff_and_without_the_option()
    {
        var low = Controller.Decide(Input(Plan(), new TariffGate(TariffRate.Low, Now.AddHours(8))));
        var none = Controller.Decide(Input(Plan(), tariff: null));

        Assert.Equal(ChargingControlAction.Charge, low.Action);
        Assert.Equal(none.ChargeCurrentAmps, low.ChargeCurrentAmps);
    }

    [Fact]
    public void Still_ends_on_the_target_and_on_the_departure_in_high_tariff()
    {
        var reached = Controller.Decide(Input(Plan(TargetedChargeStrategy.Complete, deliveredWh: 22_000), High(TimeSpan.FromHours(2))));
        var departed = Controller.Decide(Input(Plan(), High(TimeSpan.FromHours(2)), now: DepartBy));

        Assert.True(reached.SessionComplete);
        Assert.Equal(ChargingSessionEndReason.TargetReached, reached.EndsSession);
        Assert.True(departed.SessionComplete);
        Assert.Equal(ChargingSessionEndReason.DeparturePassed, departed.EndsSession);
    }

    // -- The constraint

    private static readonly TariffTimeline Night = new(
    [
        new(new TimeWindow(Now.AddHours(-20), Now.AddHours(2)), TariffRate.High),
        new(new TimeWindow(Now.AddHours(2), Now.AddHours(10)), TariffRate.Low),
        new(new TimeWindow(Now.AddHours(10), Now.AddHours(28)), TariffRate.High),
    ],
        Prices: null);

    [Fact]
    public void Forbids_every_high_tariff_stretch_and_keeps_the_owners_own_windows()
    {
        var own = new TimeWindow(Now.AddHours(3), Now.AddHours(4));
        var constraints = new TargetedChargeConstraints(ForbiddenWindows: [own], LowTariffOnly: true);

        var expanded = constraints.WithHighTariffForbidden(Night);

        Assert.Equal(3, expanded.ForbiddenWindows!.Count);
        Assert.Contains(own, expanded.ForbiddenWindows);
        Assert.True(expanded.IsForbiddenAt(Now.AddHours(1)));
        Assert.False(expanded.IsForbiddenAt(Now.AddHours(5)));
        Assert.True(expanded.IsForbiddenAt(Now.AddHours(11)));
    }

    [Fact]
    public void Forbids_nothing_with_the_option_off_or_no_tariff()
    {
        Assert.Same(TargetedChargeConstraints.SunOnly, TargetedChargeConstraints.SunOnly.WithHighTariffForbidden(Night));
        Assert.Same(TargetedChargeConstraints.LowTariff, TargetedChargeConstraints.LowTariff.WithHighTariffForbidden(null));
    }

    [Fact]
    public void Is_a_constraint_and_sun_only_wins_over_it()
    {
        Assert.False(TargetedChargeConstraints.LowTariff.IsEmpty);
        Assert.Null(TargetedChargeConstraints.Of(sunOnly: false, lowTariffOnly: false));
        Assert.Same(TargetedChargeConstraints.LowTariff, TargetedChargeConstraints.Of(sunOnly: false, lowTariffOnly: true));
        Assert.Same(TargetedChargeConstraints.SunOnly, TargetedChargeConstraints.Of(sunOnly: true, lowTariffOnly: true));
        Assert.False(new TargetedChargeConstraints(MaxGridEnergyWh: 0, LowTariffOnly: true).Normalized().LowTariffOnly);
    }
}
