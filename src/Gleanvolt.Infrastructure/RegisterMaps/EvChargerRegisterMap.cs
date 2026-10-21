using Gleanvolt.Core.Enums;

namespace Gleanvolt.Infrastructure.RegisterMaps;

public static class EvChargerRegisterMap
{
    public static readonly RegisterDescriptor ChargePowerTotal =
        new((ushort)EvChargerRegister.ChargePowerTotal, nameof(ChargePowerTotal), "W");

    public static readonly RegisterDescriptor RunMode =
        new((ushort)EvChargerRegister.RunMode, nameof(RunMode), "enum");

    // Writable control (holding) registers -- addresses UNVERIFIED, see EvChargerRegister.
    public static readonly RegisterDescriptor ChargerUseMode =
        new((ushort)EvChargerRegister.ChargerUseMode, nameof(ChargerUseMode), "enum");

    public static readonly RegisterDescriptor ChargeCurrentSetpoint =
        new((ushort)EvChargerRegister.ChargeCurrentSetpoint, nameof(ChargeCurrentSetpoint), "A");

    /// <summary>
    /// The serial number: 7 holding registers from <c>0x600</c>, 14 ASCII characters, high byte first.
    /// Only the LAN scan reads it (issue #267). From the wills106/homeassistant-solax-modbus
    /// integration (<c>plugin_solax_ev_charger.py</c>, <c>async_determineInverterType</c>), which names
    /// both charger generations from it: <c>C107</c>/<c>C311</c>/<c>C322</c> for the EVC, and
    /// <c>5</c> + model code + power code for the HAC.
    /// </summary>
    public const ushort SerialNumberStart = 0x0600;

    public const ushort SerialNumberCount = 7;
}
