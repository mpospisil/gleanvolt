using Solax.Core.Enums;

namespace Solax.Worker;

/// <summary>The outcome of one charge-control cycle, used to assemble a <see cref="Core.Models.ChargeControlStatus"/>.</summary>
/// <param name="LoanPowerWatts">
/// How much of the commanded charge the home battery is currently lending, bridging a sub-minimum
/// surplus up to the charger's floor. Zero outside the forecast-driven mode.
/// </param>
/// <param name="SessionComplete">
/// The controller reported that the car has finished (or gone away) and the mode should end itself.
/// Only the fast mode ever sets it; the poll loop answers by returning the mode to Off.
/// </param>
public readonly record struct ChargeControlCycleResult(
    ChargeControlState State,
    double? SurplusWatts,
    int? TargetCurrentAmps,
    bool HoldingControl,
    double LoanPowerWatts = 0,
    bool SessionComplete = false);
