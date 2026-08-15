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
