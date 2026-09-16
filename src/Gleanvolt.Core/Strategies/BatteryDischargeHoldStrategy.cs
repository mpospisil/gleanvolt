using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

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
/// <para><b>Except when the pack is full</b> (2026-09-16). "Already producing its maximum" holds only
/// while the pack can take whatever PV the target leaves over. A full pack cannot, so the target caps PV
/// at the reading it was computed from. The pack's standby trickle then lets the next reading land a
/// little lower, the next target follows it down, and PV ratchets to nothing under a clear sky: on
/// 2026-09-16 from 6.1 kW to 115 W in three minutes while the car took 4.2 kW from the grid, and back to
/// 5.9 kW the moment the hold released. 2026-09-12 12:19 shows the same. So while PV reads under the
/// load and the pack is full and not charging, the push reaches past PV by up to a headroom — never past
/// what the forecast expects the roof to be making now, and never past the load. PV the target had
/// capped climbs back by that margin at each write; a sun that really is that weak takes the margin from
/// a pack that is full. Upstream's integration does the same at SOC ≥ 98 % with a fixed 150 W, which is
/// less than this pack's trickle.</para>
///
/// <para>The push never exceeds the house load, so it never exceeds an export limit and never asks for
/// grid import. Outside the full-pack case it is also never more than the PV being produced, which is
/// what makes upstream's minimum-SOC clamp redundant; inside it the pack is full, so that clamp has
/// nothing to protect.</para>
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
    /// A pack taking more than this is still absorbing what the target leaves over, so the target cannot
    /// be what caps PV, and no headroom is needed.
    /// </summary>
    public const double PackAbsorbingWatts = 100;

    /// <summary>
    /// The hold's command for this reading.
    /// </summary>
    /// <param name="state">The poll's telemetry.</param>
    /// <param name="expectedPvWatts">
    /// What the forecast expects the roof to be making at this instant, or null when no forecast covers
    /// it. The headroom never reaches past it, so a dusk or night reading adds nothing; with no forecast
    /// at all there is no headroom, and the pack keeps the hold's plain promise.
    /// </param>
    /// <param name="fullPackSocPercent">The state of charge at or above which the pack counts as full.</param>
    /// <param name="fullPackHeadroomWatts">The most the push may reach past PV while the pack is full; 0 turns it off.</param>
    public static BatteryHoldTarget Target(
        EnergyState state, double? expectedPvWatts, double fullPackSocPercent, double fullPackHeadroomWatts)
    {
        // Clamp both inputs at zero first: a momentary negative PV or house-load reading (sensor
        // noise, night-time self-consumption bookkeeping) would otherwise flip the sign of the target
        // and command an import.
        var pvWatts = Math.Max(0, state.SolarPowerWatts);
        var houseLoadWatts = Math.Max(0, state.HouseLoadPowerWatts);

        var basePushWatts = Math.Min(houseLoadWatts, pvWatts);
        var pushWatts = pvWatts < houseLoadWatts
            ? Math.Min(houseLoadWatts, pvWatts + Headroom(state, pvWatts, expectedPvWatts, fullPackSocPercent, fullPackHeadroomWatts))
            : basePushWatts;

        // Return a plain 0 rather than negating a zero, so "no PV to push" doesn't log as "-0W".
        if (pushWatts <= 0)
        {
            return new BatteryHoldTarget(0, 0);
        }

        var clampedWatts = Math.Min(pushWatts, MaxTargetMagnitudeWatts);
        return new BatteryHoldTarget(-clampedWatts, Math.Max(0, clampedWatts - basePushWatts));
    }

    // How far past PV a full pack lets the push reach: 0 unless the pack is full, not charging, and the
    // forecast expects more sun than is being read.
    private static double Headroom(
        EnergyState state, double pvWatts, double? expectedPvWatts, double fullPackSocPercent, double fullPackHeadroomWatts)
    {
        if (state.BatterySocPercent < fullPackSocPercent
            || state.BatteryPowerWatts > PackAbsorbingWatts
            || expectedPvWatts is not { } expectedWatts)
        {
            return 0;
        }

        return Math.Max(0, Math.Min(fullPackHeadroomWatts, expectedWatts - pvWatts));
    }
}
