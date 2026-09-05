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

    public VehicleSourceHealth Health => _health;

    /// <summary>
    /// The configured interval while charging, and a long idle beat otherwise.
    ///
    /// <para>Idle is not zero because the worker's loop is what notices a charge has started; it is
    /// long because noticing a minute late costs nothing and asking VW every minute for a car that is
    /// asleep costs a session.</para>
    /// </summary>
    public TimeSpan NextDelay => IsCharging ? options.PollInterval : TimeSpan.FromMinutes(1);

    private bool IsCharging =>
        status.Current is { } current
        && current.Mode != ChargeControlMode.Off
        && current.CarConnected
        && !current.SessionCompleted;

    public async Task<VehicleState?> FetchAsync(CancellationToken cancellationToken)
    {
        if (!IsCharging)
        {
            // No network call at all. "Nothing is fetched while idle" has to be true of the wire, not
            // just of the dashboard.
            return null;
        }

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
