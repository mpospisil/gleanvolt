using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Devices.Solax;

/// <summary>
/// The SolaX X1/X3 Hybrid G4 inverter (issue #317): one block read of the input registers for telemetry,
/// and the Modbus Power Control command (<see cref="BatteryDischargeControl"/>) for the battery hold.
/// </summary>
public sealed class SolaxInverterDriver : IInverterDriver
{
    private readonly IModbusClient _client;
    private readonly ILogger<SolaxInverterDriver> _logger;

    /// <param name="client">The inverter's client, owned by the host.</param>
    /// <param name="loggers">For the driver's and the hold's logs. Null logs nothing.</param>
    /// <param name="options">Dry-run and the hold's timing. Null uses the defaults.</param>
    public SolaxInverterDriver(
        IModbusClient client,
        ILoggerFactory? loggers = null,
        InverterDriverOptions? options = null)
    {
        _client = client;
        loggers ??= NullLoggerFactory.Instance;
        options ??= new InverterDriverOptions();
        _logger = loggers.CreateLogger<SolaxInverterDriver>();

        BatteryHold = new BatteryDischargeControl(
            client,
            loggers.CreateLogger<BatteryDischargeControl>(),
            options.DryRun,
            options.HoldCommandDuration,
            options.HoldRetargetThresholdWatts);
    }

    /// <summary>The input register "Test connection" reads: the battery's state of charge.</summary>
    public static ushort ProbeRegister => InverterRegisterMap.BatteryCapacity.Address;

    /// <summary>What the SolaX family supports: the hold, and the inverter's output per phase.</summary>
    public static InverterCapabilities SolaxCapabilities { get; } = new(HasBatteryHold: true, HasPhaseOutput: true);

    public InverterCapabilities Capabilities => SolaxCapabilities;

    public IBatteryDischargeControl BatteryHold { get; }

    /// <summary>
    /// Whether <paramref name="value"/>, read from <see cref="ProbeRegister"/>, could be a state of charge.
    /// One register cannot prove what a box is, but it catches the mistake that cost a day once (see
    /// <see cref="InverterRegister.BatteryCapacity"/>): the same register read from the charger is a
    /// number no battery reports.
    /// </summary>
    public static bool IsStateOfCharge(ushort value) => value <= 100;

    /// <summary>
    /// How many characters of the serial number name the model (<c>10K0</c>, <c>H34A</c>): what "Test
    /// connection" checks, and what a register snapshot keeps.
    /// </summary>
    public const int ModelPrefixLength = 4;

    /// <summary>
    /// Whether <paramref name="registers"/>, read from the start of the serial number, begin with a model
    /// prefix: <see cref="ModelPrefixLength"/> upper-case letters or digits, two to a register, high byte first.
    /// </summary>
    public static bool IsModelPrefix(IReadOnlyList<ushort> registers)
    {
        ArgumentNullException.ThrowIfNull(registers);

        var characters = registers.SelectMany(register => new[] { (char)(register >> 8), (char)(register & 0xFF) })
            .Take(ModelPrefixLength)
            .ToList();

        return characters.Count == ModelPrefixLength
            && characters.All(character => character is (>= 'A' and <= 'Z') or (>= '0' and <= '9'));
    }

    public async Task<InverterReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var block = await ReadTelemetryBlockAsync(cancellationToken).ConfigureAwait(false);
        LogGridRegisterCandidates(block);

