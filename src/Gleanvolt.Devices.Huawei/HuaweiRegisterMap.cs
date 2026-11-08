namespace Gleanvolt.Devices.Huawei;

/// <summary>
/// The SUN2000 hybrid's registers the driver reads (issue #320), from Huawei's <i>Solar Inverter Modbus
/// Interface Definitions</i>, cross-checked against evcc's <c>huawei-sun2000-hybrid.yaml</c> (MIT). Every
/// one is a holding register, and every 32-bit value is <b>high word first</b>.
/// </summary>
public static class HuaweiRegisterMap
{
    /// <summary>Model name, ASCII, 15 registers (30 characters, NUL-padded): <c>SUN2000-10KTL-M1</c>.</summary>
    public const ushort ModelName = 30000;

    /// <summary>How many registers <see cref="ModelName"/> takes.</summary>
    public const ushort ModelNameCount = 15;

    /// <summary>Serial number, ASCII, 10 registers.</summary>
    public const ushort SerialNumber = 30015;

    /// <summary>How many registers <see cref="SerialNumber"/> takes.</summary>
    public const ushort SerialNumberCount = 10;

    /// <summary>
    /// Input power: all MPPT inputs together, int32, in watts (kW with a gain of 1000). The whole PV array,
    /// so no "strings 1 and 2 only" caveat as on SolaX.
    /// </summary>
    public const ushort InputPower = 32064;

    /// <summary>
    /// The power meter's active power, int32, watts: the total of all phases on a three-phase meter
    /// (DTSU666-H), the one phase on a single-phase meter (DDSU666-H). Huawei counts <b>export as positive</b> (feed-in &gt; 0,
    /// supply from the grid &lt; 0). To be confirmed by a tester's dump (#325).
    /// </summary>
    public const ushort MeterActivePower = 37113;

    /// <summary>The battery's state of charge, uint16, in tenths of a percent.</summary>
    public const ushort BatterySoc = 37760;

    /// <summary>
    /// The battery's charge/discharge power, int32, watts, <b>charging positive</b>. Several LUNA2000 towers
    /// report as one. To be confirmed by a tester's dump (#325).
    /// </summary>
    public const ushort BatteryPower = 37765;

    /// <summary>The battery block one poll reads: from <see cref="BatterySoc"/> through <see cref="BatteryPower"/>.</summary>
    public const ushort BatteryBlockStart = BatterySoc;

    /// <summary>How many registers the battery block takes.</summary>
    public const ushort BatteryBlockCount = BatteryPower + 2 - BatterySoc;

    /// <summary>
    /// The battery's maximum charging power, uint32, watts. Read beside <see cref="MaxDischargePower"/> by a
    /// snapshot only.
    /// </summary>
    public const ushort MaxChargePower = 47075;

    /// <summary>
    /// The battery's maximum discharging power, uint32, watts, read and written (issue #323). Zero holds the
    /// battery out of discharge. <b>A lasting setting</b>, not a command that lapses: it stays as written
    /// until something writes it again, through a restart of Gleanvolt or of the inverter.
    /// </summary>
    public const ushort MaxDischargePower = 47077;

    /// <summary>
    /// The EMMA's own unit id (issue #322): it answers for itself here, and forwards the inverter's unit id
    /// to the inverter.
    /// </summary>
    public const byte EmmaUnitId = 0;

    /// <summary>
    /// The EMMA's built-in sensor's active power at the grid connection, int32, watts, from evcc's
    /// <c>huawei-emma.yaml</c> (MIT). Taken to count export as positive like the inverter's meter; to be
    /// confirmed by a tester's dump (#325).
    /// </summary>
    public const ushort EmmaGridActivePower = 31657;

    /// <summary>The EMMA's grid block a snapshot reads: the total, then phases A, B and C (31665/67/69), int32 each.</summary>
    public const ushort EmmaGridBlockCount = 31671 - EmmaGridActivePower;
}
