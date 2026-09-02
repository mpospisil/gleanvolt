namespace Gleanvolt.Core.Enums;

/// <summary>
/// Whether a manufacturer's feed can currently produce a reading — three states, because those are
/// the three different things an owner can do about it (issue #140).
///
/// <para><b>The distinction that matters is <see cref="Degraded"/> against
/// <see cref="NeedsOwner"/>.</b> Both leave the reading ageing on the dashboard, and they call for
/// opposite responses: one clears itself and wants nothing, the other will never clear until somebody
/// opens a browser. A card that showed them alike would waste the only information the owner can act
/// on — and a charger sitting idle is already the most convincing impersonation of a fault this
/// controller can produce.</para>
///
/// <para>Deliberately not a taxonomy of failures. The service knows a dozen ways an exchange can go
/// wrong (<c>VwGroupFailure</c> alone has nine members); what leaves the service is which of these
/// three the owner is in, plus a sentence. Anything finer is a second service's argument to make.</para>
/// </summary>
public enum VehicleSourceState
{
    /// <summary>The last fetch produced a reading. Nothing to say and nothing to do.</summary>
    Ok = 0,

    /// <summary>
    /// The feed is trying and not currently succeeding: a 5xx, a timeout, a session that expired, a
    /// data request that has not been filled yet — or simply nothing fetched so far. Self-clearing,
    /// so it is reported without alarm and the reading is left to age.
    /// </summary>
    Degraded,

    /// <summary>
    /// Nothing this process can do will fix it: a refused password, a consent screen, an OTP, a
    /// CAPTCHA, or a portal setting only the owner can make. The service <b>stops asking</b> here
    /// rather than replaying a password at a real identity provider on a clock, which is how accounts
    /// get locked.
    /// </summary>
    NeedsOwner,
}
