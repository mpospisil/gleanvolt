using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// The knobs <see cref="Strategies.SolarDayPlanner"/> plans with. A plain Core record rather than the
/// worker's bound options class, so the planner stays free of configuration and hosting types (and so
/// tests can construct one in a line).
/// </summary>
/// <param name="BatteryCapacityWh">Usable capacity of the home battery. Everything SOC-related scales off this.</param>
/// <param name="ChargeEfficiency">
/// PV → battery efficiency (0..1]. Applied so "the battery needs 20% of 11.6 kWh" becomes the larger
/// figure the sun actually has to deliver.
/// </param>
/// <param name="MinChargePowerWatts">
/// The charger's minimum viable power — 6 A converted through the configured phase count. The line
/// between shoulder energy (battery only) and plateau energy (the car can charge).
/// </param>
/// <param name="MaxLoanPowerWatts">
/// How much the battery may lend to bridge a sub-minimum surplus up to the floor. Used here only to
/// widen the feasible window; the loan itself is granted by the controller.
/// </param>
/// <param name="EnableBatteryLoan">Whether loan-bridged periods count as feasible at all.</param>
/// <param name="MinViableWindow">The shortest stretch of chargeable weather worth starting a session for.</param>
/// <param name="MinBatterySocFloorPercent">The hard floor the computed SOC trajectory is clamped to.</param>
/// <param name="DailyEvTargetWh">What the owner would like the car to get today; the shortfall is measured against it.</param>
/// <param name="Confidence">Which forecast band to plan on. P10 by default — see <see cref="ForecastConfidence"/>.</param>
/// <param name="TightOutlookFraction">
/// The share of the EV target that separates a <see cref="DayOutlook.Tight"/> day from a
/// <see cref="DayOutlook.Shortfall"/> one.
/// </param>
/// <param name="OutlookHysteresisFraction">
/// How far past <paramref name="TightOutlookFraction"/> the estimate must move before the reported
/// outlook changes. Without it a day sitting exactly on the boundary — the common case, since the
/// boundary is half the target — flips back and forth on rounding alone.
/// </param>
public sealed record SolarDayPlannerOptions(
    double BatteryCapacityWh,
    double ChargeEfficiency,
    double MinChargePowerWatts,
    double MaxLoanPowerWatts,
    bool EnableBatteryLoan,
    TimeSpan MinViableWindow,
    double MinBatterySocFloorPercent,
    double DailyEvTargetWh,
    ForecastConfidence Confidence = ForecastConfidence.P10,
    double TightOutlookFraction = 0.5,
    double OutlookHysteresisFraction = 0.05);
