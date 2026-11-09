namespace Gleanvolt.Devices.Huawei;

/// <summary>
/// The Huawei SCharger-7KS-S0 and -22KT-S0 on their own Modbus TCP server (issue #351), from Huawei's
/// <i>Northbound Modbus-TCP Interconnection Protocol for AC Chargers</i>, V1.0 (2023-08-04). That document is the
/// whole map. Every value is a holding register, read with function code 0x03, and 32-bit values are high
/// word first.
///
/// <para>What it does not have matters as much: no status, no model and no serial number. The EMMA's own
/// interface (SmartHEMS V100R024C10SPC111, §3.3) gives the charger's model and energy but no power, no status
/// and no control, so it is no way around this.</para>
/// </summary>
public static class SChargerRegisterMap
{
    /// <summary>Phase L1's output voltage: U32, volts × 10. L2 and L3 follow at +2 and +4.</summary>
    public const ushort PhaseL1Voltage = 0x1000;

    /// <summary>Phase L1's output current: U32, amps × 10. L2 and L3 follow at +2 and +4.</summary>
    public const ushort PhaseL1Current = 0x1006;

    /// <summary>Total output power: U32, kilowatts × 10, so in steps of 100 W.</summary>
    public const ushort TotalOutputPower = 0x100C;

    /// <summary>The block the driver reads in one request: the three voltages, three currents and the total power.</summary>
    public const ushort MeasurementsStart = PhaseL1Voltage;

    /// <summary>How many registers <see cref="MeasurementsStart"/> covers.</summary>
    public const ushort MeasurementsCount = 14;

    /// <summary>Maximum charge power, read and write: U32, kilowatts × 10, 0–22, default 22.</summary>
    public const ushort MaxChargePower = 0x2000;

    /// <summary>Charging control, read and write: U16, see <see cref="SChargerControl"/>.</summary>
    public const ushort ChargingControl = 0x2006;
}

/// <summary>The values of <see cref="SChargerRegisterMap.ChargingControl"/>, as the protocol names them.</summary>
public enum SChargerControl : ushort
{
    /// <summary>"On standby": the default, and what the register reads before anyone has written it.</summary>
    Standby = 0,

    /// <summary>"Charge paused": the driver's stop.</summary>
    Paused = 1,

    /// <summary>"Charge resumed": the driver's start.</summary>
    Resumed = 2,
}
