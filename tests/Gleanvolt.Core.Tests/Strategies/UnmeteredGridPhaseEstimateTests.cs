using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// The grid flow of a meter blind on one phase. The first two cases are the reference install's own
/// readings: the afternoon the house appeared to use all the sun, and the night it appeared to generate.
/// </summary>
public class UnmeteredGridPhaseEstimateTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 12, 11, 32, 14, TimeSpan.Zero);

    [Fact]
    public void Restores_the_export_the_blind_phase_hides()
    {
        // 2026-09-12 13:32, car paused, battery full. The meter's L3 read 0 W while the inverter put
        // 1735 W there, so the meter showed 1031 W of export and the house "used" 2.2 kW of 3.2 kW PV.
        var grid = UnmeteredGridPhaseEstimate.GridPowerWatts(
            meteredGridWatts: -1031,
            GridPhase.L3,
            new PhaseWatts(459, 888, 1735),
            evChargerWatts: 0,
            evPhases: 3);

        Assert.Equal(-2608, grid, 3);

        var state = new EnergyState(At, 99, 0, SolarPowerWatts: 3236, grid, EvChargerStatus.ChargePaused, 0);
        Assert.Equal(628, state.OtherLoadsPowerWatts, 3);
    }

    [Fact]
    public void A_three_phase_charge_no_longer_makes_the_house_a_generator()
    {
        // 2026-08-27, after dark: 623 W of house on the metered phases, then a 10.8 kW charge of which the
        // meter saw two thirds. Uncorrected, the house read -3.4 kW for the length of the session.
        var metered = 623 + 10_800 * 2.0 / 3;

        var grid = UnmeteredGridPhaseEstimate.GridPowerWatts(
            metered, GridPhase.L3, new PhaseWatts(0, 0, 0), evChargerWatts: 10_800, evPhases: 3);

        var state = new EnergyState(At, 50, 0, 0, grid, EvChargerStatus.Charging, 10_800);
        Assert.Equal(623 * 1.5, state.OtherLoadsPowerWatts, 3);
    }

    [Fact]
    public void A_single_phase_car_on_the_blind_phase_is_counted_whole()
    {
        // Single-phase cars charge on L1; with L1 blind the meter saw none of it.
        var grid = UnmeteredGridPhaseEstimate.GridPowerWatts(
            meteredGridWatts: 400, GridPhase.L1, new PhaseWatts(0, 0, 0), evChargerWatts: 3_680, evPhases: 1);

        Assert.Equal(400 + 200 + 3_680, grid, 3);
    }

    [Fact]
    public void A_single_phase_car_off_the_blind_phase_adds_nothing_for_the_car()
    {
        var grid = UnmeteredGridPhaseEstimate.GridPowerWatts(
            meteredGridWatts: 400 + 3_680, GridPhase.L3, new PhaseWatts(0, 0, 0), evChargerWatts: 3_680, evPhases: 1);

        Assert.Equal(400 + 3_680 + 200, grid, 3);
    }

    [Fact]
    public void The_phase_named_is_the_one_estimated()
    {
        var output = new PhaseWatts(2_000, 300, 300);

        var l1Blind = UnmeteredGridPhaseEstimate.GridPowerWatts(-400, GridPhase.L1, output, 0, 3);
        var l3Blind = UnmeteredGridPhaseEstimate.GridPowerWatts(-400, GridPhase.L3, output, 0, 3);

        // L1 blind: metered phases carry 600 W of inverter, so 200 W of house there, 100 W assumed on L1,
        // and L1 exports 1900 W. L3 blind: 2300 W of inverter against 400 W export is 1900 W of house.
        Assert.Equal(-400 + 100 - 2_000, l1Blind, 3);
        Assert.Equal(-400 + 950 - 300, l3Blind, 3);
    }

    [Fact]
    public void Noise_below_zero_on_the_metered_phases_adds_no_house_load_to_the_blind_one()
    {
        var grid = UnmeteredGridPhaseEstimate.GridPowerWatts(
            meteredGridWatts: -1_500, GridPhase.L3, new PhaseWatts(700, 700, 2_000), evChargerWatts: 0, evPhases: 3);

        Assert.Equal(-1_500 - 2_000, grid, 3);
    }
}
