using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Devices.Huawei;

/// <summary>
/// A Huawei SUN2000 hybrid with a LUNA2000 battery and a Huawei smart meter, reached through the SDongle:
/// three-phase -KTL-M1 and -MAP0 (issue #320), and single-phase -KTL-L1 and -LC0 (issue #321). In the
/// "Expected" tier until a tester's dump confirms the readings; the battery hold (#323) stays untested until
/// a tester's dry-run and then a live run confirm it (#325).
///
/// <para><b>One driver for both phase counts.</b> The single-phase models keep the same registers, and their
/// DDSU666-H meter reports its one phase as the total at <see cref="HuaweiRegisterMap.MeterActivePower"/>,
/// so nothing here asks how many phases there are. Neither kind reports per-phase output the driver uses,
/// so the correction for a meter blind on one phase is unavailable on both.</para>
///
/// <para><b>Two topologies</b> (<see cref="HuaweiSun2000DriverFactory.Topologies"/>). Through the SDongle,
/// everything is the inverter's, on its unit id. Behind an EMMA (issue #322), the grid comes from the EMMA
/// itself on unit <see cref="HuaweiRegisterMap.EmmaUnitId"/>, and PV and the battery from the inverter
/// through it, on the inverter's unit id, which the EMMA assigns. Both on the one connection
/// (<see cref="IModbusClient.ForUnit"/>): the EMMA resets a second client.</para>
///
/// <para><b>The SDongle is slow.</b> It answers "device busy" (<see cref="ModbusDeviceException.IsBusy"/>)
/// when a request arrives while it is still forwarding another, so a busy read is waited out and repeated,
/// not reported as a failure. The spacing between requests and the reconnect backoff are the host
/// client's, from <see cref="HuaweiSun2000DriverFactory.Defaults"/>.</para>
/// </summary>
public sealed class HuaweiSun2000Driver : IInverterDriver
{
    /// <summary>What every SUN2000 model name starts with.</summary>
    public const string ModelPrefix = "SUN2000";

    /// <summary>How long to wait before each repeat of a busy read, in order; one repeat per entry.</summary>
    public static IReadOnlyList<TimeSpan> BusyRetryDelays { get; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private readonly IModbusClient _client;
    private readonly IModbusClient? _emma;
    private readonly ILogger<HuaweiSun2000Driver> _logger;
    private readonly TimeProvider _time;

    /// <param name="client">The inverter's client, owned by the host, on the inverter's unit id.</param>
    /// <param name="loggers">For the driver's log. Null logs nothing.</param>
    /// <param name="time">The clock a busy read waits on. Null is the system clock.</param>
    /// <param name="behindEmma">
    /// Whether the inverter is behind an EMMA, which then answers for the grid on its own unit id.
    /// </param>
    /// <param name="options">Dry-run and the state store the hold keeps its original in. Null uses the defaults.</param>
    public HuaweiSun2000Driver(
        IModbusClient client,
        ILoggerFactory? loggers = null,
        TimeProvider? time = null,
        bool behindEmma = false,
        InverterDriverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        loggers ??= NullLoggerFactory.Instance;
        options ??= new InverterDriverOptions();
        _client = client;
        _emma = behindEmma ? client.ForUnit(HuaweiRegisterMap.EmmaUnitId) : null;
        _logger = loggers.CreateLogger<HuaweiSun2000Driver>();
        _time = time ?? TimeProvider.System;

        // On the inverter's own unit, behind an EMMA too: the setting is the inverter's.
        BatteryHold = new HuaweiBatteryHold(
            client, options.State ?? new InMemoryDriverStateStore(), loggers.CreateLogger<HuaweiBatteryHold>(), options.DryRun, _time);
    }

    /// <summary>Whether the grid is read from an EMMA rather than the inverter's meter.</summary>
    public bool BehindEmma => _emma is not null;

    /// <summary>
    /// The battery hold (<see cref="HuaweiBatteryHold"/>, #323), and no output per phase: the meter's figure
    /// is the whole connection, so the correction for a meter blind on one phase does not apply.
    /// </summary>
    public static InverterCapabilities Sun2000Capabilities { get; } = new(HasBatteryHold: true, HasPhaseOutput: false);

    public InverterCapabilities Capabilities => Sun2000Capabilities;

    public IBatteryDischargeControl BatteryHold { get; }

    /// <summary>
    /// Three reads, one per range the values sit in: PV, the grid (the inverter's meter, or the EMMA's
    /// sensor), the battery.
    /// </summary>
    public async Task<InverterReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var pv = await ReadAsync(HuaweiRegisterMap.InputPower, 2, cancellationToken).ConfigureAwait(false);
        var meter = _emma is { } emma
            ? await ReadAsync(emma, HuaweiRegisterMap.EmmaGridActivePower, 2, cancellationToken).ConfigureAwait(false)
            : await ReadAsync(HuaweiRegisterMap.MeterActivePower, 2, cancellationToken).ConfigureAwait(false);
        var battery = await ReadAsync(HuaweiRegisterMap.BatteryBlockStart, HuaweiRegisterMap.BatteryBlockCount, cancellationToken).ConfigureAwait(false);

        return Decode(pv, meter, battery);
    }

