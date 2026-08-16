using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Splits the EV charger's measured draw across solar, grid and battery.
///
/// <para><b>Nothing measures this.</b> The installation has one meter per flow — PV production, grid
/// import/export, battery power, charger power — and none of them says which electron went where.
/// Electrically the question has no answer: power arriving at the busbar is fungible. So the split is
/// an <em>attribution</em>, and what makes it meaningful is that the rule is fixed, documented, and
/// adds back up to the measured draw.</para>
///
/// <para>The rule, in priority order:</para>
/// <list type="number">
/// <item>PV serves the rest of the house first; what is left is the solar surplus, and the charger may
/// take up to that much of it. This is the same definition the Solar and Forecasted modes decide on
/// (<see cref="EnergyState.SolarSurplusPowerWatts"/>), so the recorded solar share is measured against
/// exactly what the controller was aiming at.</item>
/// <item>Draw beyond the surplus comes from the battery, up to what the battery is actually
/// discharging. Battery before grid, because that is the inverter's own priority: a hybrid inverter
/// serves house load from the pack before it imports.</item>
/// <item>Anything still unaccounted for is grid import.</item>
/// </list>
///
/// <para>Pure and stateless; running totals are the caller's business (see
/// <see cref="EnergyIntegrator"/>).</para>
/// </summary>
public static class ChargingSourceAttribution
{
    /// <summary>
    /// Attributes the charger's draw from the four power readings of one poll. The result always sums
    /// to <paramref name="evChargerPowerWatts"/>, floored at zero — the HAC charger doesn't do V2G, so
    /// a negative reading is noise rather than export.
    /// </summary>
    /// <param name="gridPowerWatts">Positive = importing, negative = exporting.</param>
    /// <param name="batteryPowerWatts">Positive = charging, negative = discharging.</param>
    public static ChargingSourceSplit Split(
        double evChargerPowerWatts,
        double solarPowerWatts,
        double gridPowerWatts,
        double batteryPowerWatts)
    {
        var evPower = Math.Max(0, evChargerPowerWatts);
        if (evPower <= 0)
        {
            return default;
        }

        // Same two identities as EnergyState, restated here so this stays callable from a status
        // snapshot as well as from a raw reading:
        //   OtherLoads = PV + Grid - EV - Battery      (the house minus the car, the pack and the sun)
        //   Surplus    = PV - OtherLoads
        var otherLoads = solarPowerWatts + gridPowerWatts - evPower - batteryPowerWatts;
        var surplus = solarPowerWatts - otherLoads;

        // Clamped into [0, evPower]: a surplus larger than the car is taking doesn't make the solar
        // share exceed the draw, and a negative surplus (the house is already importing) contributes
        // nothing rather than subtracting.
        var fromSolar = Math.Clamp(surplus, 0, evPower);

        var remainder = evPower - fromSolar;
        var discharging = Math.Max(0, -batteryPowerWatts);
        var fromBattery = Math.Min(remainder, discharging);

        // Grid is deliberately the residual rather than a reading of its own, so the three shares
        // reconcile to the draw even when the meter and the charger were sampled a moment apart.
        var fromGrid = remainder - fromBattery;

        return new ChargingSourceSplit(fromSolar, fromGrid, fromBattery);
    }

    /// <summary>Attributes the charger's draw from a telemetry snapshot.</summary>
    public static ChargingSourceSplit Split(EnergyState state) => Split(
        state.EvChargerPowerWatts,
        state.SolarPowerWatts,
        state.GridPowerWatts,
        state.BatteryPowerWatts);

    /// <summary>Attributes the charger's draw from a published status snapshot.</summary>
    public static ChargingSourceSplit Split(ChargeControlStatus status) => Split(
        status.EvChargerPowerWatts,
        status.SolarPowerWatts,
        status.GridPowerWatts,
        status.BatteryPowerWatts);
}
