using Gleanvolt.Core.Enums;

namespace Gleanvolt.Hosting;

/// <summary>The outcome of one charge-control cycle, used to assemble a <see cref="Core.Models.ChargeControlStatus"/>.</summary>
/// <param name="LoanPowerWatts">
/// How much of the commanded charge the home battery is currently lending, bridging a sub-minimum
/// surplus up to the charger's floor. Zero outside the forecast-driven mode.
/// </param>
/// <param name="EndsSession">
/// The controller ended the mode itself this cycle, and why (#198): the decision's own reason, carried to
/// the poll loop, which answers by returning the mode to Off and hands the reason on to the session.
/// </param>
/// <param name="ChargerStoodDown">
/// Whether the charger is sitting in Stop because a deferred charge put it there for its wait. A
/// stood-down charger reports <see cref="EvChargerStatus.Available"/> or
/// <see cref="EvChargerStatus.Finishing"/> whether or not a car is plugged into it, so while this is
/// true its status says nothing about the plug.
/// </param>
/// <param name="GridBridgeWatts">
/// How much of the commanded charge the <em>grid</em> is currently bridging up to the charger's floor.
/// Non-zero only in the targeted mode, and the poll loop arms the battery discharge hold on it: the
/// bridge is only honest while the pack is kept out of it.
/// </param>
public readonly record struct ChargeControlCycleResult(
    ChargeControlState State,
    double? SurplusWatts,
    int? TargetCurrentAmps,
    bool HoldingControl,
    double LoanPowerWatts = 0,
    ChargingSessionEndReason? EndsSession = null,
    double GridBridgeWatts = 0,
    bool ChargerStoodDown = false)
{
    /// <summary>Whether the mode ends itself this cycle; <see cref="EndsSession"/> says why.</summary>
    public bool SessionComplete => EndsSession is not null;
}
