using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.VwWebsite;

/// <summary>
/// volkswagen.de behind the vehicle-feed contract (issue #170): the live source for a Volkswagen, and
/// since #212 its only one.
///
/// <para><b>Read continuously.</b> Every <see cref="VwWebsiteOptions.PollInterval"/> while a charge is
/// running and every <see cref="VwWebsiteOptions.IdlePollInterval"/> otherwise. With the Data Act
/// portal gone, this is where the state of charge a plan starts from comes from, and a plan is made
/// before the charge, after a drive.</para>
///
/// <para><b>Any failure stops it and asks the owner.</b> A one-time code wanted, no usable answer, or
/// no answer at all: the feed reports <see cref="VehicleSourceState.NeedsOwner"/>, the worker stops
/// asking, and nothing is replayed at VW's identity provider on a clock — that is how accounts get
/// locked. Signing in again on the vehicle page moves <see cref="VwWebsiteClient.SignIns"/>, which is
/// what puts the feed back on its clock without a restart.</para>
///
/// <para>The charge never depends on this: a recording is not a control input.</para>
/// </summary>
public sealed class VwWebsiteUpdateService(
    VwWebsiteOptions options,
    VwWebsiteClient client,
    ChargeControlStatusHolder status,
    string vehicleId,
    ILogger<VwWebsiteUpdateService>? logger = null) : IVehicleUpdateService
{
    /// <summary>What the dashboard's card says when the feed has stopped for the owner (#212).</summary>
    public const string OwnerActionSentence =
        "volkswagen.de wants a one-time code — sign in again on the vehicle page.";

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    private volatile VehicleSourceHealth _health = VehicleSourceHealth.Starting;
    private volatile VehicleSourceHealth? _blocked;
    private int _blockedAtSignIns;

    public string VehicleId => vehicleId;

    public string Manufacturer => "vw-website";

    public string DisplayName => "volkswagen.de";

    public string? OwnerAction => Health.IsBlocked ? OwnerActionSentence : null;

    /// <summary>
    /// Blocked until the owner has signed in again since it stopped. Read by the worker every minute
    /// while it waits, so a sign-in on the vehicle page resumes the feed within one.
    /// </summary>
    public VehicleSourceHealth Health =>
        _blocked is { } blocked && client.SignIns == Volatile.Read(ref _blockedAtSignIns) ? blocked : _health;

    public TimeSpan NextDelay =>
        Health.IsBlocked
            ? Timeout.InfiniteTimeSpan
            : IsCharging ? options.PollInterval : options.IdlePollInterval;

    private bool IsCharging =>
        status.Current is { } current
        && current.Mode != ChargeControlMode.Off
        && current.CarConnected
        && !current.SessionCompleted;

    public async Task<VehicleState?> FetchAsync(CancellationToken cancellationToken)
    {
        if (Health.IsBlocked)
        {
            // Stopped for the owner: nothing is sent until they have signed in again.
            return null;
        }

        try
        {
            var state = await client.GetVehicleStateAsync(cancellationToken).ConfigureAwait(false);

            if (state is null)
            {
                return Stop(client.AwaitingCode
                    ? "volkswagen.de wants a one-time code."
                    : "volkswagen.de did not answer with a reading.");
            }

            _blocked = null;
            _health = VehicleSourceHealth.Ok($"Answering; last reading captured {state.CapturedAt:u}.");
            return state;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Asking volkswagen.de for the car failed.");
            return Stop($"volkswagen.de was unreachable ({ex.Message}).");
        }
    }

    private VehicleState? Stop(string what)
    {
        Volatile.Write(ref _blockedAtSignIns, client.SignIns);
        _blocked = VehicleSourceHealth.NeedsOwner(
            $"{what} Sign in again on the Vehicle portal page; the feed resumes once you have. "
            + "The charge is unaffected.");

        return null;
    }
}
