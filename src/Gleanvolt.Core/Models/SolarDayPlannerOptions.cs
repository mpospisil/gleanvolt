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
/// <param name="MinBridgeSurplusWatts">
/// The live surplus the controller refuses to lend below on a day the pack can still absorb its own
/// surplus. Planned here for one reason: a window drawn from a bridge the controller would not grant
/// is a window nothing can enter — see <see cref="Strategies.BatteryLoanRules"/>.
/// </param>
/// <param name="SpillBridgeSurplusWatts">
/// The same floor on a day whose surplus the pack has no room for, where the loan costs the house
/// nothing. Much lower, and the reason a full pack on a thin afternoon has a window at all.
/// </param>
/// <param name="MinViableWindow">The shortest stretch of chargeable weather worth starting a session for.</param>
/// <param name="MinBatterySocFloorPercent">The hard floor the computed SOC trajectory is clamped to.</param>
/// <param name="Confidence">Which forecast band to plan on. P10 by default — see <see cref="ForecastConfidence"/>.</param>
public sealed record SolarDayPlannerOptions(
    double BatteryCapacityWh,
    double ChargeEfficiency,
    double MinChargePowerWatts,
    double MaxLoanPowerWatts,
    bool EnableBatteryLoan,
    double MinBridgeSurplusWatts,
    double SpillBridgeSurplusWatts,
    TimeSpan MinViableWindow,
    double MinBatterySocFloorPercent,
    ForecastConfidence Confidence = ForecastConfidence.P10);
