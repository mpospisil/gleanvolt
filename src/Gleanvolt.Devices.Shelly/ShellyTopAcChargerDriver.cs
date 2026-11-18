using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Devices.Shelly;

/// <summary>
/// The Shelly TopAC Portable EV Charger (EVE01-11R: 6–16 A, three phases, 11 kW) over its local Gen2 RPC API
/// (issue #429). Every call is a GET of <c>/rpc/&lt;Method&gt;</c> on one of the virtual components Shelly documents
/// under the owner <c>service:0</c>, and a GET answers with the bare result, <c>{"value": …}</c>.
///
/// <para><b>Reads.</b> <c>work_state</c> is the status, <c>phase_info</c> the power, <c>current_limit</c> the
/// setpoint and <c>start_charging</c> whether the charger may charge. The service's configuration is read once, to
/// warn when Shelly's own load balancing is on: it lowers the current by itself and would fight the control loop.</para>
///
/// <para><b>Writes: two components, nothing else.</b> The setpoint writes <c>current_limit</c>, start and stop write
/// <c>start_charging</c>. Whether the charger keeps either in flash is not documented, so the driver writes only on
/// a change, as the contract already asks.</para>
///
/// <para><b>Pause.</b> The charger's range starts at 6 A, and the control loop pauses by writing 0. So a pause is
/// <c>start_charging</c> false, and the driver remembers that it paused, exactly as go-e's driver does with its force
/// state: settings then read "allowed, 0 A", and the next setpoint of 6 A or more writes the limit and starts again.</para>
///
/// <para><b>Power's unit.</b> evcc reads <c>total_power</c> as kilowatts; Shelly's documentation shows only zeros.
/// While current flows the driver checks the figure against the phases' volts times amps and takes whichever unit
/// agrees, so a wrong guess cannot be out by a factor of a thousand.</para>
///
/// <para><b>Authentication.</b> The host's charger client sends no credentials, so the charger's password has to be
/// off (Shelly's default). A 401 says so.</para>
///
/// <para><b>Expected tier.</b> Every fixture is synthetic, written from Shelly's API documentation, until a tester's
/// snapshot replaces it.</para>
/// </summary>
public sealed class ShellyTopAcChargerDriver : IEvChargerDriver
{
    // Shelly takes string parameters JSON-quoted. Written escaped, as they go on the wire, so the path a recording is
    // keyed by is the one the request sends.
    private const string Owner = "owner=%22service:0%22";

    /// <summary>The charger's state.</summary>
    public static string WorkStateRequest { get; } = $"rpc/Enum.GetStatus?{Owner}&role=%22work_state%22";

    /// <summary>Power, and volts, amps and watts per phase.</summary>
    public static string PhaseInfoRequest { get; } = $"rpc/Object.GetStatus?{Owner}&role=%22phase_info%22";

    /// <summary>The current setpoint, in amps.</summary>
    public static string CurrentLimitRequest { get; } = $"rpc/Number.GetStatus?{Owner}&role=%22current_limit%22";

    /// <summary>Whether the charger may charge.</summary>
    public static string StartChargingRequest { get; } = $"rpc/Boolean.GetStatus?{Owner}&role=%22start_charging%22";

    /// <summary>The service's configuration: auto charge and auto balance, read for a warning and the snapshot.</summary>
    public static string ServiceConfigRequest { get; } = "rpc/Service.GetConfig?id=0";

    /// <summary>Every request the driver reads with.</summary>
    public static IReadOnlyList<string> ReadRequests { get; } =
        [WorkStateRequest, PhaseInfoRequest, CurrentLimitRequest, StartChargingRequest, ServiceConfigRequest];

    /// <summary>The only components the driver ever writes: the setpoint and start/stop.</summary>
    public static IReadOnlyList<string> WrittenRoles { get; } = ["current_limit", "start_charging"];

    // While less than this flows (V × A over the phases), power's unit cannot be told apart and kW is assumed.
    private const double UnitCheckMinimumWatts = 100;

