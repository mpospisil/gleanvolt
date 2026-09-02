using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>
/// Phase 1's portal client behind <see cref="IVehicleUpdateService"/>: one car, one session, its own
/// clock (issue #140).
///
/// <para><b>It holds the session, and that is the change.</b> The on-demand reader
/// (<see cref="VwGroupPortalReader"/>) builds a cookie jar and signs in afresh on every press, which
/// is right for a button and wrong for a feed — replaying a password at a real identity provider on a
/// schedule is how accounts get locked. This keeps one <see cref="HttpClient"/> and one cookie jar for
/// the life of the process and lets the client sign in again only when the portal actually bounces
/// it.</para>
///
/// <para><b>It is also the first thing that can measure a session's life.</b> Nobody knows how long
/// one lasts, because until now nothing ever kept one (#138 could not answer it for exactly that
/// reason). Every <see cref="VwGroupPortalClient.SignedIn"/> is timed against the previous one and
/// logged, so the answer arrives from the reference install rather than from a guess. The figure is a
/// lower bound: we learn a session died only when we next use it.</para>
///
/// <para><b>Fifteen minutes, and the number lives here.</b> The portal is a batch delivery whose own
/// continuous data request runs at a fifteen-minute frequency, so asking faster achieves precisely
/// nothing — and the next service along, for a car that must be woken to answer, will want something
/// else entirely. That is why the cadence is the service's and not the host's: changing it is a change
/// to this file alone.</para>
///
/// <para><b>Blocked is a full stop, not a slower loop.</b> A refused password, a consent screen, an
/// OTP, or a portal with no data request at all cannot be fixed by asking again — so
/// <see cref="NextDelay"/> becomes <see cref="Timeout.InfiniteTimeSpan"/>, the worker stops, and the
/// dashboard says <i>sign-in required</i>. The /vehicle-portal button is what re-tests it once the
/// owner has done their part, and a restart is what puts the feed back on its clock.</para>
/// </summary>
public sealed class VwGroupUpdateService : IVehicleUpdateService, IDisposable
{
    /// <summary>The name this feed answers to. Display and diagnostics; nothing dispatches on it.</summary>
    public const string ManufacturerName = "vw-group";

    /// <summary>
    /// The portal's own delivery cadence, which is the fastest that can ever be useful. See the class
    /// remarks: this is the service's decision, not a setting, because no host could pick a figure
    /// that suited both this portal and a car that has to be woken up to answer.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>How far a repeated failure may push the interval out. Four missed deliveries.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly VwGroupPortalOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly HttpMessageHandler? _transport;

    private HttpMessageHandler? _handler;
    private HttpClient? _http;
    private VwGroupPortalClient? _client;

    // When the session in the cookie jar was established, so the next sign-in can say how long the
    // last one lasted. Null until the first one happens.
    private DateTimeOffset? _sessionStartedAt;
    private int _sessionCount;

    private int _consecutiveFailures;

    // Written by the fetch loop, read by a Blazor render and by the Home Assistant publish tick, so
    // the reference is published rather than left to a cache. Same reasoning as VehicleStateHolder's.
    private volatile VehicleSourceHealth _health;

    /// <param name="vehicleId">The <c>Ev:Vehicles[]</c> entry this serves, for the log to name.</param>
    /// <param name="options">Credentials, brand and portal addresses, resolved by the host.</param>
    /// <param name="time">The clock the session lifetimes are measured against.</param>
    /// <param name="logger">Where the measurement and every failure go.</param>
    /// <param name="transport">
    /// The HTTP transport, so a test can drive the whole service — session held across fetches, the
    /// re-sign-in, the backoff — against a stubbed portal and no network. Null builds the real one,
    /// with the cookie jar that <b>is</b> the session; a supplied one is the caller's to dispose.
    /// </param>
    public VwGroupUpdateService(
        string vehicleId,
        VwGroupPortalOptions options,
        TimeProvider? time = null,
        ILogger<VwGroupUpdateService>? logger = null,
        HttpMessageHandler? transport = null)
    {
        VehicleId = string.IsNullOrWhiteSpace(vehicleId) ? "the car" : vehicleId.Trim();
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _transport = transport;

        _health = _options.IsConfigured
            ? VehicleSourceHealth.Starting
            : VehicleSourceHealth.NeedsOwner(
                $"The VW portal feed is switched on but needs {_options.DescribeWhatIsMissing()}.");

        NextDelay = Health.IsBlocked ? Timeout.InfiniteTimeSpan : Interval;
    }

