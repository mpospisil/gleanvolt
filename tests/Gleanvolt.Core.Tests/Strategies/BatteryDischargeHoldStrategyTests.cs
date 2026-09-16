using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class BatteryDischargeHoldStrategyTests
{
    private const double FullSocPercent = 95;
    private const double HeadroomWatts = 1000;

    // Builds a state with the given PV, grid and battery figures (positive Grid = importing, positive
    // Battery = charging), from which HouseLoad = PV + Grid - Battery follows.
    private static EnergyState State(
        double solarWatts, double gridWatts, double batteryWatts = 0, double evWatts = 0, double socPercent = 50) =>
        new(DateTimeOffset.UnixEpoch, BatterySocPercent: socPercent, batteryWatts, solarWatts, gridWatts,
            EvChargerStatus.Charging, evWatts);

    private static BatteryHoldTarget Target(EnergyState state, double? expectedPvWatts = null) =>
        BatteryDischargeHoldStrategy.Target(state, expectedPvWatts, FullSocPercent, HeadroomWatts);

    private static double TargetWatts(EnergyState state, double? expectedPvWatts = null) =>
        Target(state, expectedPvWatts).ActivePowerWatts;

    [Fact]
    public void PvCoversTheHouse_PushesOutTheWholeLoad_LeavingTheSurplusForTheBattery()
    {
        // 6 kW PV, 2 kW house load (the other 4 kW is charging the battery).
        var state = State(solarWatts: 6000, gridWatts: 0, batteryWatts: 4000);

        Assert.Equal(2000, state.HouseLoadPowerWatts);
        // Push out exactly the load: PV serves the house, and the 4 kW it doesn't need has nowhere to
        // go but the battery, so surplus charging survives the hold.
        Assert.Equal(-2000, TargetWatts(state));
    }

    [Fact]
    public void PvFallsShort_PushesOutOnlyThePv_SoTheGridCoversTheRest()
    {
        // 1 kW PV against an 8 kW load (EV charging): 7 kW is being imported.
        var state = State(solarWatts: 1000, gridWatts: 7000, evWatts: 7500);

        Assert.Equal(8000, state.HouseLoadPowerWatts);
        // Command only the PV that exists; the inverter cannot make up the rest, so the grid does —
        // never the battery.
        Assert.Equal(-1000, TargetWatts(state, expectedPvWatts: 5000));
    }

    [Fact]
    public void BatteryDischarging_IsCountedAsLoadTheGridShouldCover()
    {
        // The situation the feature exists for: no sun, EV charging, battery discharging 5 kW into it.
        var state = State(solarWatts: 0, gridWatts: 2000, batteryWatts: -5000, evWatts: 7000);

        Assert.Equal(7000, state.HouseLoadPowerWatts);
        // No PV to push, so the target is 0: the inverter contributes nothing and the grid takes over.
        Assert.Equal(0, TargetWatts(state));
    }

    [Fact]
    public void TargetIsNeverPositive_SoTheHoldNeverCommandsAnImport()
    {
        // Exporting heavily with a negative residual load — the target must still not flip sign.
        var state = State(solarWatts: 5000, gridWatts: -6000, batteryWatts: 0);

        Assert.True(state.HouseLoadPowerWatts < 0);
        Assert.Equal(0, TargetWatts(state));
    }

    [Fact]
    public void TargetIsNeverMoreNegativeThanThePvAvailable_WhileThePackCanStillCharge()
    {
        // Whatever the load, the inverter is never asked to push out more than it is producing —
        // which is what makes upstream's minimum-SOC clamp redundant here.
        var state = State(solarWatts: 3000, gridWatts: 9000, evWatts: 11000);

        Assert.Equal(-3000, TargetWatts(state, expectedPvWatts: 6000));
    }

    [Fact]
    public void AbsurdTelemetry_IsClampedToTheSanityCeiling()
    {
        var state = State(solarWatts: 500_000, gridWatts: 500_000);

        Assert.Equal(-BatteryDischargeHoldStrategy.MaxTargetMagnitudeWatts, TargetWatts(state));
    }

    [Fact]
    public void NegativePvReading_IsTreatedAsZeroRatherThanFlippingTheSign()
    {
        var state = State(solarWatts: -50, gridWatts: 1000);

        Assert.Equal(0, TargetWatts(state));
    }

    // 2026-09-16 12:16:08, halfway down the ratchet: the pack at 99% and trickling, PV already capped to
    // 1.6 kW under a 6 kW sun, the car on the grid.
    private static EnergyState FullPackMidRatchet(double socPercent = 99, double batteryWatts = -326) =>
        State(solarWatts: 1618, gridWatts: 2688 - (batteryWatts + 326), batteryWatts: batteryWatts, evWatts: 4217,
            socPercent: socPercent);

    [Fact]
    public void AFullPackThatIsNotCharging_ReachesPastPv_ByTheHeadroom()
    {
        var target = Target(FullPackMidRatchet(), expectedPvWatts: 6000);

        // Room for the PV the target had capped to climb back: 1 kW past the reading, not the reading.
        Assert.Equal(-2618, target.ActivePowerWatts);
        Assert.Equal(1000, target.HeadroomWatts);
    }

    [Fact]
    public void TheHeadroomNeverReachesPastWhatTheForecastExpectsNow()
    {
        var target = Target(FullPackMidRatchet(), expectedPvWatts: 1900);

        Assert.Equal(-1900, target.ActivePowerWatts);
        Assert.Equal(282, target.HeadroomWatts);
    }

    [Fact]
    public void TheHeadroomNeverReachesPastTheHouseLoad()
    {
        // 12:14:04, the first capped reading: 4995W against a 5140W load. Reaching the load is enough.
        var state = State(solarWatts: 4995, gridWatts: -184, batteryWatts: -329, evWatts: 4634, socPercent: 99);

        var target = Target(state, expectedPvWatts: 6000);

        Assert.Equal(-5140, target.ActivePowerWatts);
        Assert.Equal(145, target.HeadroomWatts);
    }

    [Fact]
    public void APackThatCanStillCharge_GetsNoHeadroom()
    {
        // 2026-09-15: the pack at 40% soaks up whatever the target leaves, so PV is never capped and
        // the pack is never asked to lend.
        var target = Target(FullPackMidRatchet(socPercent: 40), expectedPvWatts: 6000);

        Assert.Equal(-1618, target.ActivePowerWatts);
        Assert.Equal(0, target.HeadroomWatts);
    }

    [Fact]
    public void AFullPackStillCharging_GetsNoHeadroom()
    {
        // 97% and still taking 800W: the leftover PV has somewhere to go, so the target caps nothing.
        var target = Target(FullPackMidRatchet(socPercent: 97, batteryWatts: 800), expectedPvWatts: 6000);

        Assert.Equal(-1618, target.ActivePowerWatts);
        Assert.Equal(0, target.HeadroomWatts);
    }

    [Fact]
    public void NoForecastForTheMoment_GetsNoHeadroom()
    {
        var target = Target(FullPackMidRatchet(), expectedPvWatts: null);

        Assert.Equal(-1618, target.ActivePowerWatts);
        Assert.Equal(0, target.HeadroomWatts);
    }

    [Fact]
    public void AtNight_AFullPackLendsNothing()
    {
        // A fast charge on a full pack after dark: no PV and none expected, so the hold stays at 0.
        var state = State(solarWatts: 0, gridWatts: 11_200, batteryWatts: -300, evWatts: 11_000, socPercent: 100);

        Assert.Equal(new BatteryHoldTarget(0, 0), Target(state, expectedPvWatts: 0));
    }

    [Fact]
    public void AtDusk_AFullPackLendsOnlyTheForecastGap()
    {
        var state = State(solarWatts: 80, gridWatts: 11_000, batteryWatts: -300, evWatts: 11_000, socPercent: 100);

        var target = Target(state, expectedPvWatts: 120);

        Assert.Equal(-120, target.ActivePowerWatts);
        Assert.Equal(40, target.HeadroomWatts);
    }

    [Fact]
    public void PvCoveringTheLoad_IsUnchangedByAFullPack()
    {
        var state = State(solarWatts: 6000, gridWatts: -4000, batteryWatts: 0, socPercent: 100);

        Assert.Equal(new BatteryHoldTarget(-2000, 0), Target(state, expectedPvWatts: 6500));
    }

    [Fact]
    public void AZeroHeadroom_TurnsItOff()
    {
        var target = BatteryDischargeHoldStrategy.Target(
            FullPackMidRatchet(), expectedPvWatts: 6000, FullSocPercent, fullPackHeadroomWatts: 0);

        Assert.Equal(new BatteryHoldTarget(-1618, 0), target);
    }

    // The simplest inverter that reproduces 2026-09-16: it pushes out what the target asks, the full
    // pack trickles 326W whatever happens, PV makes up the rest up to what the sun allows, the pack
    // covers anything the sun cannot, and the grid covers the house. Every poll rewrites the target,
    // which the real hold also did here: each move was past the 100W reissue threshold.
    private const double SunWatts = 6000;
    private const double LoadWatts = 4700;
    private const double TrickleWatts = 326;

    private static EnergyState InverterReading(double pushWatts)
    {
        var pvWatts = Math.Clamp(pushWatts - TrickleWatts, 0, SunWatts);
        var batteryWatts = pushWatts > 0 ? -(pushWatts - pvWatts) : 0;
        return State(pvWatts, gridWatts: LoadWatts - pushWatts, batteryWatts, evWatts: 4200, socPercent: 99);
    }

    private static List<double> ReplayPv(EnergyState first, double headroomWatts, int polls)
    {
        var readings = new List<double>();
        var state = first;
        for (var poll = 0; poll < polls; poll++)
        {
            var target = BatteryDischargeHoldStrategy.Target(state, SunWatts, FullSocPercent, headroomWatts);
            state = InverterReading(-target.ActivePowerWatts);
            readings.Add(state.SolarPowerWatts);
        }

        return readings;
    }

    // Before the hold arms: the inverter's own self-use, PV uncapped and the surplus exported.
    private static EnergyState Uncapped() =>
        State(SunWatts, gridWatts: LoadWatts - SunWatts - TrickleWatts, batteryWatts: -TrickleWatts, evWatts: 4200,
            socPercent: 99);

    [Fact]
    public void Replay_WithoutHeadroom_AFullPackRatchetsPvToNothing()
    {
        // The model reproduces the day, so the test below is about the fix and not about the model.
        var pv = ReplayPv(Uncapped(), headroomWatts: 0, polls: 20);

        Assert.True(pv[^1] < 200, $"PV ended at {pv[^1]:F0}W");
        Assert.True(pv.Zip(pv.Skip(1)).All(pair => pair.Second <= pair.First), "PV should only fall");
    }

    [Fact]
    public void Replay_WithHeadroom_PvStaysWithTheLoad()
    {
        var pv = ReplayPv(Uncapped(), HeadroomWatts, polls: 20);

        // The load, less the pack's own trickle: the car and the house run on the sun, not on the grid.
        Assert.All(pv, reading => Assert.True(reading >= LoadWatts - TrickleWatts - 1, $"PV fell to {reading:F0}W"));
    }

    [Fact]
    public void Replay_WithHeadroom_PvAlreadyCappedClimbsBack()
    {
        // Where the day was when SolarGrid paused: 115W read under a 6 kW sun.
        var collapsed = InverterReading(pushWatts: 115 + TrickleWatts);

        var pv = ReplayPv(collapsed, HeadroomWatts, polls: 10);

        Assert.True(pv[^1] >= LoadWatts - TrickleWatts - 1, $"PV reached only {pv[^1]:F0}W");
    }
}
