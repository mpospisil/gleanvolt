using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// One manufacturer's feed for one configured car: produce a <see cref="VehicleState"/> on whatever
/// schedule that manufacturer's API justifies, and say whether it currently can (issue #140).
///
/// <para><b>The interval belongs to the service.</b> VW's portal is a fifteen-minute batch and asking
/// faster achieves nothing; polling a sleeping Tesla wakes it and costs the owner range. No interval a
/// host could pick is right for both, so <see cref="NextDelay"/> is read after every fetch and the
/// service is free to move it — a backoff, a longer wait while it is blocked, or a keepalive for a
/// service whose API pushes and which writes to the holder itself.</para>
///
/// <para><b>Authentication is private to the service and deliberately not unified.</b> What this
/// contract exposes is not <i>how</i> a service authenticates but <b>whether it currently can</b>,
/// which is <see cref="Health"/>. There is no credential abstraction, no capability taxonomy and no
/// auth-model enum here: one implementation exists, and the shape a second one wants is not knowable
/// from the first. This stays small and unfrozen until there is a second service to argue with it.</para>
///
/// <para><b>The feed stays advisory.</b> Nothing that writes to hardware may read what this produces:
/// it reaches <see cref="VehicleStateHolder"/>, the dashboard and what an owner <i>asks</i> for, never
/// how a charge is delivered. A car with no update service configured works exactly as it did before
/// this existed.</para>
/// </summary>
public interface IVehicleUpdateService
{
    /// <summary>The <c>Ev:Vehicles[]</c> entry this serves, so a log line names the car and not the API.</summary>
    string VehicleId { get; }

    /// <summary>
    /// Which manufacturer's feed this is (<c>"vw-group"</c>) — display and diagnostics only.
    /// <b>Never dispatch on it.</b> Choosing a service by string is the shape that grows a registry
    /// nobody asked for; the container already holds exactly the services that are configured.
    /// </summary>
    string Manufacturer { get; }

    /// <summary>
    /// Whether this feed can currently produce a reading, with a sentence for the UI. Cheap and
    /// synchronous: it is read on a Blazor render and on the Home Assistant publish tick, so it
    /// reports what the last fetch found rather than going and finding out.
    /// </summary>
    VehicleSourceHealth Health { get; }

    /// <summary>
    /// How long to wait before the next <see cref="FetchAsync"/>. Re-read after every fetch, so a
    /// service can back off after a failure and return to its natural cadence after a success.
    /// </summary>
    TimeSpan NextDelay { get; }

    /// <summary>
    /// Ask the manufacturer for the car, once. Returns null when this attempt produced nothing —
    /// which is ordinary, not exceptional: no dataset yet, a session that expired, an owner who has
    /// not accepted a consent screen. The reason lands in <see cref="Health"/>, and the holder keeps
    /// its last good reading with its age visibly growing rather than being blanked.
    ///
    /// <para>Never throws for an expected failure. Only a genuine bug escapes, and the host logs that
    /// rather than letting it stop the process — the car is advisory data and the controller must go
    /// on charging whether or not the manufacturer's cloud is reachable.</para>
    /// </summary>
    Task<VehicleState?> FetchAsync(CancellationToken cancellationToken);
}