    /// <inheritdoc />
    public string VehicleId { get; }

    /// <inheritdoc />
    public string Manufacturer => ManufacturerName;

    /// <inheritdoc />
    public VehicleSourceHealth Health
    {
        get => _health;
        private set => _health = value;
    }

    /// <inheritdoc />
    public TimeSpan NextDelay { get; private set; }

    /// <summary>
    /// How long the current portal session has been alive, or null when there is none. The measurement
    /// #138 could not make; read by nothing but a log line and a diagnostic.
    /// </summary>
    public TimeSpan? SessionAge =>
        _sessionStartedAt is { } started ? _time.GetUtcNow() - started : null;

    /// <inheritdoc />
    public async Task<VehicleState?> FetchAsync(CancellationToken cancellationToken)
    {
        if (Health.IsBlocked)
        {
            // Defensive: the worker already stops on a blocked service. Asking anyway would be the
            // one thing this design exists to avoid -- a password replayed at an identity provider on
            // a clock, or a consent screen polled forever.
            return null;
        }

        try
        {
            var result = await Client().GetVehicleStateAsync(cancellationToken).ConfigureAwait(false);
            var state = result.State!;

            _consecutiveFailures = 0;
            NextDelay = Interval;
            Health = VehicleSourceHealth.Ok(
                $"The portal answered; the car reported at {state.CapturedAt.LocalDateTime:HH:mm}.");

            if (result.UnmappedFields.Count > 0)
            {
                _logger.LogDebug(
                    "The VW portal sent {Count} field(s) nothing here reads: {Fields}.",
                    result.UnmappedFields.Count, string.Join(", ", result.UnmappedFields));
            }

            // Labelled with the manufacturer as well as the car, because the dashboard shows this
            // beside the age and "via ...1234" alone says nothing about where it came from.
            return state with
            {
                SourceId = string.IsNullOrWhiteSpace(state.SourceId)
                    ? ManufacturerName
                    : $"{ManufacturerName} {state.SourceId}",
            };
        }
        catch (VwGroupPortalException failure)
        {
            Record(failure);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <summary>
    /// What a failure does to the two things this service publishes: whether the owner has to act, and
    /// when to ask again.
    ///
    /// <para>The split is <see cref="VwGroupPortalException.IsWorthRetrying"/> plus one: a portal with
    /// <b>no data request at all</b> is retryable in the sense that nothing is broken, and is not
    /// retryable in the sense that only the owner can create one — so it stops here rather than asking
    /// every quarter of an hour for a delivery that will never come.</para>
    /// </summary>
    private void Record(VwGroupPortalException failure)
    {
        // Named rather than derived from IsWorthRetrying, which is a different question. UnusableData
        // is not worth retrying *this instant* and is still not the owner's to fix: a bundle whose
        // fields nothing here reads wants a code change, and the next delivery may well carry them --
        // so it degrades and keeps asking rather than telling somebody to go and sign in.
        var needsTheOwner = failure.Failure
            is VwGroupFailure.SignInRejected
            or VwGroupFailure.OwnerActionRequired
            or VwGroupFailure.NoDataRequest
            or VwGroupFailure.NotConfigured;

        if (needsTheOwner)
        {
            Health = VehicleSourceHealth.NeedsOwner(Sentence(failure));
            NextDelay = Timeout.InfiniteTimeSpan;

            _logger.LogWarning(
                "The VW portal feed for {Vehicle} needs you ({Failure}): {Reason} It will not ask "
                + "again until the controller is restarted.",
                VehicleId, failure.Failure, failure.Message);

            return;
        }

        _consecutiveFailures++;

        // "Nothing to download yet" is not a fault and must not slow the feed down: a newly created
        // data request takes hours to fill, and backing off would then take hours more to notice that
        // it had. The faults that are worth waiting out are the ones where asking again immediately
        // is what makes them worse.
        NextDelay = failure.Failure == VwGroupFailure.NoDataAvailable
            ? Interval
            : Backoff(_consecutiveFailures);

        Health = VehicleSourceHealth.Degraded(Sentence(failure));

        _logger.LogWarning(
            "The VW portal feed for {Vehicle} produced no reading ({Failure}): {Reason} Next attempt "
            + "in {Delay}.",
            VehicleId, failure.Failure, failure.Message, NextDelay);
    }

    /// <summary>Doubling from the natural interval, capped. Deterministic, so a test can state it.</summary>
    private static TimeSpan Backoff(int consecutiveFailures)
    {
        var factor = Math.Min(consecutiveFailures, 8);
        var delay = Interval * Math.Pow(2, factor - 1);
        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    /// <summary>
    /// The failure in a sentence for the dashboard and the Home Assistant attribute. Says what the
    /// owner has to do where the answer is not obvious, and never carries a credential — the client's
    /// own messages are written to that rule and are quoted rather than rephrased.
    /// </summary>
    private static string Sentence(VwGroupPortalException failure) => failure.Failure switch
    {
        VwGroupFailure.SignInRejected =>
            $"The portal refused the sign-in: {failure.Message}. Check the password in .env — this "
            + "one is not retried, because repeated failures put the account at risk.",
        VwGroupFailure.OwnerActionRequired =>
            $"The portal is showing something only you can answer ({failure.Message}) — consent, "
            + "terms, an OTP or a CAPTCHA. Open it in a browser and clear it.",
        VwGroupFailure.NoDataRequest => $"{failure.Message}",
        VwGroupFailure.NotConfigured => failure.Message,
        VwGroupFailure.NoDataAvailable => $"Signed in, but {failure.Message}.",
        VwGroupFailure.UnusableData =>
            $"A delivery arrived and could not be read: {failure.Message}. The last good reading "
            + "stands and is ageing.",
        _ => $"The last read did not produce a reading: {failure.Message}.",
    };

    /// <summary>
    /// The one client, built on first use and kept. The cookie jar <b>is</b> the session, so it is
    /// created exactly once: a new handler per fetch would be the reader's behaviour, which is the
    /// thing this class exists not to do.
    /// </summary>
    private VwGroupPortalClient Client()
    {
        if (_client is not null)
        {
            return _client;
        }

        _handler = _transport ?? VwGroupSignIn.CreateHandler(new CookieContainer());

        // disposeHandler false when it was handed in: a transport this class did not create is not
        // this class's to close.
        _http = new HttpClient(_handler, disposeHandler: false);
        _client = new VwGroupPortalClient(_http, _options, logger: _logger);
        _client.SignedIn += OnSignedIn;

        return _client;
    }

    private void OnSignedIn()
    {
        var now = _time.GetUtcNow();
        _sessionCount++;

        if (_sessionStartedAt is { } started)
        {
            // The measurement, and the only place it is ever made. A lower bound: the session was
            // alive at the previous fetch and gone at this one, so it lasted at least this long.
            _logger.LogInformation(
                "The VW portal session for {Vehicle} lasted at least {Lifetime} before it was bounced "
                + "(session {Count}).",
                VehicleId, now - started, _sessionCount);
        }
        else
        {
            _logger.LogInformation(
                "Signed in to the VW portal for {Vehicle}; timing how long the session lasts.",
                VehicleId);
        }

        _sessionStartedAt = now;
    }

    public void Dispose()
    {
        if (_client is not null)
        {
            _client.SignedIn -= OnSignedIn;
            _client = null;
        }

        _http?.Dispose();

        if (!ReferenceEquals(_handler, _transport))
        {
            _handler?.Dispose();
        }

        _http = null;
        _handler = null;
    }
}
