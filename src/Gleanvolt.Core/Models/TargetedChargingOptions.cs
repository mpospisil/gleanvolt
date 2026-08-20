namespace Gleanvolt.Core.Models;

/// <summary>
/// The decision parameters <see cref="Strategies.TargetedChargingController"/> works to. A plain Core
/// record for the same reason <see cref="ForecastedChargingOptions"/> is one.
/// </summary>
/// <param name="MinChargingCurrentAmps">The charger's minimum current; below it the car cannot charge at all.</param>
/// <param name="MaxChargingCurrentAmps">The current the grid block and the "not enough time" case run at.</param>
/// <param name="CurrentStepAmps">The granularity of the setpoint the charger accepts.</param>
/// <param name="ResumeHysteresisWatts">
/// How far above the minimum the surplus must climb before a paused <em>solar</em> block may restart.
/// The grid block never waits on it: it is working to a deadline.
/// </param>
/// <param name="MinRunTime">
/// The shortest a solar charge is held before a dip may stop it — a few minutes at 6 A is cheaper than
/// a contactor cycle and a vehicle wake.
/// </param>
/// <param name="MinPauseTime">The shortest a solar pause lasts before charging may resume.</param>
/// <param name="CompletionDwell">
/// How long the car must draw nothing, <em>while we are asking it to charge</em>, before it counts as
/// having stopped on its own limit.
/// </param>
public sealed record TargetedChargingOptions(
    int MinChargingCurrentAmps,
    int MaxChargingCurrentAmps,
    int CurrentStepAmps,
    double ResumeHysteresisWatts,
    TimeSpan MinRunTime,
    TimeSpan MinPauseTime,
    TimeSpan CompletionDwell);
