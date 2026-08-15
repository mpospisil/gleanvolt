using Solax.Core.Enums;

namespace Solax.Core.Models;

public sealed record EnergyState(
    DateTimeOffset Timestamp,
    double BatterySocPercent,
    double BatteryPowerWatts,
    double SolarPowerWatts,
    double GridPowerWatts,
    EvChargerStatus EvChargerStatus,
    double EvChargerPowerWatts,
    // The charger's work/use mode (Fast/ECO/Green/Stop), or null when it couldn't be read (the
    // holding register isn't available on every charger/firmware). Doesn't come from the inverter
    // telemetry block, so it's attached after FromRawRegisters rather than being a raw parameter.
    EvChargerMode? ChargeMode = null,
    // The charger's active current setpoint in amps, or null when it couldn't be read. Same story as
    // ChargeMode: a control holding register, attached after FromRawRegisters.
    int? ChargeCurrentAmps = null)
{
    /// <summary>
    /// Household consumption excluding the EV charger, the battery, and PV -- the "Other Loads"
    /// residual shown in the SolaX Cloud app (positive Grid = importing, positive Battery = charging):
    /// <code>OtherLoads = PV + Grid - EV - Battery</code>
    /// <see cref="GridPowerWatts"/> comes from the grid METER (FeedinPower), the only register that
    /// sees the whole house — not the inverter's AC output, which cannot reveal household load.
    /// </summary>
    public double OtherLoadsPowerWatts
    {
        get
        {
            return SolarPowerWatts + GridPowerWatts - EvChargerPowerWatts - BatteryPowerWatts;
        }
    }

    /// <summary>
    /// Everything the house is consuming, <b>including</b> the EV charger — the whole load the
    /// inverter's grid-connection point has to serve (positive Grid = importing, positive Battery =
    /// charging):
    /// <code>HouseLoad = PV + Grid - Battery</code>
    /// This is <see cref="OtherLoadsPowerWatts"/> plus the EV charger. The battery discharge hold
    /// uses this one rather than the residual, precisely because the EV must be counted as load the
    /// grid may cover.
    /// </summary>
    public double HouseLoadPowerWatts => SolarPowerWatts + GridPowerWatts - BatteryPowerWatts;

    /// <summary>
    /// The solar power available for EV charging: <b>sun production minus household consumption</b>,
    /// where household consumption excludes both battery charging and EV charging
    /// (<see cref="OtherLoadsPowerWatts"/>).
    /// <code>Surplus = Solar - OtherLoads</code>
    /// Anything left over after the house has taken its share is what the car may have — so charging
    /// from it neither imports from the grid nor discharges the battery.
    /// </summary>
    public double SolarSurplusPowerWatts => SolarPowerWatts - OtherLoadsPowerWatts;

    // Per the SolaX Gen4 protocol: the battery power register is signed 16-bit with positive =
    // charging (negative = discharging). FeedinPower is the signed 32-bit grid METER reading (low
    // word first) using SolaX's convention where positive = EXPORT; we negate it so this model's
    // convention is positive Grid = importing. Powerdc1/2 (solar) and EV charge power are unsigned
    // (the HAC charger doesn't support V2G); SOC is an unsigned 0-100 percentage. Total solar power
    // is the sum of both MPPT trackers (matches the "Solar" figure in the SolaX Cloud app).
    public static EnergyState FromRawRegisters(
        DateTimeOffset timestamp,
        ushort batterySocRaw,
        ushort batteryPowerRaw,
        ushort pvPowerDc1Raw,
        ushort pvPowerDc2Raw,
        ushort feedinPowerLowRaw,
        ushort feedinPowerHighRaw,
        ushort evChargerStatusRaw,
        ushort evChargerPowerRaw)
    {
        var feedinPowerWatts = unchecked((int)(((uint)feedinPowerHighRaw << 16) | feedinPowerLowRaw));

        return new EnergyState(
            timestamp,
            batterySocRaw,
            unchecked((short)batteryPowerRaw),
            pvPowerDc1Raw + pvPowerDc2Raw,
            -feedinPowerWatts, // meter reports positive = export; this model uses positive = import
            EvChargerStatusMapping.FromRaw(evChargerStatusRaw),
            evChargerPowerRaw);
    }
}
