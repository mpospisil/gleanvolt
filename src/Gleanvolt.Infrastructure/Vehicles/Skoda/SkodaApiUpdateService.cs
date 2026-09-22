using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.Skoda;

/// <summary>
/// The MyŠkoda Public API behind the vehicle-feed contract (issue #193) — for a Škoda, the part
/// <c>vw-website</c> plays for the ID.4.
///
/// <para><b>One request per reading:</b> <c>GET /api/v1/vehicles/{vin}?include=charging</c>. The
/// capture time is on the charging part, so nothing else is asked.</para>
///
/// <para><b>It keeps asking between charges</b>, where volkswagen.de does not. That one spends a login
/// session and is asked sparingly; a key has no session, and reading this API reads Škoda's cloud
/// rather than waking the car — so an idle read costs one request of the quota and nothing else, and
/// a Škoda has no other live source for the state of charge a plan starts from.</para>
///
/// <para><b>The quota is the service's to keep.</b> Twenty requests an hour per VIN, shared with the
/// sign-in and with anybody pressing <i>Ask the car</i> (#168). The clock takes four an hour idle and
/// twelve while charging, stops for the window once only <see cref="ReservedForAsks"/> are left, and
/// honours <c>Retry-After</c> exactly — reading <c>RateLimit-Remaining</c> from every response rather
/// than counting locally.</para>
///
/// <para><b>A key problem stops the feed until a new key arrives</b>, not until a restart: the page
/// writes the key into the same <see cref="SkodaApiKeyStore"/> this reads, <see cref="Health"/> stops
/// being blocked the moment it does, and the worker resumes on that.</para>
/// </summary>
public sealed class SkodaApiUpdateService(
    SkodaApiOptions options,
    SkodaApiKeyStore store,
    SkodaApiClient client,
    string vehicleId,
    ChargeControlStatusHolder? status = null,
    TimeProvider? time = null,
    ILogger<SkodaApiUpdateService>? logger = null) : IVehicleUpdateService
{
    /// <summary>
    /// Requests the clock leaves in the window for a person: an <i>Ask the car</i> or two and a key
    /// being pasted. On-demand asks may still spend them; only the clock waits for the reset.
    /// </summary>
    public const int ReservedForAsks = 3;

    /// <summary>
    /// The shortest gap between two requests. The clock and an on-demand ask landing in the same
    /// minute share one answer rather than spending two requests on the same capture.
    /// </summary>
    public static readonly TimeSpan MinimumSpacing = TimeSpan.FromMinutes(1);

    /// <summary>How long before the key's expiry the feed starts saying so.</summary>
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(7);

    /// <summary>
    /// The least a car that declined a request is left alone. It declines to protect its 12 V battery
    /// or its sleep, and asking again in a minute is exactly what it is asking us not to do.
    /// </summary>
    public static readonly TimeSpan VehicleDeclinedBackoff = TimeSpan.FromMinutes(30);

    /// <summary>How far repeated failures may push the idle cadence out.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Written by the fetch loop and read by a Blazor render and the Home Assistant tick; published
    // references rather than cached, as VwGroupUpdateService's are.
    private volatile VehicleSourceHealth _health = VehicleSourceHealth.Starting;
    private volatile VehicleSourceHealth? _blocked;
    private SkodaApiOutcome _blockedBy;
    private volatile VehicleState? _lastReading;

    private int _blockedAtVersion = -1;
    private int _consecutiveFailures;
    private DateTimeOffset? _lastRequestAt;
    private DateTimeOffset? _notBefore;
    private DateTimeOffset? _lastExpiryWarningAt;

    public string VehicleId => string.IsNullOrWhiteSpace(vehicleId) ? "the car" : vehicleId.Trim();

    public string Manufacturer => options.SourceId;

    public string DisplayName => "MyŠkoda";

    /// <summary>
    /// The card's sentence for a key that is missing or no longer good (#212). Null for a VIN Škoda
    /// does not know: no key fixes that, and <see cref="Health"/> already says to check the setting.
    /// </summary>
    public string? OwnerAction =>
        Health.IsBlocked && (store.Current is null || _blockedBy != SkodaApiOutcome.VehicleNotFound)
            ? SkodaApiSentences.OwnerAction
            : null;

    public VehicleSourceHealth Health
    {
        get
        {
            if (store.Current is not { } key)
            {
                return VehicleSourceHealth.NeedsOwner(SkodaApiSentences.NoKey);
            }

            // Blocked on the key it was refused with. A key pasted since is a different key, and the
            // feed is free to try it.
            if (_blocked is { } blocked && Volatile.Read(ref _blockedAtVersion) == store.Version)
            {
                return blocked;
            }

            var health = _health;

            if (health.State == VehicleSourceState.Ok && ExpiringSoon(key) is { } warning)
            {
                return VehicleSourceHealth.Degraded(warning);
            }

            return health;
        }
    }

    public TimeSpan NextDelay
    {
        get
        {
            if (Health.IsBlocked)
            {
                return Timeout.InfiniteTimeSpan;
            }

            var now = _time.GetUtcNow();
            var delay = IsCharging ? options.ChargingPollInterval : options.IdlePollInterval;

            if (_consecutiveFailures > 0)
            {
                delay = Longest(delay, Backoff(_consecutiveFailures));
            }

            if (_notBefore is { } notBefore && notBefore > now)
            {
                delay = Longest(delay, notBefore - now);
            }

            // The clock yields the end of the window to people. An ask can still spend it.
            if (client.Quota is { Remaining: <= ReservedForAsks, ResetsAt: { } resets } && resets > now)
            {
                delay = Longest(delay, resets - now);
            }

            return delay;
        }
    }

    /// <summary>
    /// A charge Gleanvolt is running, or the car's own last word that it is charging — the latter so a
    /// charge started from the car or a public charger is followed too.
    /// </summary>
    private bool IsCharging =>
        (status?.Current is { } current
         && current.Mode != ChargeControlMode.Off
         && current.CarConnected
         && !current.SessionCompleted)
        || _lastReading?.ChargeState == VehicleChargeState.Charging;

    public async Task<VehicleState?> FetchAsync(CancellationToken cancellationToken)
    {
        if (store.Current is not { } key || Health.IsBlocked)
        {
            // No key, or the key already refused: nothing is sent. The page is where either is fixed.
            return null;
        }

        var version = store.Version;
        var now = _time.GetUtcNow();

        if (_notBefore is { } notBefore && notBefore > now)
        {
            // Retry-After is a promise kept for asks as well as the clock; the health already says why.
            return null;
        }

        if (client.Quota is { Remaining: <= 0, ResetsAt: { } resets } && resets > now)
        {
            _health = VehicleSourceHealth.Degraded(
                $"This hour's Škoda requests are used up; the next is available at {resets.LocalDateTime:HH:mm}. "
                + "The last reading stands.");
            return null;
        }

        if (_lastRequestAt is { } last && now - last < MinimumSpacing && _lastReading is { } recent)
        {
            return recent;
        }

        _lastRequestAt = now;
        var response = await client.GetVehicleAsync(key.Key, "charging", cancellationToken).ConfigureAwait(false);

        switch (response.Outcome)
        {
            case SkodaApiOutcome.Ok:
                return Read(response, now);

            case SkodaApiOutcome.KeyExpired
                or SkodaApiOutcome.KeyUnknown
                or SkodaApiOutcome.KeyNotAuthorized
                or SkodaApiOutcome.VehicleNotFound:
                _blockedBy = response.Outcome;
                _blocked = VehicleSourceHealth.NeedsOwner(SkodaApiSentences.ForFeed(response.Outcome, options.Vin));
                Volatile.Write(ref _blockedAtVersion, version);

                _logger.LogWarning(
                    "The Škoda feed for {Vehicle} needs you ({Outcome}): {Reason} Nothing is asked until a new key "
                    + "is pasted.",
                    VehicleId, response.Outcome, _blocked.Message);
                return null;

            case SkodaApiOutcome.RateLimited:
                _notBefore = now + (response.RetryAfter
                                    ?? (client.Quota?.ResetsAt is { } reset && reset > now
                                        ? reset - now
                                        : options.IdlePollInterval));

                _health = VehicleSourceHealth.Degraded(
                    $"Škoda's hourly request quota is used up; asking again at {_notBefore.Value.LocalDateTime:HH:mm}. "
                    + "The last reading stands.");

                _logger.LogWarning("The Škoda feed for {Vehicle} hit the rate limit; waiting until {NotBefore:u}.",
                    VehicleId, _notBefore);
                return null;

            case SkodaApiOutcome.VehicleNotAccepting:
                _notBefore = now + Longest(response.RetryAfter ?? TimeSpan.Zero, VehicleDeclinedBackoff);

                _health = VehicleSourceHealth.Degraded(
                    "The car itself is declining requests — usually a low 12 V battery or deep sleep, not "
                    + $"Gleanvolt or the key; asking again at {_notBefore.Value.LocalDateTime:HH:mm}. "
                    + "The last reading stands.");

                _logger.LogWarning(
                    "The Škoda feed for {Vehicle}: the car declined the request; waiting until {NotBefore:u}.",
                    VehicleId, _notBefore);
                return null;

            default:
                _consecutiveFailures++;

                if (response.RetryAfter is { } retryAfter)
                {
                    _notBefore = now + retryAfter;
                }

                _health = VehicleSourceHealth.Degraded(
                    $"Škoda did not answer ({response.Detail}); the last reading stands.");

                _logger.LogWarning(
                    "Asking Škoda for {Vehicle} failed ({Detail}); next attempt in {Delay}.",
                    VehicleId, response.Detail, NextDelay);
                return null;
        }
    }

    private VehicleState? Read(SkodaApiResponse response, DateTimeOffset now)
    {
        if (response.KeyExpiresAt is { } expires)
        {
            store.Refresh(expires);
        }

        var state = SkodaVehicleResponse.Parse(response.Body, options.SourceId, out var error);

        if (state is null)
        {
            // Not a fault the owner can fix and not one worth hammering: the next capture may well be
            // whole. It counts towards the backoff so a car that has stopped reporting is asked less.
            _consecutiveFailures++;
            _health = VehicleSourceHealth.Degraded($"Škoda answered, but {error}. The last reading stands.");
            _logger.LogWarning("Škoda answered for {Vehicle} with something unusable: {Reason}", VehicleId, error);
            return null;
        }

        _consecutiveFailures = 0;
        _lastReading = state;
        _health = VehicleSourceHealth.Ok($"Answering; the car reported at {state.CapturedAt.LocalDateTime:HH:mm}.");

        if (store.Current is { } key
            && ExpiringSoon(key) is { } warning
            && (_lastExpiryWarningAt is not { } warned || now - warned >= TimeSpan.FromDays(1)))
        {
            _lastExpiryWarningAt = now;
            _logger.LogWarning("{Warning}", warning);
        }

        return state;
    }

    private string? ExpiringSoon(SkodaApiKey key) =>
        key.ExpiresAt is { } expires && expires - _time.GetUtcNow() <= ExpiryWarning
            ? $"The Škoda API key expires on {expires.LocalDateTime:yyyy-MM-dd} — create a new one in the "
              + "MySkoda app and paste it on the Vehicle portal page."
            : null;

    /// <summary>Doubling from the idle interval, capped. Deterministic, so a test can state it.</summary>
    private TimeSpan Backoff(int failures)
    {
        var delay = options.IdlePollInterval * Math.Pow(2, Math.Min(failures, 8) - 1);
        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    private static TimeSpan Longest(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
