using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Asks a manufacturer's portal for the car, once, when someone asks it to.
///
/// <para>The seam lives in Core because the web UI is what drives it and the web UI sees only Core —
/// the same arrangement <see cref="IChargeActions"/> has, and for the same reason. The implementation,
/// with the sign-in flow and the vendor's vocabulary in it, stays in Infrastructure where the rest of
/// the outside world lives.</para>
///
/// <para><b>Deliberately on demand, and deliberately not a feed.</b> Nothing polls this, nothing
/// schedules it, and a reading it returns is not written into
/// <see cref="IVehicleTelemetry"/>: a manual button proves the credentials and the mapping, which is
/// what is wanted before anything reasons about a car with data fetched behind the owner's back.</para>
///
/// <para>The feed with its own clock is <see cref="IVehicleUpdateService"/> (#140), and the two stay
/// separate on purpose. This one signs in afresh per press with its own session, so it is what an
/// owner uses to prove credentials before switching a feed on — and what they press again after
/// clearing a consent screen, since a blocked feed stops asking by design.</para>
/// </summary>
public interface IVehiclePortalReader
{
    /// <summary>Which portal this reads, for the page to name ("VW Group EU Data Act portal").</summary>
    string PortalName { get; }

    /// <summary>Whether enough is configured to attempt a sign-in at all.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// What is missing, in a sentence a page can show — empty when nothing is. Never names the
    /// password's value, only its absence.
    /// </summary>
    string DescribeWhatIsMissing();

    /// <summary>
    /// Signs in, downloads the newest dataset and maps it. Never throws for an expected failure — a
    /// refused password, a consent screen, a portal with nothing to give — those come back as an
    /// unsuccessful <see cref="VehiclePortalReading"/> carrying the kind and the sentence.
    /// </summary>
    Task<VehiclePortalReading> ReadAsync(CancellationToken cancellationToken = default);
}
