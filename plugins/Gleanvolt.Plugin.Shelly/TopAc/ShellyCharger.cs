using Gleanvolt.Providers.Client.Charger;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;

namespace Gleanvolt.Plugin.Shelly.TopAc;

/// <summary>What <c>total_power</c> was found to be in.</summary>
public enum PowerUnit
{
    /// <summary>Not settled yet: nothing has drawn enough to tell.</summary>
    Unknown,

    /// <summary>Watts.</summary>
    Watts,

    /// <summary>Kilowatts, as evcc reads it.</summary>
    Kilowatts,
}

/// <summary>
/// The Shelly TopAC Portable EV Charger (EVE01-11R: 6–16 A, three phases, 11 kW) for Gleanvolt (#429), as the
/// charger behind the plugin (#435). Gleanvolt asks for a reading every poll and sends two commands; this maps
/// them onto the charger's RPC components.
///
/// <para><b>Reads.</b> Four calls: <c>work_state</c> is the status, <c>phase_info</c> the power,
/// <c>current_limit</c> the setpoint and <c>start_charging</c> the switch. Whether waiting, complete and ended are
/// the car's doing or the charger's is <c>start_charging</c>: while it is off, the charger is holding the car, which
/// is never "the car is full".</para>
///
/// <para><b>Pause.</b> The charger takes nothing below 6 A, and Gleanvolt pauses by setting 0 A. So a pause is
/// <c>start_charging=false</c>, and the plugin remembers that it paused: readings then say "allowed, 0 A", which is
/// what Gleanvolt set, and the next setpoint of 6 A or more writes the limit and turns charging back on. A stop
/// while paused stays stopped. The memory does not survive a restart of the plugin; after one, a charger it had
/// paused reads as stopped, as go-e's driver does.</para>
///
/// <para><b>Units.</b> Shelly's documentation shows <c>total_power</c> only as zeros, and evcc treats it as kW. Rather
/// than trust either, the plugin compares it with Σ V·I over the phases once the car draws enough to tell, and keeps
/// what it found. Until then the power is Σ V·I.</para>
///
/// <para><b>Write wear.</b> Whether <c>current_limit</c> is written to flash is not documented: the plugin writes it
/// only when it differs from what the charger last said, on top of Gleanvolt's own threshold.</para>
/// </summary>
public sealed class ShellyCharger : IChargerSource
{
    /// <summary>The lowest setpoint the charger takes (IEC 61851).</summary>
    public const int MinCurrentAmps = 6;

    // Σ V·I above this tells watts from kilowatts beyond doubt: they differ a thousandfold.
    private const double UnitDecidingWatts = 500;

    private readonly ShellyRpcClient _rpc;
    private readonly ShellyOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ShellyCharger> _logger;

    // What Gleanvolt last asked for, which the charger cannot hold on its own: whether it may charge (null until the
    // first start or stop), and whether the setpoint is a pause.
    private bool? _allowed;
    private bool _paused;
    private int? _lastLimit;

    /// <summary>
    /// 6 A up to <see cref="ShellyOptions.MaxCurrentAmps"/> on three phases, paused with its start/stop switch rather
    /// than a setpoint (#440).
    /// </summary>
    public ChargerCapabilities Capabilities => new(MinCurrentAmps, _options.MaxCurrentAmps, Phases: 3);
    private DateTimeOffset? _ownerSettingsReadAt;

