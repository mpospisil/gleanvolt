namespace Gleanvolt.Core.Enums;

// Register map for the SolaX "GEN2" EV charger, cross-referenced with the
// wills106/homeassistant-solax-modbus integration (plugin_solax_ev_charger.py).
public enum EvChargerRegister : ushort
{
    // --- Telemetry: Input Registers (Modbus function code 0x04), read-only. Verified. ---
    ChargePowerTotal = 0x000B,
    RunMode = 0x001D,

    // --- Control: Holding Registers (function codes 0x03/0x06/0x10), writable. ---
    // Addresses/encodings from the SolaX X1/X3-HAC Modbus protocol and the wills106
    // homeassistant-solax-modbus map. STILL confirm against your GEN/firmware before letting the
    // controller write for real (ChargeControl:DryRun logs every intended write and its encoded
    // value instead): GEN1 uses Datahub Charge Current 0x624 and some GEN2 units expose EVSE Mode
    // 0x669 {0:Fast,1:ECO,2:Green} instead of Charger Use Mode.
    //
    // Both of these are now WRITTEN. ChargerUseMode was read-only here until #89, when starting and
    // stopping charging became an action that owns it, so its write path has the least mileage of
    // anything in this file.
    ChargerUseMode = 0x060D,        // 0=Stop, 1=Fast, 2=ECO, 3=Green (see EvChargerMode)
    ChargeCurrentSetpoint = 0x0628, // target charging current; scale 0.01A (value = amps*100), range 6-32A
}
