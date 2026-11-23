using Gleanvolt.Devices;
using Gleanvolt.Providers.Contracts.Charger;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Hosting.Chargers;

/// <summary>
/// The charger driver for a charger behind a plugin (#435). It keeps on Gleanvolt's side everything the driver
/// contract promises, so a plugin cannot get it wrong: the setpoint is sent only when it moved by the threshold,
/// clamped to the charger's range; in dry-run nothing is sent and what would have been stands in for the
/// device's; a read that fails or is refused throws, so the poll says unknown. The plugin only maps the two
/// commands and its charger's states onto its own device.
/// </summary>
public sealed class ExternalChargerDriver : IEvChargerDriver
{
    private readonly ExternalChargerLink _link;
    private readonly ILogger<ExternalChargerDriver> _logger;
    private readonly bool _dryRun;
    private readonly int _currentChangeThresholdAmps;

    // Dry-run only: what the charger would now have.
    private int? _simulatedAmps;
    private bool? _simulatedAllowed;

    public ExternalChargerDriver(ExternalChargerLink link, ILogger<ExternalChargerDriver>? logger = null, EvChargerDriverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        _link = link;
        _logger = logger ?? NullLogger<ExternalChargerDriver>.Instance;
        options ??= new EvChargerDriverOptions();
        _dryRun = options.DryRun;
        _currentChangeThresholdAmps = Math.Max(1, options.CurrentChangeThresholdAmps);
    }

    /// <summary>
    /// What is known before a plugin connects, and what a 1.0 plugin, which says nothing, is taken to be: 6 to 32 A on
    /// three phases. A 1.1 plugin says its charger's own in its hello (#440); a reading can narrow the ceiling further
    /// with <see cref="ChargerReading.MaxCurrentAmps"/>, and nothing above it is sent.
    /// </summary>
    public static EvChargerCapabilities ExternalCapabilities { get; } = CapabilitiesOf(ChargerCapabilities.Default);

    /// <summary>The contract's capabilities as the driver contract's.</summary>
    public static EvChargerCapabilities CapabilitiesOf(ChargerCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return new EvChargerCapabilities(
            capabilities.MinCurrentAmps,
            capabilities.MaxCurrentAmps,
            capabilities.PauseCurrentAmps,
            capabilities.Phases,
            capabilities.CanSwitchPhases,
            HasUseMode: false);
    }

    /// <summary>The attached plugin's charger, or <see cref="ExternalCapabilities"/> before one said hello.</summary>
    public EvChargerCapabilities Capabilities =>
        _link.Capabilities is { } capabilities ? CapabilitiesOf(capabilities) : ExternalCapabilities;

    /// <summary>The contract's state as the driver contract's status; the names are the same OCPP ones.</summary>
    public static EvChargerStatus StatusOf(ChargerState state) => state switch
    {
        ChargerState.Available => EvChargerStatus.Available,
        ChargerState.Preparing => EvChargerStatus.Preparing,
        ChargerState.Charging => EvChargerStatus.Charging,
        ChargerState.SuspendedEv => EvChargerStatus.SuspendedEv,
        ChargerState.SuspendedEvse => EvChargerStatus.SuspendedEvse,
        ChargerState.Finishing => EvChargerStatus.Finishing,
        ChargerState.Faulted => EvChargerStatus.Faulted,
        ChargerState.Unavailable => EvChargerStatus.Unavailable,
        _ => EvChargerStatus.Unknown,
    };

