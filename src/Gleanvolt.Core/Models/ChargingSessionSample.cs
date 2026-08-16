using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// One point in a session's progress: what every meter read, what we had asked for, and what the
/// forecast expected at that moment.
///
/// <para>Samples are recorded at a fixed cadence (coarser than the poll interval) plus whenever
/// something changes, so a chart keeps every transition at full resolution without storing a row every
/// five seconds. The running totals are carried on each row so a session interrupted by a power cut is
/// still readable from its last sample alone.</para>
/// </summary>
/// <param name="EvChargerPowerWatts">
/// <b>Measured</b> power the charger is drawing. The ground truth, and the basis of every energy total
/// here — the amp figures are derived conveniences, never the basis of a sum.
/// </param>
/// <param name="EvChargingCurrentAmps">
/// The <b>actual</b> charging current, derived phase-aware from <paramref name="EvChargerPowerWatts"/>
/// and rounded to a whole amp for display. What the car is really pulling.
/// </param>
/// <param name="ActiveCurrentAmps">
/// The charger's own current setpoint, <b>read back from the device</b>. Null when the holding
/// register isn't readable on this charger/firmware.
/// </param>
/// <param name="TargetCurrentAmps">
/// What the controller <b>decided</b> to command this cycle. Null when paused or not controlling.
/// </param>
/// <remarks>
/// The three current figures are kept apart because the gaps between them are the whole diagnostic:
/// <list type="bullet">
/// <item><b>target ≠ active</b> — our write didn't land, or the change was below
/// <c>CurrentChangeThresholdAmps</c> and was deliberately suppressed.</item>
/// <item><b>active ≠ actual</b> — the <em>car</em> is the limiter: its own charge curve, its taper
/// near full, or its on-board charger's ceiling. This is what separates "the strategy under-delivered"
/// from "the car wouldn't take more", and nothing in the system could tell them apart after the fact
/// before this store existed.</item>
/// </list>
/// </remarks>
public sealed record ChargingSessionSample(
    Guid SessionId,
    DateTimeOffset Timestamp,
    ChargeControlMode Mode,
    ChargeControlState State,
    EvChargerStatus ChargerStatus,
    double BatterySocPercent,
    double SolarPowerWatts,
    double GridPowerWatts,
    double BatteryPowerWatts,
    double EvChargerPowerWatts,
    int EvChargingCurrentAmps,
    int? ActiveCurrentAmps,
    int? TargetCurrentAmps,
    double FromSolarWatts,
    double FromGridWatts,
    double FromBatteryWatts,
    double EnergyDeliveredWh,
    double FromSolarWh,
    double FromGridWh,
    double FromBatteryWh,
    double LoanedWh,
    double? SurplusWatts,
    double LoanPowerWatts,
    bool BatteryHoldActive,
    double? ForecastPowerWatts,
    double? PlanRemainingPvWh,
    double? PlanFeasibleEvEnergyWh,
    double? PlanRequiredSocFloorPercent);
