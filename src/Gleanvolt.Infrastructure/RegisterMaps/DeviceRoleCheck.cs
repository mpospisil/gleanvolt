using Gleanvolt.Core.Models;
using Gleanvolt.Devices.Solax;

namespace Gleanvolt.Infrastructure.RegisterMaps;

/// <summary>
/// The one-register test of whether an address is the device it is supposed to be: the inverter's
/// battery state of charge, or the charger's run mode. <c>/pv-system</c>'s probe and the LAN scan
/// (issue #267) both ask it, so the two cannot disagree about what a box is. The inverter half is the SolaX
/// driver's own probe (<see cref="SolaxInverterDriver.ProbeAsync"/>); this names it for the LAN scan, which
/// reads through its own pacer rather than through a driver.
///
/// <para>One register cannot prove what a box is, but it catches the mistake that cost a day once
/// (see <see cref="InverterRegister.BatteryCapacity"/>): the inverter's state of
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
        ? SolaxInverterDriver.ProbeRegister
        : EvChargerRegisterMap.RunMode.Address;

    /// <summary>Whether <paramref name="value"/>, read from <see cref="Register"/>, could have come from <paramref name="role"/>.</summary>
    public static bool IsPlausible(PvDeviceRole role, ushort value) => role == PvDeviceRole.Inverter
        ? SolaxInverterDriver.IsStateOfCharge(value)
        : value <= HighestChargerRunMode;
}
