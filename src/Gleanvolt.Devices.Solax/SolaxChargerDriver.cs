using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Devices.Solax;

/// <summary>
/// The SolaX X1/X3-HAC EV charger (issue #348). Telemetry is two input registers (RunMode and total charge
/// power) and two best-effort holding registers (use-mode and current setpoint); control writes the current
/// setpoint, and the use-mode as the contract's start and stop: <see cref="SolaxChargerUseMode.Fast"/> to
/// allow charging, <see cref="SolaxChargerUseMode.Stop"/> to stop it.
///
/// <para>In dry-run nothing is written: the intended change is logged with its encoded register value, and
/// the simulated value stands in for the device's on the next <see cref="ReadSettingsAsync"/>, so the log
/// reads like a real run (change once, then quiet) without touching the charger.</para>
/// </summary>
public sealed class SolaxChargerDriver : IEvChargerDriver
{
    // The charge-current holding register stores hundredths of an amp: registerValue = amps * 100 (16 A -> 1600).
    private const double CurrentRegisterAmpsPerCount = 0.01;

    // The RunMode register runs 0-13, and follows OCPP's status names, as EvChargerStatus does.
    private const ushort HighestRunMode = (ushort)EvChargerStatus.Stopping;

    private readonly IModbusClient _client;
    private readonly ILogger<SolaxChargerDriver> _logger;
    private readonly bool _dryRun;
    private readonly int _currentChangeThresholdAmps;

    // Dry-run only: what the charger "would" now have, so reads reflect prior simulated writes and change
    // detection behaves like a real run. The use-mode is here too: a dry run in which charging is never
    // allowed would report every controller idle, which says nothing about the setpoints it exists to check.
    private int? _simulatedCurrentAmps;
    private SolaxChargerUseMode? _simulatedUseMode;

    /// <param name="client">The charger's client, owned by the host.</param>
    /// <param name="logger">For the writes. Null logs nothing.</param>
    /// <param name="options">Dry-run and the setpoint's hysteresis. Null uses the defaults.</param>
    public SolaxChargerDriver(
        IModbusClient client,
        ILogger<SolaxChargerDriver>? logger = null,
        EvChargerDriverOptions? options = null)
    {
        _client = client;
        _logger = logger ?? NullLogger<SolaxChargerDriver>.Instance;
        options ??= new EvChargerDriverOptions();
        _dryRun = options.DryRun;
        _currentChangeThresholdAmps = Math.Max(1, options.CurrentChangeThresholdAmps);
    }

    /// <summary>
    /// The X1/X3-HAC: 6-32 A per its protocol (6 A is also the IEC 61851 minimum), paused at 0, three phases
    /// with no switching, and a use-mode of its own.
    /// </summary>
    public static EvChargerCapabilities SolaxCapabilities { get; } = new(
        MinCurrentAmps: 6,
        MaxCurrentAmps: 32,
        PauseCurrentAmps: 0,
        Phases: 3,
        CanSwitchPhases: false,
        HasUseMode: true);

    public EvChargerCapabilities Capabilities => SolaxCapabilities;

    /// <summary>The input register "Test connection" and the LAN scan read: RunMode.</summary>
    public static ushort ProbeRegister => EvChargerRegisterMap.RunMode.Address;

    /// <summary>
    /// Whether <paramref name="value"/>, read from <see cref="ProbeRegister"/>, could be a run mode. One register
    /// cannot prove what a box is, but the inverter's register there is a number far outside the charger's range.
    /// </summary>
    public static bool IsRunMode(ushort value) => value <= HighestRunMode;

    /// <summary>
    /// The status RunMode reports. A value outside its range is <see cref="EvChargerStatus.Unknown"/>: the
    /// absence of information, never a car that is gone.
    /// </summary>
    public static EvChargerStatus StatusFromRunMode(ushort raw) =>
        IsRunMode(raw) ? (EvChargerStatus)raw : EvChargerStatus.Unknown;

    /// <summary>The use-mode a raw register value names, or null for a value outside the known ones.</summary>
    public static SolaxChargerUseMode? UseModeFromRaw(ushort raw) =>
        raw <= (ushort)SolaxChargerUseMode.Green ? (SolaxChargerUseMode)raw : null;

