using Solax.Core.Enums;

namespace Solax.Infrastructure.RegisterMaps;

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

}
