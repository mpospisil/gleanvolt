using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Devices.GoE;

/// <summary>
/// The go-e Charger (Gemini, Gemini flex, HOMEfix and HOME+, hardware V3 and later) over its local HTTP API v2
/// (issue #352). The owner switches the API on in the go-e app first (Internet → Advanced settings → "Local
/// HTTP API v2"); it is off by default. Older units speak only API v1, which this driver does not.
///
/// <para><b>Reads.</b> One filtered <c>/api/status</c> per call (<see cref="StatusRequest"/>). <c>car</c> is the
/// status: 1 idle, 2 charging, 3 waiting for the car, 4 finished with the car still connected. Whether 3 and 4
/// are the car's doing or the charger's is <c>alw</c>, "is the car allowed to charge now". Power is
/// <c>nrg[11]</c>, in watts.</para>
///
/// <para><b>Writes: two keys, nothing else.</b> Start and stop write the force state <c>frc</c> (2 on, 1 off). The
/// setpoint writes <c>amp</c>. Both are configuration keys the charger keeps across a restart; API v2 documents
/// no transient current (v1's <c>amx</c> is gone, and <c>tcl</c> is undocumented). So the driver writes only on
/// a change, as the contract already asks: a steady control loop writes nothing, and the tests hold it to
/// that. Phase switching (<c>psm</c>) is read for the recording, reported as a capability, and never written.</para>
///
/// <para><b>Pause.</b> The charger refuses a setpoint below 6 A, and the control loop pauses by writing 0. So a
/// pause is the force state Off, and the driver remembers that it paused: settings then read "allowed, 0 A",
/// which is what the loop wrote, and the next setpoint of 6 A or more writes <c>amp</c> and turns the force
/// state back on. A stop while paused stays stopped. The memory does not survive a restart; after one, a
/// charger the driver had paused reads as stopped, and the controller starts in Off anyway.</para>
///
/// <para><b>Expected tier.</b> Every fixture is synthetic, written from go-e's API documentation, until a
/// tester's snapshot (#354) replaces it.</para>
/// </summary>
public sealed class GoeChargerDriver : IEvChargerDriver
{
    /// <summary>The keys every status read asks for. <c>psm</c>, <c>pnp</c>, <c>fwv</c>, <c>typ</c> and <c>var</c> are for the recording.</summary>
    public static IReadOnlyList<string> StatusKeys { get; } =
        ["car", "alw", "frc", "amp", "ama", "var", "nrg", "err", "modelStatus", "psm", "pnp", "fwv", "typ"];

    /// <summary>The one request the driver reads with, relative to the charger's base address.</summary>
    public static string StatusRequest { get; } = "api/status?filter=" + string.Join(',', StatusKeys);

    /// <summary>The only keys the driver ever writes: the setpoint and the force state.</summary>
    public static IReadOnlyList<string> WrittenKeys { get; } = ["amp", "frc"];

    // nrg: U L1, L2, L3, N; I L1, L2, L3; P L1, L2, L3, N, total; pf L1, L2, L3, N. Power in W.
    private const int TotalPowerIndex = 11;

    private readonly HttpClient _http;
    private readonly ILogger<GoeChargerDriver> _logger;
    private readonly bool _dryRun;
    private readonly int _currentChangeThresholdAmps;

    // What the control loop last asked for, which the device cannot hold on its own: whether it may charge (null
    // until the first start or stop), and whether the setpoint is a pause.
    private bool? _allowed;
    private bool _paused;

    // The unit's own ceiling, from the last status: ama, and 16 A on an 11 kW unit.
    private int? _deviceMaxAmps;

    // Dry-run only: what the charger would now have.
    private int? _simulatedAmps;
    private GoeForceState? _simulatedForceState;

    /// <param name="http">The charger's client, base address set, owned by the host.</param>
    /// <param name="logger">For the writes. Null logs nothing.</param>
    /// <param name="options">Dry-run and the setpoint's hysteresis. Null uses the defaults.</param>
    public GoeChargerDriver(HttpClient http, ILogger<GoeChargerDriver>? logger = null, EvChargerDriverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _logger = logger ?? NullLogger<GoeChargerDriver>.Instance;
        options ??= new EvChargerDriverOptions();
        _dryRun = options.DryRun;
        _currentChangeThresholdAmps = Math.Max(1, options.CurrentChangeThresholdAmps);
    }

    /// <summary>6–32 A on three phases; the unit switches to one phase itself, and reports that it can.</summary>
    public static EvChargerCapabilities GoeCapabilities { get; } = new(
        MinCurrentAmps: 6,
        MaxCurrentAmps: 32,
        PauseCurrentAmps: 0,
        Phases: 3,
        CanSwitchPhases: true,
        HasUseMode: false);

    public EvChargerCapabilities Capabilities => GoeCapabilities;