    public async Task<EvChargerReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var statusRaw = await ReadInputAsync(EvChargerRegisterMap.RunMode, cancellationToken).ConfigureAwait(false);
        var powerRaw = await ReadInputAsync(EvChargerRegisterMap.ChargePowerTotal, cancellationToken).ConfigureAwait(false);
        var mode = await TryReadHoldingAsync(EvChargerRegisterMap.ChargerUseMode, cancellationToken).ConfigureAwait(false);
        var current = await TryReadHoldingAsync(EvChargerRegisterMap.ChargeCurrentSetpoint, cancellationToken).ConfigureAwait(false);

        // Unsigned: the HAC doesn't support V2G. The use-mode is for display: a value this driver does not
        // know -- what the charger reports for a while after the link recovers -- is no mode at all.
        return new EvChargerReading(
            StatusFromRunMode(statusRaw),
            powerRaw,
            current is { } amps ? (int)Math.Round(amps * CurrentRegisterAmpsPerCount) : null,
            mode is { } raw ? UseModeFromRaw(raw)?.ToString() : null);
    }

    public async Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        // Both readings follow the same rule: in dry-run a value we have "written" stands in for the
        // hardware; otherwise the register is read live and decoded.
        ushort useModeRaw = _dryRun && _simulatedUseMode is { } simulatedMode
            ? (ushort)simulatedMode
            : await ReadHoldingAsync(EvChargerRegisterMap.ChargerUseMode, cancellationToken).ConfigureAwait(false);

        var currentAmps = _dryRun && _simulatedCurrentAmps is { } simulated
            ? simulated
            : (int)Math.Round(
                await ReadHoldingAsync(EvChargerRegisterMap.ChargeCurrentSetpoint, cancellationToken).ConfigureAwait(false)
                * CurrentRegisterAmpsPerCount);

        // Only Fast is "allowed": Stop is ours or the owner's, Eco and Green are the owner's, and anything
        // else is junk from a link that just recovered. Which of those it is, is the caller's to judge over
        // time (ChargerOwnership), never this one read's.
        var useMode = UseModeFromRaw(useModeRaw);
        return new EvChargerSettings(
            useMode == SolaxChargerUseMode.Fast,
            currentAmps,
            useMode?.ToString() ?? useModeRaw.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default)
    {
        // Never below 0 (pause) or above the hardware maximum.
        var clampedAmps = Math.Clamp(targetAmps, 0, SolaxCapabilities.MaxCurrentAmps);

        // Hysteresis: only re-command once the target has moved by at least the threshold (1 A default).
        if (Math.Abs(clampedAmps - activeAmps) < _currentChangeThresholdAmps)
        {
            return;
        }

        var registerValue = (ushort)Math.Round(clampedAmps / CurrentRegisterAmpsPerCount);
        var prefix = _dryRun ? "[DRY RUN] would set " : "";

        _logger.LogInformation(
            "{Prefix}charger current setpoint: {OldAmps}A -> {NewAmps}A (register {RegisterValue}). {Reason}",
            prefix, activeAmps, clampedAmps, registerValue, reason);

        if (_dryRun)
        {
            _simulatedCurrentAmps = clampedAmps;
            return;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _client
            .WriteSingleRegisterAsync(EvChargerRegisterMap.ChargeCurrentSetpoint.Address, registerValue, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetChargingAllowedAsync(bool allowed, string reason, CancellationToken cancellationToken = default)
    {
        // The enum's numeric values are the register's: 0=Stop, 1=Fast. Written raw, with no scaling --
        // unlike the current setpoint, which is hundredths of an amp.
        var mode = allowed ? SolaxChargerUseMode.Fast : SolaxChargerUseMode.Stop;
        var registerValue = (ushort)mode;
        var prefix = _dryRun ? "[DRY RUN] would set " : "";

        _logger.LogInformation(
            "{Prefix}charger use-mode: {Mode} (register {RegisterValue}). {Reason}",
            prefix, mode, registerValue, reason);

        if (_dryRun)
        {
            _simulatedUseMode = mode;
            return;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _client
            .WriteSingleRegisterAsync(EvChargerRegisterMap.ChargerUseMode.Address, registerValue, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EvChargerProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var value = (await _client.ReadInputRegistersAsync(ProbeRegister, 1, cancellationToken).ConfigureAwait(false))[0];
        return new EvChargerProbeResult(IsRunMode(value), value);
    }

    private async Task<ushort> ReadInputAsync(RegisterDescriptor register, CancellationToken cancellationToken)
    {
        try
        {
            var values = await _client
                .ReadInputRegistersAsync(register.Address, numberOfPoints: 1, cancellationToken)
                .ConfigureAwait(false);
            return values[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Failed to read register '{register.Name}' at address {register.Address}.", ex);
        }
    }

    private async Task<ushort> ReadHoldingAsync(RegisterDescriptor register, CancellationToken cancellationToken)
    {
        try
        {
            var values = await _client
                .ReadHoldingRegistersAsync(register.Address, numberOfPoints: 1, cancellationToken)
                .ConfigureAwait(false);
            return values[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Failed to read charger register '{register.Name}' at address {register.Address}.", ex);
        }
    }

    // Best-effort, for telemetry: these registers aren't available on every charger/firmware, and a failed
    // read must not fail the whole poll.
    private async Task<ushort?> TryReadHoldingAsync(RegisterDescriptor register, CancellationToken cancellationToken)
    {
        try
        {
            var values = await _client
                .ReadHoldingRegistersAsync(register.Address, numberOfPoints: 1, cancellationToken)
                .ConfigureAwait(false);
            return values[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
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

/// <summary>Makes <see cref="SolaxChargerDriver"/>s.</summary>
public sealed class SolaxChargerDriverFactory : IEvChargerDriverFactory
{
    /// <summary>SolaX's id in configuration.</summary>
    public const string DriverId = "solax";

    private readonly ILoggerFactory _loggers;

    public SolaxChargerDriverFactory(ILoggerFactory? loggers = null)
    {
        _loggers = loggers ?? NullLoggerFactory.Instance;
    }

    public string Id => DriverId;

    public string DisplayName => "SolaX";

    public EvChargerTransport Transport => EvChargerTransport.Modbus;

    // Port 502 and unit 1, like the inverter. The SolaX protocol asks for a pause between instructions;
    // the same 250 ms compromise as the inverter's (see DeviceConfig.MinRequestInterval).
    public ModbusDefaults Defaults { get; } = new(Port: 502, UnitId: 1, MinRequestInterval: TimeSpan.FromMilliseconds(250));

    ModbusDefaults? IEvChargerDriverFactory.Defaults => Defaults;

    public EvChargerCapabilities Capabilities => SolaxChargerDriver.SolaxCapabilities;

    // The input registers up to RunMode (power and status, and what lies between, for the record), the
    // serial number, and the two holding registers the driver reads and writes. The serial number keeps the
    // prefix the LAN scan names the model by (SolaxModels: C107/C311/C322 for the EVC, 5 + model code for
    // the HAC).
    public RegisterSnapshotSpec Snapshot { get; } = new(
        [
            new RegisterRange(RegisterTable.Input, 0x0000, (ushort)(EvChargerRegisterMap.RunMode.Address + 1)),
            new RegisterRange(RegisterTable.Holding, EvChargerRegisterMap.SerialNumberStart, EvChargerRegisterMap.SerialNumberCount),
            new RegisterRange(RegisterTable.Holding, EvChargerRegisterMap.ChargerUseMode.Address, 1),
            new RegisterRange(RegisterTable.Holding, EvChargerRegisterMap.ChargeCurrentSetpoint.Address, 1),
        ],
        SerialNumber: new RegisterRange(RegisterTable.Holding, EvChargerRegisterMap.SerialNumberStart, EvChargerRegisterMap.SerialNumberCount),
        SerialPrefixLength: 4);

    RegisterSnapshotSpec? IEvChargerDriverFactory.Snapshot => Snapshot;

    public IEvChargerDriver Create(EvChargerConnection connection, EvChargerDriverOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new SolaxChargerDriver(connection.RequireModbus(DisplayName), _loggers.CreateLogger<SolaxChargerDriver>(), options);
    }
}
