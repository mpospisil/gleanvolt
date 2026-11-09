using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Devices.Huawei;

/// <summary>
/// Huawei's SCharger-7KS-S0 (one phase) and SCharger-22KT-S0 (three phases) on their own Modbus TCP server
/// (issue #351). The registers are <see cref="SChargerRegisterMap"/>. The owner has to switch Modbus TCP on in
/// FusionSolar first; it is off by default.
///
/// <para><b>No status.</b> The protocol reports power, voltages and currents, nothing else. Power or current
/// above zero reads <see cref="EvChargerStatus.Charging"/>. Anything else reads <see cref="EvChargerStatus.Unknown"/>,
/// because no car and a car plugged in and waiting both read 0 W. The controller never ends a session on
/// Unknown (<c>IsCarKnownDisconnected</c>).</para>
///
/// <para><b>The setpoint is power.</b> The contract speaks amps, and the charger takes a maximum charge power
/// in tenths of a kilowatt. The driver converts as the control loop does, at <c>ChargeControl:NominalVoltage</c>
/// on the phases the car charges on (<c>ChargeControl:Phases</c>, never more than the model has): 16 A on three
/// phases is 11.0 kW, on one 3.7 kW. Start and stop write the charging control: resume and pause.</para>
///
/// <para><b>Expected tier, readings only.</b> Every fixture is synthetic, written from the protocol. Owners
/// report port 502 refused on 2023/2024 firmware, and nobody has yet confirmed the writes. Behind an EMMA,
/// the EMMA manages the charger: an owner there should leave the controller in dry-run until a tester shows
/// a clean hand-over.</para>
/// </summary>
public sealed class HuaweiSChargerDriver : IEvChargerDriver
{
    // 180-265 V, in the register's tenths: a European mains phase with margin on both sides. A SolaX or Huawei
    // inverter keeps nothing at 0x1000, and a device that does is unlikely to hold a mains voltage there.
    private const uint LowestProbeVoltage = 1800;
    private const uint HighestProbeVoltage = 2650;

    // Tenths of an amp: a phase drawing at least 1 A is delivering, even when the power register's 100 W step
    // still reads 0.
    private const uint DeliveringCurrent = 10;

    // The protocol's range for the maximum charge power: 0-22 kW.
    private const uint MaxPowerTenthsKw = 220;

    private readonly IModbusClient _client;
    private readonly ILogger<HuaweiSChargerDriver> _logger;
    private readonly bool _dryRun;
    private readonly int _currentChangeThresholdAmps;
    private readonly int _phases;
    private readonly double _volts;

    // Dry-run only, as in the SolaX driver: what the charger would now have, so that reads reflect the writes
    // the driver would have made.
    private int? _simulatedCurrentAmps;
    private SChargerControl? _simulatedControl;

    /// <param name="client">The charger's client, owned by the host.</param>
    /// <param name="capabilities">The model's: <see cref="SevenKs"/> or <see cref="TwentyTwoKt"/>.</param>
    /// <param name="logger">For the writes. Null logs nothing.</param>
    /// <param name="options">Dry-run and the setpoint's hysteresis. Null uses the defaults.</param>
    public HuaweiSChargerDriver(
        IModbusClient client,
        EvChargerCapabilities capabilities,
        ILogger<HuaweiSChargerDriver>? logger = null,
        EvChargerDriverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(capabilities);
        _client = client;
        Capabilities = capabilities;
        _logger = logger ?? NullLogger<HuaweiSChargerDriver>.Instance;
        options ??= new EvChargerDriverOptions();
        _dryRun = options.DryRun;
        _currentChangeThresholdAmps = Math.Max(1, options.CurrentChangeThresholdAmps);
        _phases = Math.Clamp(options.Phases ?? capabilities.Phases, 1, capabilities.Phases);
        _volts = options.NominalVoltage > 0 ? options.NominalVoltage : 230;
    }

    /// <summary>The SCharger-7KS-S0: one phase, 6-32 A (7.4 kW), no status and no use-mode.</summary>
    public static EvChargerCapabilities SevenKs { get; } = new(
        MinCurrentAmps: 6,
        MaxCurrentAmps: 32,
        PauseCurrentAmps: 0,
        Phases: 1,
        CanSwitchPhases: false,
        HasUseMode: false)
    {
        ReportsStatus = false,
    };

    /// <summary>The SCharger-22KT-S0: three phases, 6-32 A (22 kW), no status and no use-mode.</summary>
    public static EvChargerCapabilities TwentyTwoKt { get; } = SevenKs with { Phases = 3 };

    public EvChargerCapabilities Capabilities { get; }

    /// <summary>The phases the setpoint converts at: <c>ChargeControl:Phases</c>, never more than the model has.</summary>
    public int SetpointPhases => _phases;

