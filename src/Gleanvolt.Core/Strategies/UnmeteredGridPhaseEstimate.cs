using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// What the grid meter would have reported had it seen all three phases, for an installation whose meter
/// is blind on one (<see cref="PvSystemInfo.UnmeteredGridPhase"/>).
///
/// <para><b>Why it matters.</b> Every figure the controller decides on is a residual of the grid meter:
/// house load is PV + grid − EV − battery. A meter that reads 0 W on one phase hides everything on it — a
/// third of a three-phase charge, and whatever the inverter exports there. On the reference install that
/// was the inverter's 1.7–2.3 kW on L3 (2026-09-12): the house appeared to use ~90% of the sun on every
/// sunny afternoon, so no solar mode could start the car, and it appeared to <em>generate</em> kilowatts
/// while the car charged, so a running charge took grid it believed was sun.</para>
///
/// <para><b>What is measured and what is not.</b> The blind phase's flow is its house load, plus the car's
/// share, minus the inverter's output on it. The inverter reports its own output per phase and the charger
/// its power, so only the house load on the blind phase is unknown anywhere. It is taken as half of what
/// the two metered phases carry: wrong for any single large load, but bounded by the house, where the
/// uncorrected figure was wrong by the inverter and the charger.</para>
///
/// <para>The car is assumed spread evenly over the phases it charges on, starting from L1 — so a
/// single-phase car is on L1. Pure and side-effect free.</para>
/// </summary>
public static class UnmeteredGridPhaseEstimate
{
    /// <param name="meteredGridWatts">The meter's total, positive = import — which is the other two phases only.</param>
    /// <param name="unmeteredPhase">The phase the meter does not see.</param>
    /// <param name="inverterOutput">The inverter's AC output per phase, positive = into the house.</param>
    /// <param name="evChargerWatts">What the charger reports drawing.</param>
    /// <param name="evPhases">How many phases the car charges on.</param>
    /// <returns>The estimated grid flow across all three phases, positive = import.</returns>
    public static double GridPowerWatts(
        double meteredGridWatts,
        GridPhase unmeteredPhase,
        PhaseWatts inverterOutput,
        double evChargerWatts,
        int evPhases)
    {
        ArgumentNullException.ThrowIfNull(inverterOutput);

        var evOnUnmetered = EvShare(unmeteredPhase, evChargerWatts, evPhases);
        var evOnMetered = Math.Max(0, evChargerWatts) - evOnUnmetered;
        var inverterOnUnmetered = inverterOutput[unmeteredPhase];
        var inverterOnMetered = inverterOutput.Total - inverterOnUnmetered;

        // Floored for the reason OtherLoads is: a residual of several meters can dip below zero on noise,
        // and a house does not generate.
        var houseOnMetered = Math.Max(0, meteredGridWatts + inverterOnMetered - evOnMetered);
        var houseOnUnmetered = houseOnMetered / 2;

        return meteredGridWatts + houseOnUnmetered + evOnUnmetered - inverterOnUnmetered;
    }

    private static double EvShare(GridPhase phase, double evChargerWatts, int evPhases)
    {
        var phases = Math.Clamp(evPhases, 1, 3);
        return (int)phase < phases ? Math.Max(0, evChargerWatts) / phases : 0;
    }
}
