namespace Solax.Core.Models;

/// <summary>
/// The knobs <see cref="Strategies.ForecastedChargingController"/> decides with. A Core record rather
/// than the worker's bound options class, for the same reason as
/// <see cref="SolarDayPlannerOptions"/>: the strategy stays free of configuration and hosting types.
/// </summary>
/// <param name="MinChargingCurrentAmps">Minimum viable charging current; below it the charger is paused.</param>
/// <param name="MaxChargingCurrentAmps">Maximum current to command (usually the car's limit, not the charger's).</param>
/// <param name="CurrentStepAmps">Granularity of the setpoint the hardware accepts.</param>
/// <param name="ResumeHysteresisWatts">Extra surplus required to (re)start, so a marginal surplus can't flap the charger.</param>
/// <param name="EnableBatteryLoan">Whether the home battery may bridge a sub-minimum surplus up to the charger's floor.</param>
/// <param name="MaxLoanPowerWatts">Ceiling on that bridge, which also caps the battery's discharge rate.</param>
/// <param name="MinBridgeSurplusWatts">
/// The loan tops up a genuine surplus; it never funds a session on its own. Below this much live
/// surplus, no loan is granted — lending the full minimum into no sun at all would just be a
/// battery-to-car transfer, paying a round trip and a cycle on both packs for nothing.
/// </param>
/// <param name="MaxDailyLoanWh">Total energy the battery may lend in a day, reset at local midnight.</param>
/// <param name="LoanSocMarginPercent">How far above the trajectory floor the SOC must sit before lending.</param>
/// <param name="MinRunTime">Once charging, the shortest time to keep going before a soft reason may stop it.</param>
/// <param name="MinPauseTime">Once paused, the shortest time before charging may restart.</param>
/// <param name="FinalGuardBefore">
/// How long before the deadline the car is paused outright while the battery is under 100% — the
/// belt-and-braces guard for an afternoon the forecast got wrong.
/// </param>
/// <param name="SessionEnergyTargetWh">
/// Ceiling on energy delivered per session, 0 for unlimited. Stands in for "charge to 80%": the
/// charger exposes no vehicle SOC, so the only lever on the car's pack is how much is put into it.
/// </param>
public sealed record ForecastedChargingOptions(
    int MinChargingCurrentAmps,
    int MaxChargingCurrentAmps,
    int CurrentStepAmps,
    double ResumeHysteresisWatts,
    bool EnableBatteryLoan,
    double MaxLoanPowerWatts,
    double MinBridgeSurplusWatts,
    double MaxDailyLoanWh,
    double LoanSocMarginPercent,
    TimeSpan MinRunTime,
    TimeSpan MinPauseTime,
    TimeSpan FinalGuardBefore,
    double SessionEnergyTargetWh);
