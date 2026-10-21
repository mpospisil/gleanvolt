using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.RegisterMaps;

/// <summary>
/// The one-register test of whether an address is the device it is supposed to be: the inverter's
/// battery state of charge, or the charger's run mode. <c>/pv-system</c>'s probe and the LAN scan
/// (issue #267) both ask it, so the two cannot disagree about what a box is.
///
/// <para>One register cannot prove what a box is, but it catches the mistake that cost a day once
/// (see <see cref="Gleanvolt.Core.Enums.InverterRegister.BatteryCapacity"/>): the inverter's state of
/// charge read from the charger is a number no battery reports, and the other way round a run mode far
/// outside the charger's range. Both are input registers.</para>
/// </summary>
public static class DeviceRoleCheck
{
    // The charger's RunMode register runs 0-13 (EvChargerStatusMapping); anything past it did not
    // come from a charger.
    private const ushort HighestChargerRunMode = 13;

    /// <summary>The input register that tells <paramref name="role"/> apart.</summary>
    public static ushort Register(PvDeviceRole role) => role == PvDeviceRole.Inverter
        ? InverterRegisterMap.BatteryCapacity.Address
        : EvChargerRegisterMap.RunMode.Address;

    /// <summary>Whether <paramref name="value"/>, read from <see cref="Register"/>, could have come from <paramref name="role"/>.</summary>
    public static bool IsPlausible(PvDeviceRole role, ushort value) => role == PvDeviceRole.Inverter
        ? value <= 100
        : value <= HighestChargerRunMode;
}
