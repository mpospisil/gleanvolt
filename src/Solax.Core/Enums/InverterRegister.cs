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

    // Battery state of charge, in **tenths of a percent** (804 = 80.4%).
    //
    // The protocol document puts "BatCapacity" at 0x001C as a whole percent, and that is what this
    // map used until it was checked against the hardware: with the inverter's own display and SolaX
    // Cloud both reading ~80%, 0x001C read a steady 31 while 0x0025 read a steady 804. A sweep of
    // every readable input register (0x0000-0x00FF; anything above returns an illegal-address
    // exception) found 0x0025 to be the *only* register that can represent ~80% at any sane scale.
    //
    // So this follows the device rather than the document. 0x001C is left out entirely rather than
    // kept under a name that turned out not to describe it -- what it actually reports is unknown,
    // and guessing would be worse than silence.
    //
    // One honest caveat, because it matters to anyone with a different battery. Two readings of 804
    // fit equally well on the reference install:
    //
    //   * state of charge in tenths of a percent      -> 80.4 %
    //   * remaining energy in hundredths of a kWh     -> 8.04 kWh, which is 80.4 % of the 10 kWh
    //                                                    nominal pack this install happens to have
    //
    // They are indistinguishable on a 10 kWh battery -- both reach 1000 when full -- and no reading
    // taken here can separate them. The /10 applied in EnergyState.FromRawRegisters is therefore
    // known-correct for a 10 kWh pack and unverified for any other size. If SOC ever reads
    // proportionally wrong on different hardware, this is the first thing to re-check.
    BatterySoc = 0x0025,
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