    /// <summary>The register "Test connection" reads: phase L1's voltage.</summary>
    public static ushort ProbeRegister => SChargerRegisterMap.PhaseL1Voltage;

    /// <summary>Whether <paramref name="tenthsOfVolts"/>, read from <see cref="ProbeRegister"/>, is a mains voltage.</summary>
    public static bool IsMainsVoltage(uint tenthsOfVolts) =>
        tenthsOfVolts is >= LowestProbeVoltage and <= HighestProbeVoltage;

    /// <summary>
    /// The maximum charge power, in the register's tenths of a kilowatt, for a setpoint of <paramref name="amps"/>
    /// on <paramref name="phases"/> at <paramref name="volts"/>. Never above the protocol's 22 kW: 32 A on three
    /// phases would be 22.1.
    /// </summary>
    public static uint PowerForAmps(int amps, int phases, double volts, int maxAmps) =>
        Math.Min(MaxPowerTenthsKw, (uint)Math.Round(Math.Clamp(amps, 0, maxAmps) * phases * volts / 100));

    /// <summary>The setpoint in whole amps that <paramref name="tenthsOfKilowatts"/> means on <paramref name="phases"/> at <paramref name="volts"/>.</summary>
    public static int AmpsForPower(uint tenthsOfKilowatts, int phases, double volts, int maxAmps) =>
        (int)Math.Clamp(Math.Round(tenthsOfKilowatts * 100 / (phases * volts)), 0, maxAmps);

    public async Task<EvChargerReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var block = await ReadAsync(SChargerRegisterMap.MeasurementsStart, SChargerRegisterMap.MeasurementsCount, cancellationToken)
            .ConfigureAwait(false);
        var powerTenthsKw = Measurement(block, SChargerRegisterMap.TotalOutputPower);
        var highestCurrent = Enumerable.Range(0, 3)
            .Max(phase => Measurement(block, SChargerRegisterMap.PhaseL1Current + (phase * 2)));

        var delivering = powerTenthsKw > 0 || highestCurrent >= DeliveringCurrent;

        // The setpoint is best-effort, as on SolaX: a failed read must not fail the poll.
        uint? setpoint = null;
        try
        {
            setpoint = Word32(await ReadAsync(SChargerRegisterMap.MaxChargePower, 2, cancellationToken).ConfigureAwait(false), 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        return new EvChargerReading(
            delivering ? EvChargerStatus.Charging : EvChargerStatus.Unknown,
            powerTenthsKw * 100.0,
            setpoint is { } power ? AmpsForPower(power, _phases, _volts, Capabilities.MaxCurrentAmps) : null,
            Mode: null);
    }

    public async Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var control = _dryRun && _simulatedControl is { } simulatedControl
            ? simulatedControl
            : (SChargerControl)(await ReadAsync(SChargerRegisterMap.ChargingControl, 1, cancellationToken).ConfigureAwait(false))[0];

        var amps = _dryRun && _simulatedCurrentAmps is { } simulatedAmps
            ? simulatedAmps
            : AmpsForPower(
                Word32(await ReadAsync(SChargerRegisterMap.MaxChargePower, 2, cancellationToken).ConfigureAwait(false), 0),
                _phases,
                _volts,
                Capabilities.MaxCurrentAmps);

        // Only "resumed" is the state a start leaves. Standby is what the register reads before anyone wrote it.
        return new EvChargerSettings(control == SChargerControl.Resumed, amps);
    }

