namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>
/// Why a portal exchange did not produce a reading. The <b>kind</b> is the point: issue #139 asks for
/// the consent screen and an expired session to fail distinguishably, because they need opposite
/// responses — one wants a human at a browser and must never be retried, the other is ordinary and is
/// fixed by signing in again.
/// </summary>
public enum VwGroupFailure
{
    /// <summary>Nothing is configured, so nothing was attempted.</summary>
    NotConfigured,

    /// <summary>
    /// The session is gone: a 401, a bounce to <c>/login</c>, or HTML where JSON was expected. Expected,
    /// recoverable, and the reason the client can sign itself back in.
    /// </summary>
    SessionExpired,

    /// <summary>
    /// Credentials were replayed and refused. Retrying with the same password achieves nothing, and
    /// hammering it risks the account.
    /// </summary>
    SignInRejected,

    /// <summary>
    /// A screen appeared that a program cannot answer — consent, terms, an email OTP, a CAPTCHA. The
    /// owner must open a browser. <b>Never retried</b>: #137's unattended design degrades here rather
    /// than looping, and this is what the dashboard's "sign-in required" is built on.
    /// </summary>
    OwnerActionRequired,

    /// <summary>The account has no vehicle, or not the one that was asked for.</summary>
    VehicleNotFound,

    /// <summary>
    /// Signed in, but the portal has no dataset to give. Usually means nobody has enabled a
    /// <i>continuous data request</i> in the portal by hand, which is a browser step no client can do.
    /// </summary>
    NoDataAvailable,

    /// <summary>
    /// The bundle arrived and could not be believed: not a ZIP, no readings, or values that are
    /// present-but-unusable. #73's rule — the last good reading stays, visibly ageing.
    /// </summary>
    UnusableData,

    /// <summary>
    /// A 5xx, a timeout, a dropped connection. Try again later; how much later is Phase 2's business.
    /// </summary>
    Transient,
}

/// <summary>
/// A portal exchange that failed, carrying which kind of failure it was so a caller can respond
/// without parsing a message.
/// </summary>
public sealed class VwGroupPortalException : Exception
{
    public VwGroupPortalException(VwGroupFailure failure, string message, Exception? inner = null)
        : base(message, inner) =>
        Failure = failure;

    public VwGroupFailure Failure { get; }

    /// <summary>
    /// Whether trying the same thing again could plausibly work. False for the two that need a human:
    /// a refused password and a screen only a browser can answer.
    /// </summary>
    public bool IsWorthRetrying =>
        Failure is VwGroupFailure.SessionExpired or VwGroupFailure.Transient or VwGroupFailure.NoDataAvailable;
}