    private readonly HttpClient _http;
    private readonly ILogger<ShellyTopAcChargerDriver> _logger;
    private readonly bool _dryRun;
    private readonly int _currentChangeThresholdAmps;

    // What the control loop last asked for, which the device cannot hold on its own: whether it may charge (null
    // until the first start or stop), and whether the setpoint is a pause.
    private bool? _allowed;
    private bool _paused;

    // The service's configuration is checked once per driver: it is the owner's, and changes rarely.
    private bool _configChecked;

    // Dry-run only: what the charger would now have.
    private int? _simulatedAmps;
    private bool? _simulatedStart;

    /// <param name="http">The charger's client, base address set, owned by the host.</param>
    /// <param name="logger">For the writes and the warnings. Null logs nothing.</param>
    /// <param name="options">Dry-run and the setpoint's hysteresis. Null uses the defaults.</param>
    public ShellyTopAcChargerDriver(HttpClient http, ILogger<ShellyTopAcChargerDriver>? logger = null, EvChargerDriverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _logger = logger ?? NullLogger<ShellyTopAcChargerDriver>.Instance;
        options ??= new EvChargerDriverOptions();
        _dryRun = options.DryRun;
        _currentChangeThresholdAmps = Math.Max(1, options.CurrentChangeThresholdAmps);
    }

    /// <summary>6–16 A on three phases, as the EVE01-11R is rated; no phase switching in the API.</summary>
    public static EvChargerCapabilities TopAcCapabilities { get; } = new(
        MinCurrentAmps: 6,
        MaxCurrentAmps: 16,
        PauseCurrentAmps: 0,
        Phases: 3,
        CanSwitchPhases: false,
        HasUseMode: false);

    public EvChargerCapabilities Capabilities => TopAcCapabilities;

    /// <summary><c>work_state</c>'s text as a <see cref="ShellyWorkState"/>; anything else is Unknown.</summary>
    public static ShellyWorkState WorkStateOf(string? value) => value switch
    {
        "charger_free" => ShellyWorkState.Free,
        "charger_insert" => ShellyWorkState.Insert,
        "charger_wait" => ShellyWorkState.Wait,
        "charger_charging" => ShellyWorkState.Charging,
        "charger_pause" => ShellyWorkState.Pause,
        "charger_complete" => ShellyWorkState.Complete,
        "charger_end" => ShellyWorkState.End,
        "charger_error" => ShellyWorkState.Error,
        _ => ShellyWorkState.Unknown,
    };

    /// <summary>
    /// The work state and <c>start_charging</c> as the contract's status. Waiting and finished are the car's doing
    /// only while the charger may charge; otherwise the charger is holding it, which is never "the car is full".
    /// A pause is the charger's, whoever asked for it.
    /// </summary>
    public static EvChargerStatus StatusOf(ShellyWorkState state, bool allowed) => state switch
    {
        ShellyWorkState.Free => EvChargerStatus.Available,
        ShellyWorkState.Insert => EvChargerStatus.Preparing,
        ShellyWorkState.Wait => allowed ? EvChargerStatus.Preparing : EvChargerStatus.SuspendedEvse,
        ShellyWorkState.Charging => EvChargerStatus.Charging,
        ShellyWorkState.Pause => EvChargerStatus.SuspendedEvse,
        ShellyWorkState.Complete or ShellyWorkState.End => allowed ? EvChargerStatus.SuspendedEv : EvChargerStatus.SuspendedEvse,
        ShellyWorkState.Error => EvChargerStatus.Faulted,
        _ => EvChargerStatus.Unknown,
    };

