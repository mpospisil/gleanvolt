namespace Solax.Core.Enums;

// Offsets within the Input Register block (Modbus function code 0x04) of the SolaX
// X1/X3 Hybrid G4 inverter. Verified against "Energy Storage Inverter Modbus TCP&RTU
// Communication protocols" V3.21 (SolaX Power). Real-time telemetry lives in Input
// Registers; the similarly-numbered Holding Registers (function code 0x03) are
// configuration/protection parameters and mean something entirely different.
public enum InverterRegister : ushort
{
    Powerdc1 = 0x000A,
    Powerdc2 = 0x000B,
    BatteryPowerCharge1 = 0x0016,
    // !! KNOWN WRONG, and the right answer is not yet known. Read live on 2026-08-15 while the
    // inverter's own display went from ~80% to 60%, this register sat at exactly 31 the whole time.
    // A frozen value cannot be a state of charge, so what this address reports is not SOC -- but
    // nothing better has been found yet, so it stays until something is actually verified.
    //
    // Already ruled out, so nobody repeats the work:
    //   * 0x0025 (=804, constant)  -- looked like 80.4% for one reading; equally frozen over 80
    //                                 minutes. This was briefly shipped and reverted (PR #62).
    //   * 0x0003 (41 -> 31 -> 35)  -- moves, but noisily, and rose 4 points in 5 minutes on an
    //                                 idle battery.
    //   * every other input register 0x0000-0x00FF, at 1x and 0.1x scale -- nothing near the
    //     displayed value. Above 0x00FF the device returns an illegal-address exception.
    //   * unit IDs 0-7 -- all return the identical block, so this is not a wrong-device problem.
    //
    // Note the wider symptom, which may be the actual cause: grid voltage and frequency read live
    // and sane while *every power register reads ~0* -- battery, solar and grid alike -- during a
    // period when the battery was demonstrably discharging. That points at telemetry only partly
    // published by the inverter rather than at a misassigned address, and may need a setting
    // changed on the device itself.
    BatteryCapacity = 0x001C,
    // The grid METER / CT reading (int32, low word first): net power at the utility connection,
    // positive = feeding in (exporting). This is the only register that sees the whole house, so it
    // is what household consumption and the charging surplus are derived from.
    FeedinPowerLow = 0x0046,
    FeedinPowerHigh = 0x0047,

    // !! NOT the grid meter: these report the INVERTER's AC output per phase (verified live -- they
    // track Solar - Battery at ~96.5%, i.e. inverter efficiency). Kept for reference only; do not use
    // them for household load or surplus.
    GridPowerR = 0x006C,
    GridPowerS = 0x0070,
    GridPowerT = 0x0074,
}
