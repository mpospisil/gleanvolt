namespace Gleanvolt.Devices.Solax;

// The SolaX EV charger's "use mode" (a writable holding register). The numeric values follow the
// SolaX GEN2 charger protocol as used by the wills106/homeassistant-solax-modbus integration.
//
// !! VERIFY these values and the register address against your specific hardware before letting the
// controller write for real. Writing an incorrect value could put the charger into an unintended
// mode. Two things stand between this and the hardware: ChargeControl:DryRun, which logs the encoded
// value instead of sending it, and the fact that nothing is written until somebody presses a button.
//
// Only Stop and Fast are ever written: they are what SolaxChargerDriver.SetChargingAllowedAsync maps
// false and true to (issue #348). Eco and Green exist here because the charger can be in them, not
// because we put it there; they are read for display only.
public enum SolaxChargerUseMode
{
    Stop = 0,
    Fast = 1,
    Eco = 2,
    Green = 3,
}