    public async Task<EvChargerReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        var reading = await _link.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new EvChargerReading(
            StatusOf(reading.Status),
            Math.Max(0, reading.PowerWatts),
            _dryRun && _simulatedAmps is { } simulated ? simulated : reading.CurrentAmps,
            reading.Mode);
    }

    public async Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_dryRun && _simulatedAllowed is { } allowedNow && _simulatedAmps is { } ampsNow)
        {
            return new EvChargerSettings(allowedNow, ampsNow);
        }

        var reading = await _link.ReadAsync(cancellationToken).ConfigureAwait(false);
        var allowed = _dryRun && _simulatedAllowed is { } simulatedAllowed
            ? simulatedAllowed
            : reading.ChargingAllowed ?? throw new InvalidOperationException("The charger plugin did not say whether charging is allowed.");
        var amps = _dryRun && _simulatedAmps is { } simulatedAmps
            ? simulatedAmps
            : reading.CurrentAmps ?? throw new InvalidOperationException("The charger plugin did not say the current setpoint.");

        return new EvChargerSettings(allowed, Math.Clamp(amps, 0, Ceiling), reading.Mode);
    }

    public async Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default)
    {
        var clampedAmps = Math.Clamp(targetAmps, 0, Ceiling);
        if (Math.Abs(clampedAmps - activeAmps) < _currentChangeThresholdAmps)
        {
            return;
        }

        var prefix = _dryRun ? "[DRY RUN] would set " : "";
        _logger.LogInformation(
            "{Prefix}charger current through the plugin: {OldAmps}A -> {NewAmps}A{Pause}. {Reason}",
            prefix, activeAmps, clampedAmps, clampedAmps < Capabilities.MinCurrentAmps ? " (a pause)" : "", reason);

        if (_dryRun)
        {
            _simulatedAmps = clampedAmps;
            return;
        }

        await _link.SetCurrentAsync(clampedAmps, reason, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetChargingAllowedAsync(bool allowed, string reason, CancellationToken cancellationToken = default)
    {
        var prefix = _dryRun ? "[DRY RUN] would " : "";
        _logger.LogInformation("{Prefix}{Action} charging through the plugin. {Reason}", prefix, allowed ? "allow" : "stop", reason);

        if (_dryRun)
        {
            _simulatedAllowed = allowed;
            return;
        }

        await _link.SetChargingAllowedAsync(allowed, reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A fresh reading from the plugin: recognised when one arrives, with its power as the reading. Throws when
    /// no plugin is connected or it does not answer, as an address that does not answer would.
    /// </summary>
    public async Task<EvChargerProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var reading = await _link.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new EvChargerProbeResult(true, reading.PowerWatts);
    }

    // The charger's ceiling from its hello, lowered by what a reading says now, never above what the contract allows.
    private int Ceiling => Math.Min(
        Math.Min(_link.DeviceMaxAmps ?? int.MaxValue, Capabilities.MaxCurrentAmps),
        ChargerReadingValidator.MaxCurrentAmps);
}

/// <summary>
/// The <c>external</c> charger driver (#435): the charger is behind a plugin on the charger hub. It needs no
/// address and no connection; Settings hides both when it is chosen.
/// </summary>
public sealed class ExternalChargerDriverFactory : IEvChargerDriverFactory
{
    /// <summary>The id in <c>Pv:Chargers:0:Driver</c>.</summary>
    public const string DriverId = "external";

    private readonly ExternalChargerLink? _link;
    private readonly ILoggerFactory? _loggers;

    /// <param name="link">The hub's side; null for a factory that only describes the driver.</param>
    /// <param name="loggers">For the driver's log. Null logs nothing.</param>
    public ExternalChargerDriverFactory(ExternalChargerLink? link, ILoggerFactory? loggers = null)
    {
        _link = link;
        _loggers = loggers;
    }

    public string Id => DriverId;

    /// <summary>Shown translated on Settings; this is the English for the log and the API.</summary>
    public string DisplayName => "External plugin";

    public EvChargerTransport Transport => EvChargerTransport.External;

    public ModbusDefaults? Defaults => null;

    public EvChargerCapabilities Capabilities => ExternalChargerDriver.ExternalCapabilities;

    public RegisterSnapshotSpec? Snapshot => null;

    public IEvChargerDriver Create(EvChargerConnection connection, EvChargerDriverOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.Transport != EvChargerTransport.External)
        {
            throw new ArgumentException($"A charger behind a plugin takes no connection, not one over {connection.Transport}.");
        }

        var link = _link ?? throw new InvalidOperationException("This host has no charger hub for a plugin to connect to.");
        return new ExternalChargerDriver(link, _loggers?.CreateLogger<ExternalChargerDriver>(), options);
    }
}