        return Decode(block);
    }

    public async Task<InverterProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var value = (await _client.ReadInputRegistersAsync(ProbeRegister, 1, cancellationToken).ConfigureAwait(false))[0];
        if (!IsStateOfCharge(value))
        {
            return new InverterProbeResult(false, value);
        }

        // A state of charge alone does not tell the inverter from the charger: the SolaX charger's 0x001C
        // reads a small number too (24 in the reference installation's snapshot, #319). The inverter keeps
        // its serial number at holding 0x0000; the charger refuses the address.
        try
        {
            var prefix = await _client.ReadHoldingRegistersAsync(
                InverterRegisterMap.SerialNumberStart, (ModelPrefixLength + 1) / 2, cancellationToken).ConfigureAwait(false);
            return new InverterProbeResult(IsModelPrefix(prefix), value);
        }
        catch (ModbusDeviceException ex) when (ex.IsIllegalAddress)
        {
            return new InverterProbeResult(false, value);
        }
    }

    /// <summary>
    /// The telemetry block, from <see cref="InverterRegisterMap.TelemetryBlockStart"/>, in the contract's
    /// units and signs. Pure, so the decoding can be tested without a device.
    ///
    /// <para>Per the SolaX Gen4 protocol: the battery power register is signed 16-bit with positive =
    /// charging (negative = discharging). FeedinPower is the signed 32-bit grid METER reading (low word
    /// first) using SolaX's convention where positive = EXPORT; it is negated, because the contract is
    /// positive = importing. Powerdc1/2 (solar) are unsigned, and their sum is the "Solar" figure in the
    /// SolaX Cloud app; SOC is an unsigned 0-100 percentage. GridPowerR/S/T are the inverter's own AC
    /// output per phase, signed 16-bit.</para>
    /// </summary>
    public static InverterReading Decode(ushort[] block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var feedinWatts = FeedinWatts(block);

        return new InverterReading(
            PvWatts: FromBlock(block, InverterRegisterMap.Powerdc1) + FromBlock(block, InverterRegisterMap.Powerdc2),
            GridWatts: -feedinWatts,
            BatteryWatts: unchecked((short)FromBlock(block, InverterRegisterMap.BatteryPowerCharge1)),
            BatterySocPercent: FromBlock(block, InverterRegisterMap.BatteryCapacity),
            InverterOutputPerPhase: new PhaseWatts(
                unchecked((short)FromBlock(block, InverterRegisterMap.GridPowerR)),
                unchecked((short)FromBlock(block, InverterRegisterMap.GridPowerS)),
                unchecked((short)FromBlock(block, InverterRegisterMap.GridPowerT))));
    }

    // Reads the whole telemetry range in one Modbus request (the SolaX protocol requires
    // >=1 second between separate instructions, so batching beats many small reads).
    private async Task<ushort[]> ReadTelemetryBlockAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _client
                .ReadInputRegistersAsync(
                    InverterRegisterMap.TelemetryBlockStart,
                    InverterRegisterMap.TelemetryBlockCount,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to read inverter telemetry block starting at address {InverterRegisterMap.TelemetryBlockStart}.", ex);
        }
    }

    // Diagnostic: dumps every register that could plausibly be "the grid" so we can compare each one
    // against the live figures in the SolaX Cloud app and confirm which reading is real. FeedinPower
    // (0x46/0x47) is the grid METER we use for surplus; GridPowerR/S/T (0x6C/70/74) are the inverter's
    // AC output per phase (not the meter). All come from the one telemetry block, so this costs no
    // extra Modbus reads. Logs at Debug -- enable with Logging:LogLevel:Gleanvolt.Devices.Solax=Debug.
    private void LogGridRegisterCandidates(ushort[] block)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        _logger.LogDebug(
            "Grid registers: FeedinPower(0x46/47)={FeedinWatts}W (raw low={FeedinLow} high={FeedinHigh}, +=export) | "
            + "inverter AC output GridPowerR(0x6C)={GridR}W S(0x70)={GridS}W T(0x74)={GridT}W",
            FeedinWatts(block),
            FromBlock(block, InverterRegisterMap.FeedinPowerLow),
            FromBlock(block, InverterRegisterMap.FeedinPowerHigh),
            unchecked((short)FromBlock(block, InverterRegisterMap.GridPowerR)),
            unchecked((short)FromBlock(block, InverterRegisterMap.GridPowerS)),
            unchecked((short)FromBlock(block, InverterRegisterMap.GridPowerT)));
    }

    // Signed 32-bit, low word first; positive = export, as SolaX reports it.
    private static int FeedinWatts(ushort[] block)
    {
        var low = FromBlock(block, InverterRegisterMap.FeedinPowerLow);
        var high = FromBlock(block, InverterRegisterMap.FeedinPowerHigh);
        return unchecked((int)(((uint)high << 16) | low));
    }

    private static ushort FromBlock(ushort[] block, RegisterDescriptor register) => block[register.Address];

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Makes <see cref="SolaxInverterDriver"/>s.</summary>
public sealed class SolaxInverterDriverFactory : IInverterDriverFactory
{
    /// <summary>SolaX's id in configuration.</summary>
    public const string DriverId = "solax";

    private readonly ILoggerFactory _loggers;

    public SolaxInverterDriverFactory(ILoggerFactory? loggers = null)
    {
        _loggers = loggers ?? NullLoggerFactory.Instance;
    }

    public string Id => DriverId;

    public string DisplayName => "SolaX";

    public InverterCapabilities Capabilities => SolaxInverterDriver.SolaxCapabilities;

    // The telemetry block (which holds the probe's register too), and the serial number, kept to the prefix
    // the LAN scan names the model by -- four characters at most (SolaxModels). The power-control block is
    // written, never read: reading it reports the firmware version, at an address nobody has verified, so
    // it is not in the snapshot.
    public RegisterSnapshotSpec Snapshot { get; } = new(
        [
            new RegisterRange(RegisterTable.Input, InverterRegisterMap.TelemetryBlockStart, InverterRegisterMap.TelemetryBlockCount),
            new RegisterRange(RegisterTable.Holding, InverterRegisterMap.SerialNumberStart, InverterRegisterMap.SerialNumberCount),
        ],
        SerialNumber: new RegisterRange(RegisterTable.Holding, InverterRegisterMap.SerialNumberStart, InverterRegisterMap.SerialNumberCount),
        SerialPrefixLength: SolaxInverterDriver.ModelPrefixLength);

    // Port 502, unit 1, and the 250 ms compromise between requests: see DeviceConfig.MinRequestInterval.
    public ModbusDefaults Defaults { get; } = new(Port: 502, UnitId: 1, MinRequestInterval: TimeSpan.FromMilliseconds(250));

    public IInverterDriver Create(IModbusClient client, InverterDriverOptions options) =>
        new SolaxInverterDriver(client, _loggers, options);
}