    public ShellyCharger(ShellyRpcClient rpc, ShellyOptions options, TimeProvider time, ILogger<ShellyCharger> logger)
    {
        _rpc = rpc;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised when the status, the last reading or the owner's settings change.</summary>
    public event Action? Changed;

    /// <summary>The newest reading, or null.</summary>
    public ChargerReading? LastReading { get; private set; }

    /// <summary>The phases of the newest reading.</summary>
    public PowerInfo? LastPower { get; private set; }

    /// <summary>The charger's own <c>work_state</c> in the newest reading.</summary>
    public string? LastWorkState { get; private set; }

    /// <summary>The owner's settings, read every <see cref="ShellyOptions.OwnerSettingsInterval"/>; null until read.</summary>
    public OwnerSettings? OwnerSettings { get; private set; }

    /// <summary>What <c>total_power</c> was found to be in.</summary>
    public PowerUnit Unit { get; private set; }

    /// <summary>Whether the plugin holds a pause.</summary>
    public bool Paused => _paused;

    /// <summary>How the plugin is doing, as told to Gleanvolt; null before the first read.</summary>
    public ProviderStatus? Status { get; private set; }

    /// <summary>What is wrong, in the page's language; null when healthy.</summary>
    public Words? StatusWords { get; private set; }

    /// <summary>The charger's <c>work_state</c> as the contract's state.</summary>
    /// <param name="workState">As the charger sends it.</param>
    /// <param name="startCharging">Whether the charger's switch is on: off, it is holding the car.</param>
    public static ChargerState StateOf(string? workState, bool startCharging) => workState switch
    {
        "charger_free" => ChargerState.Available,
        "charger_insert" => ChargerState.Preparing,
        "charger_wait" => startCharging ? ChargerState.Preparing : ChargerState.SuspendedEvse,
        "charger_charging" => ChargerState.Charging,
        "charger_pause" => ChargerState.SuspendedEvse,
        "charger_complete" => startCharging ? ChargerState.SuspendedEv : ChargerState.SuspendedEvse,
        "charger_end" => startCharging ? ChargerState.Finishing : ChargerState.SuspendedEvse,
        "charger_error" => ChargerState.Faulted,
        _ => ChargerState.Unknown,
    };

    /// <summary>
    /// The power in watts from <paramref name="info"/>, settling <paramref name="unit"/> once Σ V·I is large
    /// enough to tell watts from kilowatts. Never negative.
    /// </summary>
    public static double PowerWatts(PowerInfo info, ref PowerUnit unit)
    {
        ArgumentNullException.ThrowIfNull(info);
        var measured = info.Phases.Any(phase => phase.Voltage is not null && phase.Current is not null)
            ? info.Phases.Sum(phase => (phase.Voltage ?? 0) * (phase.Current ?? 0))
            : info.Phases.Any(phase => phase.Power is not null) ? info.Phases.Sum(phase => phase.Power ?? 0) : (double?)null;

        if (info.TotalPower is { } total)
        {
            if (measured is { } check && check >= UnitDecidingWatts)
            {
                unit = Math.Abs(total * 1000 - check) < Math.Abs(total - check) ? PowerUnit.Kilowatts : PowerUnit.Watts;
            }

            if (unit != PowerUnit.Unknown)
            {
                return Math.Max(0, unit == PowerUnit.Kilowatts ? total * 1000 : total);
            }
        }

        // Not settled, or no total: the phases, and failing those evcc's reading of the total.
        return Math.Max(0, measured ?? (info.TotalPower ?? 0) * 1000);
    }

    public async Task<ChargerReading> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var workState = await _rpc.GetWorkStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var power = await _rpc.GetPowerAsync(cancellationToken).ConfigureAwait(false);
            var limit = await _rpc.GetCurrentLimitAsync(cancellationToken).ConfigureAwait(false);
            var on = await _rpc.GetStartChargingAsync(cancellationToken).ConfigureAwait(false);
            await ReadOwnerSettingsAsync(cancellationToken).ConfigureAwait(false);

            var unit = Unit;
            var watts = PowerWatts(power, ref unit);
            if (unit != Unit)
            {
                _logger.LogInformation("The charger's total_power is in {Unit}: it matches the phases' V·I.", unit);
                Unit = unit;
            }

            _lastLimit = limit;
            LastWorkState = workState;
            LastPower = power;
            LastReading = new ChargerReading(
                _time.GetUtcNow(),
                StateOf(workState, on),
                watts,
                CurrentAmps: _paused ? 0 : limit,
                // Off is "allowed" only when it is the plugin's own pause under a start: what Gleanvolt set was 0 A.
                ChargingAllowed: on || (_paused && _allowed == true),
                MaxCurrentAmps: _options.MaxCurrentAmps);

            Healthy();
            return LastReading;
        }
        catch (RpcFailedException ex)
        {
            Failed(ex);
            throw;
        }
    }

    public async Task SetCurrentAsync(int amps, CancellationToken cancellationToken)
    {
        try
        {
            if (amps < MinCurrentAmps)
            {
                // A pause. Under a stop the switch is already off, and is not written again.
                var wasPaused = _paused;
                _paused = true;

                if (!wasPaused && _allowed != false)
                {
                    await _rpc.SetStartChargingAsync(false, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            var limit = Math.Min(amps, _options.MaxCurrentAmps);
            if (limit != _lastLimit)
            {
                await _rpc.SetCurrentLimitAsync(limit, cancellationToken).ConfigureAwait(false);
                _lastLimit = limit;
            }

            // Out of a pause the limit goes first, so the car does not start at whatever held before it.
            if (_paused)
            {
                _paused = false;
                if (_allowed == true)
                {
                    await _rpc.SetStartChargingAsync(true, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    public async Task SetChargingAllowedAsync(bool allowed, CancellationToken cancellationToken)
    {
        _allowed = allowed;

        try
        {
            // A start under a pause leaves the charger off: the setpoint is 0 A until Gleanvolt raises it.
            await _rpc.SetStartChargingAsync(allowed && !_paused, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The address changed on the page: what was learnt about the old charger is forgotten.</summary>
    public void AddressChanged()
    {
        LastReading = null;
        LastPower = null;
        LastWorkState = null;
        OwnerSettings = null;
        _ownerSettingsReadAt = null;
        _lastLimit = null;
        Unit = PowerUnit.Unknown;
        Changed?.Invoke();
    }

    // The owner's settings, now and then: they change in the Shelly app, not here. A failure here is not a failed
    // reading; the warning simply waits for the next try.
    private async Task ReadOwnerSettingsAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_ownerSettingsReadAt is { } at && now - at < _options.OwnerSettingsInterval)
        {
            return;
        }

        _ownerSettingsReadAt = now;

        try
        {
            OwnerSettings = await _rpc.GetOwnerSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RpcFailedException ex)
        {
            _logger.LogDebug("Could not read the charger's own settings ({Reason}).", ex.Message);
        }
    }

    private void Healthy()
    {
        // Auto balance lowers the current on its own and would fight Gleanvolt's control loop.
        if (OwnerSettings is { AutoBalance: true })
        {
            SetStatus(new ProviderStatus(false, PageText.AutoBalanceOn.En, NeedsOwner: true, Messages: PageText.AutoBalanceOn.All), PageText.AutoBalanceOn);
        }
        else
        {
            SetStatus(new ProviderStatus(true), null);
        }
    }

    private void Failed(RpcFailedException ex)
    {
        _logger.LogWarning("Reading the charger failed: {Reason}", ex.Message);

        switch (ex.Failure)
        {
            case RpcFailure.Unauthorized:
                SetStatus(Unhealthy(PageText.Unauthorized, ProviderStatusCodes.Refused, needsOwner: true), PageText.Unauthorized);
                break;
            case RpcFailure.NotTopAc:
                SetStatus(Unhealthy(PageText.NotTopAc.Format(ex.Message), ProviderStatusCodes.ServiceChanged, needsOwner: true), PageText.NotTopAc.Format(ex.Message));
                break;
            case RpcFailure.NoAddress:
                SetStatus(Unhealthy(PageText.NoAddress, ProviderStatusCodes.NotSetUp, needsOwner: true), PageText.NoAddress);
                break;
            default:
                SetStatus(Unhealthy(PageText.Unreachable.Format(ex.Message), ProviderStatusCodes.Unreachable, needsOwner: false), PageText.Unreachable.Format(ex.Message));
                break;
        }
    }

    private static ProviderStatus Unhealthy(Words what, string code, bool needsOwner) =>
        new(false, what.En, NeedsOwner: needsOwner, Code: code, Messages: what.All);

    // Every read raises Changed: the page shows the reading, which changes every poll. The reporter tells
    // Gleanvolt only when the status itself changed.
    private void SetStatus(ProviderStatus status, Words? words)
    {
        Status = status;
        StatusWords = words;
        Changed?.Invoke();
    }
}

/// <summary>
/// Tells Gleanvolt how the plugin is doing (#429): after every hello, since a new session starts with no status
/// in Gleanvolt, and whenever the status changes, not on every poll.
/// </summary>
public sealed class StatusReporter : IHostedService
{
    private readonly ChargerProviderConnection _connection;
    private readonly ShellyCharger _charger;
    private readonly ILogger<StatusReporter> _logger;
    private ProviderStatus? _reported;

    public StatusReporter(ChargerProviderConnection connection, ShellyCharger charger, ILogger<StatusReporter> logger)
    {
        _connection = connection;
        _charger = charger;
        _logger = logger;

        // Here rather than at start: the first hello can come before it.
        connection.Welcomed += welcome => _ = ReportAsync(force: true);
        charger.Changed += () => _ = ReportAsync(force: false);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ReportAsync(bool force)
    {
        if (_charger.Status is not { } status || (!force && Same(status, _reported)))
        {
            return;
        }

        try
        {
            _reported = status;
            await _connection.ReportStatusAsync(status).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _reported = null;
            _logger.LogDebug("Could not report the status to Gleanvolt ({Reason}).", ex.Message);
        }
    }

    // The messages are a dictionary, so a record's equality would call every poll a change.
    private static bool Same(ProviderStatus status, ProviderStatus? other) =>
        other is not null && status.Healthy == other.Healthy && status.NeedsOwner == other.NeedsOwner
        && status.Code == other.Code && status.Message == other.Message;
}