    /// <summary>
    /// The model name, then the state of charge. Recognised when the name is a SUN2000's and the state of
    /// charge is a percentage. A device that keeps nothing at the model name's address is no Huawei
    /// inverter, and its refusal propagates. Behind an EMMA, the EMMA's grid sensor is read first: an
    /// address with no EMMA refuses it, or no inverter answers on the unit id.
    /// </summary>
    public async Task<InverterProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        if (_emma is { } emma)
        {
            await ReadAsync(emma, HuaweiRegisterMap.EmmaGridActivePower, 2, cancellationToken).ConfigureAwait(false);
        }

        var name = await ReadAsync(HuaweiRegisterMap.ModelName, HuaweiRegisterMap.ModelNameCount, cancellationToken).ConfigureAwait(false);
        if (!IsSun2000(DecodeString(name)))
        {
            return new InverterProbeResult(false, name[0]);
        }

        var soc = (await ReadAsync(HuaweiRegisterMap.BatterySoc, 1, cancellationToken).ConfigureAwait(false))[0];
        return soc <= 1000
            ? new InverterProbeResult(true, soc / 10.0)
            : new InverterProbeResult(false, soc);
    }

    /// <summary>Whether <paramref name="modelName"/> is a SUN2000's.</summary>
    public static bool IsSun2000(string modelName) =>
        modelName.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The three ranges a poll reads, in the contract's units and signs. Pure, so the decoding is tested
    /// without a device.
    ///
    /// <para>Input power is the DC input of all MPPTs, in watts; a negative figure (standby draw at night)
    /// is no production and reads as zero. The meter counts export as positive, so it is negated: the
    /// contract is import positive. The battery already counts charging as positive.</para>
    /// </summary>
    /// <param name="pv">The two registers at <see cref="HuaweiRegisterMap.InputPower"/>.</param>
    /// <param name="meter">
    /// The two registers at <see cref="HuaweiRegisterMap.MeterActivePower"/>, or at the EMMA's
    /// <see cref="HuaweiRegisterMap.EmmaGridActivePower"/>: the same encoding and sign.
    /// </param>
    /// <param name="battery">The battery block, from <see cref="HuaweiRegisterMap.BatteryBlockStart"/>.</param>
    public static InverterReading Decode(ushort[] pv, ushort[] meter, ushort[] battery)
    {
        ArgumentNullException.ThrowIfNull(pv);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(battery);

        const int soc = HuaweiRegisterMap.BatterySoc - HuaweiRegisterMap.BatteryBlockStart;
        const int power = HuaweiRegisterMap.BatteryPower - HuaweiRegisterMap.BatteryBlockStart;

        return new InverterReading(
            PvWatts: Math.Max(0, Int32(pv, 0)),
            GridWatts: -(double)Int32(meter, 0),
            BatteryWatts: Int32(battery, power),
            BatterySocPercent: battery[soc] / 10.0,
            InverterOutputPerPhase: null);
    }

    /// <summary>ASCII, two characters to a register, high byte first, up to the first NUL.</summary>
    public static string DecodeString(ushort[] registers)
    {
        ArgumentNullException.ThrowIfNull(registers);

        var bytes = registers.SelectMany(register => new[] { (byte)(register >> 8), (byte)register }).ToArray();
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end).Trim();
    }

    // Signed 32-bit, high word first.
    private static int Int32(ushort[] registers, int offset) =>
        unchecked((int)(((uint)registers[offset] << 16) | registers[offset + 1]));

    private Task<ushort[]> ReadAsync(ushort start, ushort count, CancellationToken cancellationToken) =>
        ReadAsync(_client, start, count, cancellationToken);

    private async Task<ushort[]> ReadAsync(IModbusClient client, ushort start, ushort count, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await client.ReadHoldingRegistersAsync(start, count, cancellationToken).ConfigureAwait(false);
            }
            catch (ModbusDeviceException ex) when (ex.IsBusy && attempt < BusyRetryDelays.Count)
            {
                _logger.LogDebug(
                    "The inverter was busy reading {Start}+{Count}; repeating in {Delay}.", start, count, BusyRetryDelays[attempt]);
                await Task.Delay(BusyRetryDelays[attempt], _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Makes <see cref="HuaweiSun2000Driver"/>s.</summary>
public sealed class HuaweiSun2000DriverFactory : IInverterDriverFactory
{
    /// <summary>The SUN2000's id in configuration.</summary>
    public const string DriverId = "huawei";

    /// <summary>The topology id for the inverter's own SDongle, the default.</summary>
    public const string DongleTopology = "dongle";

    /// <summary>The topology id for an inverter behind an EMMA (issue #322).</summary>
    public const string EmmaTopology = "emma";

    private static readonly IReadOnlyList<InverterTopology> SupportedTopologies =
    [
        new(DongleTopology, "SDongle"),
        new(EmmaTopology, "EMMA"),
    ];

    private readonly ILoggerFactory _loggers;
    private readonly TimeProvider _time;

    public HuaweiSun2000DriverFactory(ILoggerFactory? loggers = null, TimeProvider? time = null)
    {
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string Id => DriverId;

    public string DisplayName => "Huawei";

    public InverterCapabilities Capabilities => HuaweiSun2000Driver.Sun2000Capabilities;

    /// <summary>Through the SDongle (the default), or behind an EMMA.</summary>
    public IReadOnlyList<InverterTopology> Topologies => SupportedTopologies;

    // Port 502 and unit 1, the inverter behind the SDongle. A full second between requests: the dongle
    // forwards one request at a time and answers "busy" to the next, and three reads a poll still fit
    // the 5 s poll interval. After a failed connect, 5 s before the next, doubling: the dongle takes few
    // clients and does not recover while it is being reconnected to every poll. The 5 s request timeout
    // is the host client's default.
    public ModbusDefaults Defaults { get; } = new(
        Port: 502, UnitId: 1, MinRequestInterval: TimeSpan.FromSeconds(1), ReconnectBackoff: TimeSpan.FromSeconds(5));

    // What the driver reads, with the neighbours a tester's dump needs to settle the open questions: the
    // model, serial and part numbers; the inverter's AC figures beside the PV input; the meter's whole
    // block, its per-phase powers included, to confirm its sign; and the battery block. The serial number
    // is removed whole: the model name already says what the device is.
    public RegisterSnapshotSpec Snapshot { get; } = new(
        [
            new RegisterRange(RegisterTable.Holding, HuaweiRegisterMap.ModelName, 35),
            new RegisterRange(RegisterTable.Holding, HuaweiRegisterMap.InputPower, 31),
            new RegisterRange(RegisterTable.Holding, 37100, 39),
            new RegisterRange(RegisterTable.Holding, HuaweiRegisterMap.BatteryBlockStart, HuaweiRegisterMap.BatteryBlockCount),
            new RegisterRange(RegisterTable.Holding, HuaweiRegisterMap.MaxChargePower, 4),
        ],
        SerialNumber: new RegisterRange(RegisterTable.Holding, HuaweiRegisterMap.SerialNumber, HuaweiRegisterMap.SerialNumberCount),
        SerialPrefixLength: 0);

    // Behind an EMMA the inverter has no meter of its own to read; the EMMA's grid block, on its unit, takes
    // the meter block's place. No serial number from the EMMA: only its grid registers are read.
    private static readonly RegisterSnapshotSpec EmmaSnapshot = new(
        [
            new RegisterRange(RegisterTable.Holding, HuaweiRegisterMap.ModelName, 35),
            new RegisterRange(RegisterTable.Holding, HuaweiRegisterMap.InputPower, 31),
            new RegisterRange(RegisterTable.Holding, HuaweiRegisterMap.BatteryBlockStart, HuaweiRegisterMap.BatteryBlockCount),
            new RegisterRange(RegisterTable.Holding, HuaweiRegisterMap.MaxChargePower, 4),
            new RegisterRange(RegisterTable.Holding, HuaweiRegisterMap.EmmaGridActivePower, HuaweiRegisterMap.EmmaGridBlockCount, HuaweiRegisterMap.EmmaUnitId),
        ],
        SerialNumber: new RegisterRange(RegisterTable.Holding, HuaweiRegisterMap.SerialNumber, HuaweiRegisterMap.SerialNumberCount),
        SerialPrefixLength: 0);

    public RegisterSnapshotSpec SnapshotFor(InverterDriverOptions options) =>
        IsBehindEmma(options) ? EmmaSnapshot : Snapshot;

    public IInverterDriver Create(IModbusClient client, InverterDriverOptions options) =>
        new HuaweiSun2000Driver(client, _loggers, _time, IsBehindEmma(options), options);

    private static bool IsBehindEmma(InverterDriverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var topology = options.Topology.Trim().ToLowerInvariant();
        return topology switch
        {
            "" or DongleTopology => false,
            EmmaTopology => true,
            _ => throw new ArgumentException(
                $"'{options.Topology}' is not a way to reach a Huawei inverter: '{DongleTopology}' or '{EmmaTopology}'.", nameof(options)),
        };
    }
}
