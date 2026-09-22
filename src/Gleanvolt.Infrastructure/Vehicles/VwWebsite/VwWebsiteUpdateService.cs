using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.VwWebsite;

/// <summary>
/// volkswagen.de behind the vehicle-feed contract (issue #170) — the live source, asked
/// <b>only while a charge is running</b>.
///
/// <para>That restriction is the design rather than a limitation. The owner asked for precise
/// progress during a charge and nothing at all when idle, and the two halves reinforce each other: a
/// parked car's state of charge does not drift, so polling it earns nothing, while during a charge it
/// is the one number worth recording. Bounding the polling to the hours of a session also bounds the
/// exposure of a session that VW will eventually expire.</para>
///
/// <para><b>Authorisation happens elsewhere, on purpose.</b> A cold login always wants an email
/// one-time code — verified against the live account — so it is done from the web UI while a person is
/// there to read it. This service never prompts, never loops, and never blocks a charge: if the
/// session has lapsed it says so through <see cref="Health"/> and returns nothing, and the charge
/// carries on, because a recording is not a control input.</para>
/// </summary>
public sealed class VwWebsiteUpdateService(
    VwWebsiteOptions options,
    VwWebsiteClient client,
    ChargeControlStatusHolder status,
    string vehicleId,
    ILogger<VwWebsiteUpdateService>? logger = null) : IVehicleUpdateService
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    private VehicleSourceHealth _health =
        VehicleSourceHealth.Ok("Not asked yet; this source is used while a charge is running.");

    public string VehicleId => vehicleId;

    public string Manufacturer => "vw-website";

    public string DisplayName => "volkswagen.de";

    public string? OwnerAction => _health.IsBlocked ? OwnerActionSentence : null;

    /// <summary>What the dashboard's card says when the session wants a one-time code (#212).</summary>
    public const string OwnerActionSentence =
        "volkswagen.de wants a one-time code — sign in again on the vehicle page.";

    public VehicleSourceHealth Health => _health;

    /// <summary>
    /// The configured interval while charging, and a long idle beat otherwise.
    ///
    /// <para>Idle is not zero because the worker's loop is what notices a charge has started; it is
    /// long because noticing a minute late costs nothing and asking VW every minute for a car that is
    /// asleep costs a session.</para>
    /// </summary>
    public TimeSpan NextDelay => IsCharging ? options.PollInterval : TimeSpan.FromMinutes(1);

    /// <summary>
    /// True, and the whole point of this service (#170/#180). Between charges it fetches nothing at
    /// all, so its gaps measure how much the car was driven rather than how reliable the feed is.
    /// </summary>
    public bool DeliversOnlyWhileCharging => true;

    private bool IsCharging =>
        status.Current is { } current
        && current.Mode != ChargeControlMode.Off
        && current.CarConnected
        && !current.SessionCompleted;

    public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken)
    {
        // No network call at all while idle. "Nothing is polled while idle" has to be true of the
        // wire, not just of the dashboard.
        return IsCharging ? ReadAsync(cancellationToken) : Task.FromResult<VehicleState?>(null);
    }

    /// <summary>
    /// Read once whether or not a charge is running (#212). With one feed per car this is the only
    /// way a parked ID.4 gets a reading newer than its last charge, and it is one request per owner
    /// action — the clock above is still gated to a charge.
    /// </summary>
    public Task<VehicleState?> AskAsync(CancellationToken cancellationToken) => ReadAsync(cancellationToken);

    private async Task<VehicleState?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await client.GetVehicleStateAsync(cancellationToken).ConfigureAwait(false);

            if (state is null)
            {
                _health = client.AwaitingCode
                    ? VehicleSourceHealth.NeedsOwner(
                        "volkswagen.de wants a one-time code. Sign in again from the vehicle page; "
                        + "the charge is unaffected.")
                    : VehicleSourceHealth.Degraded("volkswagen.de did not answer with a usable reading.");

                return null;
            }

            _health = VehicleSourceHealth.Ok($"Answering; last reading captured {state.CapturedAt:u}.");
            return state;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Asking volkswagen.de for the car failed.");
            _health = VehicleSourceHealth.Degraded($"volkswagen.de was unreachable ({ex.Message}).");
            return null;
        }
    }
}
