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
    /// The feed in the owner's words — <i>volkswagen.de</i>, <i>MyŠkoda</i>, <i>Data Act portal</i> —
    /// for the dashboard's "via …" (issue #212). The raw <see cref="Manufacturer"/> is a log key, not
    /// something an owner recognises. Defaulted to it for a feed that has no better name.
    /// </summary>
    string DisplayName => Manufacturer;

    /// <summary>
    /// What fixes this feed when it is <see cref="Enums.VehicleSourceState.NeedsOwner"/>, as one sentence
    /// for the dashboard's card (issue #212); null to show <see cref="Health"/>'s own sentence. A
    /// fixed sentence, because the card is where an owner decides <i>what to do</i>, and the health
    /// message is written for the log, where it says <i>what happened</i>.
    /// </summary>
    string? OwnerAction => null;

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
    /// Whether this feed produces a reading <b>only while a charge is running</b> — so that hours of
    /// silence are the car not charging rather than the feed failing (issue #180).
    ///
    /// <para>Declared by the service because only the service knows: volkswagen.de is asked while a
    /// session is live and not at all between sessions (#170), and that is its design rather than a
    /// limitation. Without this, a parked car's days-old reading from its last charge would look like
    /// a feed that has died; the dashboard says <i>from the last charge</i> instead (#212).</para>
    ///
    /// <para><b>Reporting only.</b> Nothing dispatches on it and no charging decision reads it; it
    /// exists so a page can tell a designed silence from a dropout. Defaulted to <c>false</c>, which is the ordinary case: a feed on its own clock is
    /// expected to keep it whatever the car is doing.</para>
    /// </summary>
    bool DeliversOnlyWhileCharging => false;

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

    /// <summary>
    /// Ask the manufacturer because somebody wants to know now — the dashboard's <i>Ask the car</i>
    /// (issue #168) — rather than because <see cref="NextDelay"/> came round. The same answer as
    /// <see cref="FetchAsync"/> for a feed on its own clock, which is why it defaults to it.
    ///
    /// <para>A feed that is gated to a charge overrides it (issue #212): with one feed per car, a
    /// parked ID.4 has only volkswagen.de, and a feed that refused an owner's explicit ask would
    /// leave the car's last-charge reading as the only thing a plan could start from. One request per
    /// owner action; the polling gate is unchanged.</para>
    /// </summary>
    Task<VehicleState?> AskAsync(CancellationToken cancellationToken) => FetchAsync(cancellationToken);
}
