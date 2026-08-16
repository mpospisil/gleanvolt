namespace Gleanvolt.Core.Enums;

// Holding Registers (Modbus function codes 0x03/0x10) of the SolaX X1/X3 Hybrid G4/G5 inverter.
// A DIFFERENT address space from InverterRegister, which is Input Registers (0x04) -- the same
// numbers there are real-time telemetry and mean something entirely different.
//
// !! VERIFY AGAINST YOUR HARDWARE before enabling BatteryHold:Enabled. Addresses and the field
// layout are taken from the wills106/homeassistant-solax-modbus map (plugin_solax.py, the
// "remotecontrol_trigger" button at register 0x7C), not from a SolaX document we hold, and upstream
// reports behaviour differing across GEN/firmware versions.
public enum InverterControlRegister : ushort
{
    // Start of the "Modbus Power Control" block: a single 13-register multi-write (see
    // PowerControlPayload). It is a COMMAND, not a stored setting -- nothing is written to EEPROM,
    // and upstream states these can be issued as often as desired.
    //
    // WRITE-ONLY for our purposes: reading holding register 0x7C back does NOT return the command
    // state. Upstream reads 0x7B/0x7C as the inverter's full DSP/ARM firmware version, so the
    // address is overloaded and there is no way to read the active power-control mode. See
    // docs/DECISIONS.md.
    PowerControlBlockStart = 0x007C,
}
