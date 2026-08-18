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
/// <param name="SolarWh">
/// PV <b>produced on site</b> since the session opened. Distinct from <paramref name="FromSolarWh"/>,
/// which is only the part attributed to the car: the difference is what the house and the battery took.
/// </param>
/// <param name="ForecastSolarWh">
/// What the forecast expected the roof to make over the same window, integrated from the same
/// per-instant figure as <paramref name="ForecastPowerWatts"/>. Null while no forecast has been
/// available for any part of the session — an honest gap rather than a zero that reads as "predicted
/// nothing". Against <paramref name="SolarWh"/> it is the session's own forecast-versus-reality line.
/// </param>
/// <param name="GridImportWh">
/// Energy imported from the grid <b>by the whole site</b> since the session opened, export excluded.
/// Again distinct from <paramref name="FromGridWh"/>, the car's attributed share: the gap is what the
/// house drew off-peak of the car.
/// </param>
/// <param name="VehicleSocPercent">
/// The <b>car's own</b> battery SOC, as last reported by the vehicle feed, or null when nothing has
/// been received. Nothing here depends on it; it is recorded so a finished session can be read against
/// what the car actually gained.
/// </param>
/// <param name="VehicleSocCapturedAt">
/// When the <em>vehicle</em> produced that reading. Stored alongside it because it routinely lags by
/// hours — a SOC without its capture time cannot be told apart from a live one, and would silently
/// flatten a chart. Null whenever <paramref name="VehicleSocPercent"/> is.
/// </param>
/// <param name="VehicleChargeTimeRemainingMinutes">
/// How much longer the <b>car itself</b> reckons it needs. Its own estimate, not one of ours — it
/// knows its charge curve, its taper and its target, and we know none of those.
///
/// <para><b>0 when the car did not report it</b>, which is not an error: plenty of feeds publish SOC
/// and nothing else. Because 0 is also a legitimate reading ("done"), the two are told apart by
/// <paramref name="VehicleChargeTimeRemainingReported"/> — never read the number without it.</para>
/// </param>
/// <param name="VehicleChargeTimeRemainingReported">
/// Whether the 0 above is the car's answer or the absence of one. False means no estimate was
/// provided, by a feed that isn't configured, hasn't spoken yet, or simply doesn't publish this field.
/// </param>
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
    double SolarWh,
    double? ForecastSolarWh,
    double GridImportWh,
    double? VehicleSocPercent,
    DateTimeOffset? VehicleSocCapturedAt,
    double VehicleChargeTimeRemainingMinutes,
    bool VehicleChargeTimeRemainingReported,
    double? SurplusWatts,
    double LoanPowerWatts,
    bool BatteryHoldActive,
    double? ForecastPowerWatts,
    double? PlanRemainingPvWh,
    double? PlanFeasibleEvEnergyWh,
    double? PlanRequiredSocFloorPercent);
