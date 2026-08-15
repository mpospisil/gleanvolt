using Solax.Core.Enums;

namespace Solax.Infrastructure.RegisterMaps;

// The SolaX protocol requires >=1 second between separate Modbus instructions, so we
// read the whole telemetry block we need in a single request rather than one register
// at a time, and pick individual fields out of it by offset.
public static class InverterRegisterMap
{
    public const ushort TelemetryBlockStart = 0x0000;
    public const ushort TelemetryBlockCount = 0x0075; // covers offsets up to GridPowerT (0x0074) inclusive

    public static readonly RegisterDescriptor Powerdc1 =
        new((ushort)InverterRegister.Powerdc1, nameof(Powerdc1), "W");

    public static readonly RegisterDescriptor Powerdc2 =
        new((ushort)InverterRegister.Powerdc2, nameof(Powerdc2), "W");

    public static readonly RegisterDescriptor BatteryPowerCharge1 =
        new((ushort)InverterRegister.BatteryPowerCharge1, nameof(BatteryPowerCharge1), "W");

    /// <summary>Battery state of charge. Raw units are tenths of a percent -- see
    /// <see cref="InverterRegister.BatterySoc"/> for why this register and not the documented one.</summary>
    public static readonly RegisterDescriptor BatterySoc =
        new((ushort)InverterRegister.BatterySoc, nameof(BatterySoc), "0.1%");

    public static readonly RegisterDescriptor FeedinPowerLow =
        new((ushort)InverterRegister.FeedinPowerLow, nameof(FeedinPowerLow), "W");

    public static readonly RegisterDescriptor FeedinPowerHigh =
        new((ushort)InverterRegister.FeedinPowerHigh, nameof(FeedinPowerHigh), "W");

    public static readonly RegisterDescriptor GridPowerR =
        new((ushort)InverterRegister.GridPowerR, nameof(GridPowerR), "W");

    public static readonly RegisterDescriptor GridPowerS =
        new((ushort)InverterRegister.GridPowerS, nameof(GridPowerS), "W");

    public static readonly RegisterDescriptor GridPowerT =
        new((ushort)InverterRegister.GridPowerT, nameof(GridPowerT), "W");

    // --- Control: Holding Registers (function codes 0x03/0x10), writable. ---
    // Note the address space change: everything above is Input Registers. See
    // InverterControlRegister for the "verify against your hardware" warning.

    /// <summary>
    /// Start of the Modbus Power Control block, written as one 13-register command (see
    /// <see cref="PowerControlPayload"/>). Written, never read — the address reports the inverter's
    /// ARM firmware version on read.
    /// </summary>
    public static readonly RegisterDescriptor PowerControlBlockStart =
        new((ushort)InverterControlRegister.PowerControlBlockStart, nameof(PowerControlBlockStart), "");
}
