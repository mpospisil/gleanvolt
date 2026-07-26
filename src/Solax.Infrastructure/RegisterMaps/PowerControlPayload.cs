using Solax.Core.Enums;

namespace Solax.Infrastructure.RegisterMaps;

/// <summary>
/// Encodes the SolaX "Modbus Power Control" command — the 13-register block written in one
/// multi-register write starting at <see cref="InverterControlRegister.PowerControlBlockStart"/>.
/// Pure, so the encoding can be asserted in tests without a device.
///
/// <para>Field order and widths follow the upstream wills106/homeassistant-solax-modbus map
/// (<c>autorepeat_function_remotecontrol_recompute</c>). 32-bit fields are written <b>low word
/// first</b> (the plugin's <c>order32="little"</c>), the same convention the FeedinPower telemetry
/// registers already use in this project.</para>
///
/// <code>
/// offset  field                          width
/// +0      power_control                  u16
/// +1      set_type                       u16
/// +2      active_power                   s32   (negative = push out to the grid connection)
/// +4      reactive_power                 s32
/// +6      duration                       u16   seconds, 0..28800
/// +7      target_soc                     u16   unused, 0
/// +8      target_energy                  u32   unused, 0
/// +10     target_charge_discharge_power  s32   unused, 0
/// +12     timeout                        u16   seconds, 0..28800
/// </code>
/// </summary>
public static class PowerControlPayload
{
    /// <summary>Number of registers in the block.</summary>
    public const int RegisterCount = 13;

    /// <summary>
    /// Hardware ceiling on the <c>duration</c> and <c>timeout</c> fields: both are unsigned 16-bit
    /// seconds, capped by upstream at 28,800 s (8 hours). Taken from the upstream integration, not
    /// measured on this inverter — the firmware may cap it lower.
    /// </summary>
    public const int MaxDurationSeconds = 28_800;

    /// <summary>
    /// The command that arms the hold: drive the grid-connection point to
    /// <paramref name="activePowerWatts"/> for <paramref name="durationSeconds"/>.
    /// </summary>
    public static ushort[] Hold(double activePowerWatts, int durationSeconds) =>
        Build(
            InverterPowerControlMode.EnabledPowerControl,
            (int)Math.Round(activePowerWatts),
            Math.Clamp(durationSeconds, 0, MaxDurationSeconds));

    /// <summary>
    /// The command that releases the hold immediately, without waiting for the duration to run out:
    /// power control off, every target zeroed. The inverter resumes its own work mode.
    /// </summary>
    public static ushort[] Release() =>
        Build(InverterPowerControlMode.Disabled, activePowerWatts: 0, durationSeconds: 0);

    private static ushort[] Build(InverterPowerControlMode mode, int activePowerWatts, int durationSeconds)
    {
        var registers = new ushort[RegisterCount];

        registers[0] = (ushort)mode;
        registers[1] = (ushort)InverterPowerControlSetType.Set;
        WriteInt32(registers, offset: 2, activePowerWatts);
        WriteInt32(registers, offset: 4, value: 0);        // reactive_power: we only control real power
        registers[6] = (ushort)durationSeconds;
        registers[7] = 0;                                  // target_soc: unused by this mode
        WriteInt32(registers, offset: 8, value: 0);        // target_energy: unused by this mode
        WriteInt32(registers, offset: 10, value: 0);       // target_charge_discharge_power: unused
        registers[12] = 0;                                 // timeout: upstream's default for this mode

        return registers;
    }

    // Low word first, matching the plugin's order32="little" for SolaX.
    private static void WriteInt32(ushort[] registers, int offset, int value)
    {
        var raw = unchecked((uint)value);
        registers[offset] = (ushort)(raw & 0xFFFF);
        registers[offset + 1] = (ushort)(raw >> 16);
    }
}