    /// <summary>
    /// <c>car</c> and <c>alw</c> as the contract's status. Waiting and finished are the car's doing only while
    /// the charger allows charging; otherwise the charger is holding it, which is never "the car is full".
    /// </summary>
    public static EvChargerStatus StatusOf(int? car, bool allowed) => car switch
    {
        1 => EvChargerStatus.Available,
        2 => EvChargerStatus.Charging,
        3 => allowed ? EvChargerStatus.Preparing : EvChargerStatus.SuspendedEvse,
        4 => allowed ? EvChargerStatus.SuspendedEv : EvChargerStatus.SuspendedEvse,
        5 => EvChargerStatus.Faulted,
        _ => EvChargerStatus.Unknown,   // 0 and null: the charger's own internal error, not a fact about the car
    };

    public async Task<EvChargerReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);

        if (!status.ContainsKey("car"))
        {
            throw new InvalidOperationException("The go-e status has no 'car': is this a go-e with the HTTP API v2?");
        }

        var power = status["nrg"] is JsonArray energy && energy.Count > TotalPowerIndex && Number(energy[TotalPowerIndex]) is { } watts
            ? watts
            : throw new InvalidOperationException("The go-e status has no total power (nrg[11]).");

        return new EvChargerReading(
            StatusOf(Integer(status["car"]), Boolean(status["alw"]) ?? false),
            Math.Max(0, power),
            _paused ? 0 : Integer(status["amp"]),
            Mode: null);
    }

    public async Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        GoeForceState forceState;
        int amps;

        if (_dryRun && _simulatedForceState is { } simulatedForce && _simulatedAmps is { } simulatedAmps)
        {
            forceState = simulatedForce;
            amps = simulatedAmps;
        }
        else
        {
            var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            forceState = _dryRun && _simulatedForceState is { } force
                ? force
                : (GoeForceState)(Integer(status["frc"]) ?? throw new InvalidOperationException("The go-e status has no force state (frc)."));
            amps = _dryRun && _simulatedAmps is { } simulated
                ? simulated
                : Integer(status["amp"]) ?? throw new InvalidOperationException("The go-e status has no setpoint (amp).");
        }

        // Off is "allowed" only when it is the driver's own pause under a start: what the loop wrote was 0 A.
        var allowed = forceState == GoeForceState.On || (forceState == GoeForceState.Off && _paused && _allowed == true);
        return new EvChargerSettings(allowed, _paused ? 0 : Math.Clamp(amps, 0, Capabilities.MaxCurrentAmps));
    }

    public async Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default)
    {
        var clampedAmps = Math.Clamp(targetAmps, 0, _deviceMaxAmps ?? Capabilities.MaxCurrentAmps);
        if (Math.Abs(clampedAmps - activeAmps) < _currentChangeThresholdAmps)
        {
            return;
        }

        var prefix = _dryRun ? "[DRY RUN] would set " : "";

        if (clampedAmps < Capabilities.MinCurrentAmps)
        {
            // A pause. The charger does not take a setpoint below 6 A, so it is the force state; under a stop it
            // is already off, and is not written again.
            var wasPaused = _paused;
            _paused = true;
            _logger.LogInformation(
                "{Prefix}charger paused: {OldAmps}A -> {NewAmps}A, below go-e's 6 A, so force state Off. {Reason}",
                prefix, activeAmps, clampedAmps, reason);

            if (!wasPaused && _allowed != false)
            {
                await SetAsync([("frc", (int)GoeForceState.Off)], cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Out of a pause the setpoint goes first, in the same request as the force state back on, so the car does
        // not start at whatever amp held before the pause.
        var resume = _paused && _allowed == true;
        _paused = false;

        _logger.LogInformation(
            "{Prefix}charger current: {OldAmps}A -> {NewAmps}A{Resume}. {Reason}",
            prefix, activeAmps, clampedAmps, resume ? ", force state back on" : "", reason);

        await SetAsync(
            resume ? [("amp", clampedAmps), ("frc", (int)GoeForceState.On)] : [("amp", clampedAmps)],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetChargingAllowedAsync(bool allowed, string reason, CancellationToken cancellationToken = default)
    {
        _allowed = allowed;

        // A start under a pause leaves the charger off: the setpoint is 0 A until the loop raises it.
        var forceState = allowed && !_paused ? GoeForceState.On : GoeForceState.Off;
        var prefix = _dryRun ? "[DRY RUN] would set " : "";

        _logger.LogInformation(
            "{Prefix}charger force state: {ForceState} (frc={Value}){Paused}. {Reason}",
            prefix, forceState, (int)forceState, allowed && _paused ? ", held by the pause" : "", reason);

        await SetAsync([("frc", (int)forceState)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The status, recognised when it carries a car state and a firmware version; the reading is then the car
    /// state. A web server that does not have the path, or answers with something else, is not a go-e with API
    /// v2: a result, with the HTTP status as the reading. Only a charger that does not answer at all throws.
    /// </summary>
    public async Task<EvChargerProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var request = ChargerHttp.Read(StatusRequest);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new EvChargerProbeResult(false, (int)response.StatusCode);
        }

        JsonObject? status;
        try
        {
            status = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject;
        }
        catch (JsonException)
        {
            return new EvChargerProbeResult(false, (int)response.StatusCode);
        }

        var recognised = status is not null
            && status.ContainsKey("car")
            && status["fwv"] is JsonValue firmware && firmware.TryGetValue<string>(out var version) && version.Length > 0;

        return new EvChargerProbeResult(recognised, recognised ? Integer(status!["car"]) ?? 0 : (int)response.StatusCode);
    }

    private async Task<JsonObject> ReadStatusAsync(CancellationToken cancellationToken)
    {
        using var request = ChargerHttp.Read(StatusRequest);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("The go-e answered 404 for /api/status: switch on the local HTTP API v2 in the go-e app.");
        }

        response.EnsureSuccessStatusCode();

        JsonObject status;
        try
        {
            status = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject
                ?? throw new InvalidOperationException("The go-e status is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The go-e status is not JSON.", ex);
        }

        // The unit's own ceiling, for the next setpoint: ama, and never above 16 A on an 11 kW unit.
        var ceiling = Integer(status["ama"]) ?? Capabilities.MaxCurrentAmps;
        if (Integer(status["var"]) == 11)
        {
            ceiling = Math.Min(ceiling, 16);
        }

        _deviceMaxAmps = Math.Clamp(ceiling, Capabilities.MinCurrentAmps, Capabilities.MaxCurrentAmps);
        return status;
    }

    // One /api/set of the given keys. Its answer names each key with true, or with the reason it was refused.
    private async Task SetAsync(IReadOnlyList<(string Key, int Value)> values, CancellationToken cancellationToken)
    {
        if (_dryRun)
        {
            foreach (var (key, value) in values)
            {
                if (key == "amp")
                {
                    _simulatedAmps = value;
                }
                else
                {
                    _simulatedForceState = (GoeForceState)value;
                }
            }

            return;
        }

        var query = string.Join('&', values.Select(value => $"{value.Key}={value.Value.ToString(CultureInfo.InvariantCulture)}"));
        using var request = ChargerHttp.Write("api/set?" + query);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The go-e refused {query}: HTTP {(int)response.StatusCode} {body}".TrimEnd());
        }

        JsonObject? answer;
        try
        {
            answer = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            answer = null;
        }

        foreach (var (key, _) in values)
        {
            if (answer?[key] is not JsonValue accepted || !accepted.TryGetValue<bool>(out var ok) || !ok)
            {
                throw new InvalidOperationException($"The go-e did not accept {key}: {answer?[key]?.ToJsonString() ?? "no answer"}.");
            }
        }
    }

    private static int? Integer(JsonNode? node) => Number(node) is { } value ? (int)Math.Round(value) : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;

    private static bool? Boolean(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}

/// <summary>go-e's force state, <c>frc</c>.</summary>
public enum GoeForceState
{
    /// <summary>The charger's own logic decides (its schedule, its PV surplus, its access control).</summary>
    Neutral = 0,

    /// <summary>Charging is off.</summary>
    Off = 1,

    /// <summary>Charging is on.</summary>
    On = 2,
}

/// <summary>Makes <see cref="GoeChargerDriver"/>s.</summary>
public sealed class GoeChargerDriverFactory : IEvChargerDriverFactory
{
    /// <summary>The family's id in configuration.</summary>
    public const string DriverId = "goe";

    private readonly ILoggerFactory _loggers;

    public GoeChargerDriverFactory(ILoggerFactory? loggers = null)
    {
        _loggers = loggers ?? NullLoggerFactory.Instance;
    }

    public string Id => DriverId;

    public string DisplayName => "go-e";

    public EvChargerTransport Transport => EvChargerTransport.Http;

    // Port 80, which is what the charger's web server listens on. The unit id and spacing are Modbus's, unused.
    public ModbusDefaults Defaults { get; } = new(Port: 80, UnitId: 0, MinRequestInterval: TimeSpan.Zero);

    ModbusDefaults? IEvChargerDriverFactory.Defaults => Defaults;

    public EvChargerCapabilities Capabilities => GoeChargerDriver.GoeCapabilities;

    RegisterSnapshotSpec? IEvChargerDriverFactory.Snapshot => null;

    // The driver's one request. Its filter leaves out the serial number, the Wi-Fi networks and the cloud
    // tokens the unfiltered status has, so there is nothing to redact.
    public HttpSnapshotSpec HttpSnapshot { get; } = new([GoeChargerDriver.StatusRequest]);

    HttpSnapshotSpec? IEvChargerDriverFactory.HttpSnapshot => HttpSnapshot;

    public IEvChargerDriver Create(EvChargerConnection connection, EvChargerDriverOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new GoeChargerDriver(connection.RequireHttp(DisplayName), _loggers.CreateLogger<GoeChargerDriver>(), options);
    }
}
