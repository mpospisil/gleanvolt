namespace Gleanvolt.Core.Enums;

// The SolaX EV charger's "use mode" (a writable holding register). The numeric values follow the
// SolaX GEN2 charger protocol as used by the wills106/homeassistant-solax-modbus integration.
//
// !! VERIFY these values and the register address against your specific hardware before enabling
// control writes (ChargeControl:Enabled). Writing an incorrect value could put the charger into an
// unintended mode. Control is disabled by default precisely because of this.
public enum EvChargerMode
{
    Stop = 0,
    Fast = 1,
    Eco = 2,
    Green = 3,
}