    /// <summary>
    /// <c>phase_info</c>'s total power in watts. Read as kilowatts (evcc's reading), unless volts times amps over the
    /// phases says it is already watts; below <see cref="UnitCheckMinimumWatts"/> there is too little to tell.
    /// </summary>
    public static double WattsOf(JsonObject phaseInfo)
    {
        ArgumentNullException.ThrowIfNull(phaseInfo);
        var total = Number(phaseInfo["total_power"])
            ?? throw new InvalidOperationException("The Shelly's phase_info has no total_power.");

        var reference = 0.0;
        foreach (var phase in new[] { "phase_a", "phase_b", "phase_c" })
        {
            if (phaseInfo[phase] is JsonObject values && Number(values["voltage"]) is { } volts && Number(values["current"]) is { } amps)
            {
                reference += volts * amps;
            }
        }

        var asWatts = reference >= UnitCheckMinimumWatts && Math.Abs(total - reference) < Math.Abs(total * 1000 - reference);
        return Math.Max(0, asWatts ? total : total * 1000);
    }

    public async Task<EvChargerReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        var state = await ReadValueAsync(WorkStateRequest, cancellationToken).ConfigureAwait(false);
        var workState = state is JsonValue text && text.TryGetValue<string>(out var name)
            ? WorkStateOf(name)
            : throw new InvalidOperationException("The Shelly's work_state is not a text: is this a TopAC EV charger?");

        var phaseInfo = await ReadValueAsync(PhaseInfoRequest, cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidOperationException("The Shelly's phase_info is not an object.");

        var start = await TryReadValueAsync(StartChargingRequest, cancellationToken).ConfigureAwait(false);
        var amps = await TryReadValueAsync(CurrentLimitRequest, cancellationToken).ConfigureAwait(false);
        await CheckConfigurationOnceAsync(cancellationToken).ConfigureAwait(false);

        return new EvChargerReading(
            StatusOf(workState, Boolean(start) ?? false),
            WattsOf(phaseInfo),
            _paused ? 0 : Integer(amps) is { } limit ? Math.Clamp(limit, 0, Capabilities.MaxCurrentAmps) : null,
            Mode: null);
    }