    public async Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default)
    {
        var clampedAmps = Math.Clamp(targetAmps, 0, Capabilities.MaxCurrentAmps);
        if (Math.Abs(clampedAmps - activeAmps) < _currentChangeThresholdAmps)
        {
            return;
        }

        var power = PowerForAmps(clampedAmps, _phases, _volts, Capabilities.MaxCurrentAmps);
        var prefix = _dryRun ? "[DRY RUN] would set " : "";

        _logger.LogInformation(
            "{Prefix}charger maximum power: {OldAmps}A -> {NewAmps}A ({Kilowatts} kW, register {RegisterValue}). {Reason}",
            prefix, activeAmps, clampedAmps, (power / 10.0).ToString("0.0", CultureInfo.InvariantCulture), power, reason);

        if (_dryRun)
        {
            _simulatedCurrentAmps = clampedAmps;
            return;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _client
            .WriteMultipleRegistersAsync(SChargerRegisterMap.MaxChargePower, [(ushort)(power >> 16), (ushort)(power & 0xFFFF)], cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetChargingAllowedAsync(bool allowed, string reason, CancellationToken cancellationToken = default)
    {
        var control = allowed ? SChargerControl.Resumed : SChargerControl.Paused;
        var prefix = _dryRun ? "[DRY RUN] would set " : "";

        _logger.LogInformation(
            "{Prefix}charger charging control: {Control} (register {RegisterValue}). {Reason}",
            prefix, control, (ushort)control, reason);

        if (_dryRun)
        {
            _simulatedControl = control;
            return;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _client
            .WriteSingleRegisterAsync(SChargerRegisterMap.ChargingControl, (ushort)control, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Phase L1's voltage, recognised when it is a mains voltage. A device that keeps nothing at the address
    /// refuses it, and the refusal propagates: that is no SCharger either.
    /// </summary>
    public async Task<EvChargerProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var voltage = Word32(await _client.ReadHoldingRegistersAsync(ProbeRegister, 2, cancellationToken).ConfigureAwait(false), 0);
        return new EvChargerProbeResult(IsMainsVoltage(voltage), voltage);
    }

    // A value in the measurement block, by its address.
    private static uint Measurement(ushort[] block, int address) =>
        Word32(block, address - SChargerRegisterMap.MeasurementsStart);

    private static uint Word32(ushort[] registers, int offset) =>
        ((uint)registers[offset] << 16) | registers[offset + 1];

    private async Task<ushort[]> ReadAsync(ushort start, ushort count, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.ReadHoldingRegistersAsync(start, count, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Failed to read SCharger registers 0x{start:X4}-0x{start + count - 1:X4}.", ex);
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

/// <summary>
/// Makes <see cref="HuaweiSChargerDriver"/>s for one model. There are two factories, because a factory's
/// capabilities are fixed and the two models differ in phase count: <see cref="SevenKs"/> and
/// <see cref="TwentyTwoKt"/>.
/// </summary>
public sealed class HuaweiSChargerDriverFactory : IEvChargerDriverFactory
{
    /// <summary>The SCharger-7KS-S0's id in configuration.</summary>
    public const string SevenKsId = "huawei-7ks";

    /// <summary>The SCharger-22KT-S0's id in configuration.</summary>
    public const string TwentyTwoKtId = "huawei-22kt";

    private readonly ILoggerFactory _loggers;

    private HuaweiSChargerDriverFactory(string id, string displayName, EvChargerCapabilities capabilities, ILoggerFactory? loggers)
    {
        Id = id;
        DisplayName = displayName;
        Capabilities = capabilities;
        _loggers = loggers ?? NullLoggerFactory.Instance;
    }

    /// <summary>The single-phase SCharger-7KS-S0.</summary>
    public static HuaweiSChargerDriverFactory SevenKs(ILoggerFactory? loggers = null) =>
        new(SevenKsId, "Huawei SCharger-7KS", HuaweiSChargerDriver.SevenKs, loggers);

    /// <summary>The three-phase SCharger-22KT-S0.</summary>
    public static HuaweiSChargerDriverFactory TwentyTwoKt(ILoggerFactory? loggers = null) =>
        new(TwentyTwoKtId, "Huawei SCharger-22KT", HuaweiSChargerDriver.TwentyTwoKt, loggers);

    public string Id { get; }

    public string DisplayName { get; }

    public EvChargerTransport Transport => EvChargerTransport.Modbus;

    // Port 502 and unit 1, as installers' guides give them. The protocol itself says a direct connection may
    // use unit 0 or 255; an owner whose charger refuses unit 1 sets that. A second between requests and 5 s
    // after a failed connect, as for the SUN2000: Huawei's Modbus servers answer "busy" to a request that
    // arrives too soon, and two reads a poll still fit the 5 s poll interval.
    public ModbusDefaults Defaults { get; } = new(
        Port: 502, UnitId: 1, MinRequestInterval: TimeSpan.FromSeconds(1), ReconnectBackoff: TimeSpan.FromSeconds(5));

    ModbusDefaults? IEvChargerDriverFactory.Defaults => Defaults;

    public EvChargerCapabilities Capabilities { get; }

    // Every register the protocol defines: the measurements, and the setting block from the maximum power to
    // the charging control, the four addresses between included for the record. No serial number to remove:
    // the protocol has none.
    public RegisterSnapshotSpec Snapshot { get; } = new(
        [
            new RegisterRange(RegisterTable.Holding, SChargerRegisterMap.MeasurementsStart, SChargerRegisterMap.MeasurementsCount),
            new RegisterRange(RegisterTable.Holding, SChargerRegisterMap.MaxChargePower, 7),
        ]);

    RegisterSnapshotSpec? IEvChargerDriverFactory.Snapshot => Snapshot;

    public IEvChargerDriver Create(EvChargerConnection connection, EvChargerDriverOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new HuaweiSChargerDriver(
            connection.RequireModbus(DisplayName), Capabilities, _loggers.CreateLogger<HuaweiSChargerDriver>(), options);
    }
}
