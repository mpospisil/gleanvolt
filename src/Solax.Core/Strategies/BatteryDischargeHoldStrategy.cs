using Solax.Core.Models;

namespace Solax.Core.Strategies;

/// <summary>
/// Computes the <c>active_power</c> target that makes the inverter stop discharging the battery while
/// leaving it free to keep charging from PV surplus — the "battery discharge hold" of issue #20.
///
/// <para>The inverter has no "no discharge" mode. What it has is a remote-control command that drives
/// its grid-connection point to a commanded power target (negative = push out, positive = pull in).
/// Commanding it to push out exactly <c>min(house load, PV)</c> gives us what we want:</para>
/// <list type="bullet">
/// <item><description><b>PV covers the house</b> (<c>PV ≥ load</c>): push out the whole load. The house
/// runs on sun, and everything PV makes beyond that has nowhere to go but the battery — so surplus
/// charging is preserved rather than blocked.</description></item>
/// <item><description><b>PV falls short</b> (<c>PV &lt; load</c>): push out all the PV there is. The
/// inverter is already producing its maximum, so the shortfall can only come from the grid — the
/// battery is never asked to contribute.</description></item>
/// </list>
///
/// <para>Because the target is always in <c>[-PV, 0]</c>, the guards upstream applies around this
/// formula are satisfied for free: it never exceeds an export limit (its magnitude is at most the
/// house load), it never asks for grid import, and the minimum-SOC clamp (<c>≥ -PV</c>) is already
/// met — so none of them are duplicated here.</para>
///
/// <para>Pure and stateless: the target is recomputed from each telemetry reading, because both
/// house load and PV move continuously. That is inherent to the mechanism — see docs/DECISIONS.md
/// for why this is not the "one write per 8 hours" switch the issue originally described.</para>
/// </summary>
public static class BatteryDischargeHoldStrategy
{
    /// <summary>
    /// Sanity ceiling on the magnitude of the commanded target, in watts. The target is derived from
    /// live telemetry, so a corrupt reading could otherwise command something absurd; no domestic
    /// SolaX hybrid is anywhere near 30 kW. Matches the range upstream allows for this field.
    /// </summary>
    public const double MaxTargetMagnitudeWatts = 30_000;

    /// <summary>
    /// The <c>active_power</c> target for the hold, in watts. Always ≤ 0 (negative = the inverter
    /// pushes power out to the grid-connection point) and never more negative than the PV currently
    /// being produced.
    /// </summary>
    public static double ActivePowerTargetWatts(EnergyState state)
    {
        // Clamp both inputs at zero first: a momentary negative PV or house-load reading (sensor
        // noise, night-time self-consumption bookkeeping) would otherwise flip the sign of the target
        // and command an import.
        var pvWatts = Math.Max(0, state.SolarPowerWatts);
        var houseLoadWatts = Math.Max(0, state.HouseLoadPowerWatts);

        var pushWatts = Math.Min(houseLoadWatts, pvWatts);

        // Return a plain 0 rather than negating a zero, so "no PV to push" doesn't log as "-0W".
        return pushWatts <= 0 ? 0 : -Math.Min(pushWatts, MaxTargetMagnitudeWatts);
    }
}