    public async Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var start = _dryRun && _simulatedStart is { } simulatedStart
            ? simulatedStart
            : Boolean(await ReadValueAsync(StartChargingRequest, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidOperationException("The Shelly's start_charging is not true or false.");

        var amps = _dryRun && _simulatedAmps is { } simulatedAmps
            ? simulatedAmps
            : Integer(await ReadValueAsync(CurrentLimitRequest, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidOperationException("The Shelly's current_limit is not a number.");

        // Not started is "allowed" only when it is the driver's own pause under a start: what the loop wrote was 0 A.
        var allowed = start || (_paused && _allowed == true);
        return new EvChargerSettings(allowed, _paused ? 0 : Math.Clamp(amps, 0, Capabilities.MaxCurrentAmps));
    }

    public async Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default)
    {
        var clampedAmps = Math.Clamp(targetAmps, 0, Capabilities.MaxCurrentAmps);
        if (Math.Abs(clampedAmps - activeAmps) < _currentChangeThresholdAmps)
        {
            return;
        }

        var prefix = _dryRun ? "[DRY RUN] would set " : "";

        if (clampedAmps < Capabilities.MinCurrentAmps)
        {
            // A pause. The charger takes nothing below 6 A, so it is start_charging off; under a stop it is already
            // off, and is not written again.
            var wasPaused = _paused;
            _paused = true;
            _logger.LogInformation(
                "{Prefix}charger paused: {OldAmps}A -> {NewAmps}A, below the Shelly's 6 A, so start_charging off. {Reason}",
                prefix, activeAmps, clampedAmps, reason);

            if (!wasPaused && _allowed != false)
            {
                await SetStartAsync(false, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Out of a pause the limit goes first, then the start, so the car does not start at whatever limit held
        // before the pause.
        var resume = _paused && _allowed == true;
        _paused = false;

        _logger.LogInformation(
            "{Prefix}charger current: {OldAmps}A -> {NewAmps}A{Resume}. {Reason}",
            prefix, activeAmps, clampedAmps, resume ? ", start_charging back on" : "", reason);

        await SetLimitAsync(clampedAmps, cancellationToken).ConfigureAwait(false);
        if (resume)
        {
            await SetStartAsync(true, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SetChargingAllowedAsync(bool allowed, string reason, CancellationToken cancellationToken = default)
    {
        _allowed = allowed;

        // A start under a pause leaves the charger off: the setpoint is 0 A until the loop raises it.
        var start = allowed && !_paused;
        var prefix = _dryRun ? "[DRY RUN] would set " : "";

        _logger.LogInformation(
            "{Prefix}charger start_charging: {Start}{Paused}. {Reason}",
            prefix, start, allowed && _paused ? ", held by the pause" : "", reason);

        await SetStartAsync(start, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The work state, recognised when it is one of the charger's own (<c>charger_…</c>); the reading is then its
    /// <see cref="ShellyWorkState"/> number. Another Shelly, which has no such component, or any other web server,
    /// is not this charger: a result, with the HTTP status as the reading. Only a charger that does not answer at
    /// all throws.
    /// </summary>
    public async Task<EvChargerProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var request = ChargerHttp.Read(WorkStateRequest);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new EvChargerProbeResult(false, (int)response.StatusCode);
        }

        JsonNode? answer;
        try
        {
            answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return new EvChargerProbeResult(false, (int)response.StatusCode);
        }

        var state = answer is JsonObject status && status["value"] is JsonValue value && value.TryGetValue<string>(out var text)
            ? WorkStateOf(text)
            : ShellyWorkState.Unknown;

        return state == ShellyWorkState.Unknown
            ? new EvChargerProbeResult(false, (int)response.StatusCode)
            : new EvChargerProbeResult(true, (int)state);
    }

    // One component's value: the GET's answer is {"value": …, "source": …, "last_update_ts": …}.
    private async Task<JsonNode?> ReadValueAsync(string path, CancellationToken cancellationToken)
    {
        var answer = await ReadJsonAsync(path, cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidOperationException($"The Shelly's answer to {path} is not a JSON object.");

        return answer.TryGetPropertyValue("value", out var value)
            ? value
            : throw new InvalidOperationException($"The Shelly's answer to {path} has no value.");
    }

    // A value the reading can do without: null when it could not be read, the contract's "best-effort".
    private async Task<JsonNode?> TryReadValueAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadValueAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<JsonNode?> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = ChargerHttp.Read(path);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                "The Shelly asks for a password (HTTP 401). Gleanvolt does not send one: switch off the charger's "
                + "authentication in its web interface or the Shelly app.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The Shelly answered {path} with HTTP {(int)response.StatusCode}: {body}".TrimEnd());
        }

        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The Shelly's answer to {path} is not JSON.", ex);
        }
    }

    // The owner's settings that change what the driver can do. Auto balance (a Shelly EM's load management) lowers
    // the current by itself; auto charge starts a car as it is plugged in, which the controller copes with as it does
    // on every charger that does so, but which leaves an Off charger charging. Best-effort: a charger that will not
    // say is still driven.
    private async Task CheckConfigurationOnceAsync(CancellationToken cancellationToken)
    {
        if (_configChecked)
        {
            return;
        }

        JsonObject? config;
        try
        {
            config = await ReadJsonAsync(ServiceConfigRequest, cancellationToken).ConfigureAwait(false) as JsonObject;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return;
        }

        _configChecked = true;

        if (config?["auto_balance"] is JsonObject balance && Boolean(balance["enable"]) == true)
        {
            _logger.LogWarning(
                "The Shelly's auto balance is on: it lowers the charging current by itself, against Gleanvolt's "
                + "setpoint. Switch it off in the Shelly app.");
        }

        if (Boolean(config?["auto_charge"]) == true)
        {
            _logger.LogInformation(
                "The Shelly's auto charge is on: a car starts charging as it is plugged in, before any mode is picked.");
        }
    }

    private Task SetLimitAsync(int amps, CancellationToken cancellationToken)
    {
        if (_dryRun)
        {
            _simulatedAmps = amps;
            return Task.CompletedTask;
        }

        return SetAsync($"rpc/Number.Set?{Owner}&role=%22current_limit%22&value={amps.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
    }

    private Task SetStartAsync(bool start, CancellationToken cancellationToken)
    {
        if (_dryRun)
        {
            _simulatedStart = start;
            return Task.CompletedTask;
        }

        return SetAsync($"rpc/Boolean.Set?{Owner}&role=%22start_charging%22&value={(start ? "true" : "false")}", cancellationToken);
    }

    // A set answers null when it is taken; a refusal is an HTTP error with {"code": …, "message": …}.
    private async Task SetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = ChargerHttp.Write(path);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The Shelly refused {path}: HTTP {(int)response.StatusCode} {body}".TrimEnd());
        }

        JsonNode? answer;
        try
        {
            answer = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            answer = null;
        }

        if (answer is JsonObject error && error.ContainsKey("code") && error.ContainsKey("message"))
        {
            throw new InvalidOperationException($"The Shelly refused {path}: {error["message"]}");
        }
    }

    private static int? Integer(JsonNode? node) => Number(node) is { } value ? (int)Math.Round(value) : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;

    private static bool? Boolean(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}

/// <summary>
/// The TopAC's <c>work_state</c>, numbered for the probe's reading (Settings → "Test connection" shows the number).
/// The texts are the ones Shelly's charger reports; Shelly documents only <c>charger_free</c>, and evcc the rest.
/// </summary>
public enum ShellyWorkState
{
    /// <summary>A text the driver does not know.</summary>
    Unknown = 0,

    /// <summary><c>charger_free</c>: no car.</summary>
    Free = 1,

    /// <summary><c>charger_insert</c>: a car has just been plugged in.</summary>
    Insert = 2,

    /// <summary><c>charger_wait</c>: a car is plugged in, waiting to start.</summary>
    Wait = 3,

    /// <summary><c>charger_charging</c>: delivering.</summary>
    Charging = 4,

    /// <summary><c>charger_pause</c>: the charger has paused.</summary>
    Pause = 5,

    /// <summary><c>charger_complete</c>: finished, the car still plugged in.</summary>
    Complete = 6,

    /// <summary><c>charger_end</c>: the session has ended, the car still plugged in.</summary>
    End = 7,

    /// <summary><c>charger_error</c>: a fault.</summary>
    Error = 8,
}

/// <summary>Makes <see cref="ShellyTopAcChargerDriver"/>s.</summary>
public sealed class ShellyTopAcChargerDriverFactory : IEvChargerDriverFactory
{
    /// <summary>The family's id in configuration.</summary>
    public const string DriverId = "shelly-topac";

    private readonly ILoggerFactory _loggers;

    public ShellyTopAcChargerDriverFactory(ILoggerFactory? loggers = null)
    {
        _loggers = loggers ?? NullLoggerFactory.Instance;
    }

    public string Id => DriverId;

    public string DisplayName => "Shelly TopAC";

    public EvChargerTransport Transport => EvChargerTransport.Http;

    // Port 80, which is what the charger's web server listens on. The unit id and spacing are Modbus's, unused.
    public ModbusDefaults Defaults { get; } = new(Port: 80, UnitId: 0, MinRequestInterval: TimeSpan.Zero);

    ModbusDefaults? IEvChargerDriverFactory.Defaults => Defaults;

    public EvChargerCapabilities Capabilities => ShellyTopAcChargerDriver.TopAcCapabilities;

    RegisterSnapshotSpec? IEvChargerDriverFactory.Snapshot => null;

    // The driver's own reads. None carries the device id, the MAC or the Wi-Fi settings (Shelly.GetDeviceInfo and
    // Shelly.GetConfig do, and are not read), so there is nothing to redact.
    public HttpSnapshotSpec HttpSnapshot { get; } = new(ShellyTopAcChargerDriver.ReadRequests);

    HttpSnapshotSpec? IEvChargerDriverFactory.HttpSnapshot => HttpSnapshot;

    public IEvChargerDriver Create(EvChargerConnection connection, EvChargerDriverOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ShellyTopAcChargerDriver(connection.RequireHttp(DisplayName), _loggers.CreateLogger<ShellyTopAcChargerDriver>(), options);
    }
}
