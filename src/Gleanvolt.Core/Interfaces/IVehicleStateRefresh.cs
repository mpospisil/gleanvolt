using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Asks the car for its state <b>now</b>, because a person or a plan wants to know (issue #168).
///
/// <para>The seam lives in Core because the web UI drives it and the web UI sees only Core — the
/// arrangement <see cref="IChargeActions"/> has, and for the same reason. The implementation knows
/// about the registered feeds; nothing above it does.</para>
///
/// <para><b>Distinct from <see cref="IVehicleTelemetry"/>, which is the read side.</b> That one
/// answers instantly from what is held and never does I/O. This one goes and asks, takes as long as
/// the network takes, and updates what the other serves. A caller on a hot path wants the first; a
/// caller who has just pressed a button wants this.</para>
/// </summary>
public interface IVehicleStateRefresh
{
    /// <summary>
    /// Whether there is any feed to ask. False on an installation with no vehicle source configured,
    /// which is a perfectly ordinary state — the car is described in <c>Ev</c> either way.
    /// </summary>
    bool CanRefresh { get; }

    /// <summary>
    /// Asks, and updates what <see cref="IVehicleTelemetry"/> serves if an answer comes back.
    ///
    /// <para>Never throws for an expected failure — an unreachable feed, a refused sign-in, a source
    /// with nothing to give — those come back as an unsuccessful result carrying the reason and,
    /// where one exists, the last known reading.</para>
    /// </summary>
    Task<VehicleRefreshResult> RefreshAsync(CancellationToken cancellationToken = default);
}
