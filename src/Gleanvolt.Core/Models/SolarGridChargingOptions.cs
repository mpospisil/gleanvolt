namespace Gleanvolt.Core.Models;

/// <summary>
/// The decision parameters <see cref="Strategies.SolarGridChargingController"/> works to. A plain Core
/// record for the same reason <see cref="TargetedChargingOptions"/> is one.
/// </summary>
/// <param name="MinChargingCurrentAmps">The charger's minimum current: what a grid bridge tops the surplus up to.</param>
/// <param name="MaxChargingCurrentAmps">The ceiling a large surplus is clamped to.</param>
/// <param name="CurrentStepAmps">The granularity of the setpoint the charger accepts.</param>
/// <param name="ResumeHysteresisWatts">
/// How far above the minimum surplus a paused session must climb before it restarts; a running one
/// continues down to the minimum itself.
/// </param>
/// <param name="MinRunTime">
/// The shortest a charge is held, at the charger's floor, before a dip below the minimum may stop it —
/// a few minutes at 6 A is cheaper than a contactor cycle and a vehicle wake.
/// </param>
/// <param name="MinPauseTime">The shortest a pause lasts, once the car has charged, before charging may resume.</param>
/// <param name="CompletionDwell">
/// How long the car must draw nothing, <em>while we are asking it to charge</em>, before it counts as
/// having finished on its own limit.
/// </param>
/// <param name="MinSurplusWatts">
/// The minimum used when a cycle carries no <see cref="SolarGridOutlook"/>. In the host the outlook
/// always carries the runtime value; this is what unit tests and a missing outlook fall back to.
/// </param>
public sealed record SolarGridChargingOptions(
    int MinChargingCurrentAmps,
    int MaxChargingCurrentAmps,
    int CurrentStepAmps,
    double ResumeHysteresisWatts,
    TimeSpan MinRunTime,
    TimeSpan MinPauseTime,
    TimeSpan CompletionDwell,
    double MinSurplusWatts = 2000);
