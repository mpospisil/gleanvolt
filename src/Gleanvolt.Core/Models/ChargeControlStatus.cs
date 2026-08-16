using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// A snapshot of what charge control is doing, for reporting to the outside world (e.g. Home
/// Assistant). Pure data — assembled once per poll and published by the integration layer.
/// </summary>
/// <param name="Mode">The selected mode (Off / Solar / Force).</param>
/// <param name="DryRun">Whether it is in dry-run (deciding and logging, but not writing to the charger).</param>
/// <param name="HoldingControl">Whether it is currently driving the charger (took control of a session).</param>
/// <param name="State">Coarse state for display.</param>
/// <param name="SurplusWatts">The (averaged) solar surplus it is deciding on, or null when not solar-charging.</param>
/// <param name="TargetCurrentAmps">The current it wants to charge at, or null when not charging.</param>
/// <param name="ActiveCurrentAmps">The charger's active current setpoint as read back, or null if unknown.</param>
/// <param name="BatterySocPercent">Home battery state of charge.</param>
/// <param name="ChargerStatus">The charger's status (Available / Preparing / Charging / ChargePaused / ...).</param>
/// <param name="CarConnected">Whether a vehicle is plugged into the charger.</param>
/// <param name="SolarPowerWatts">Actual solar (PV) production.</param>
/// <param name="EvChargerPowerWatts">Actual power the EV charger is drawing.</param>
/// <param name="EvChargingCurrentAmps">Actual charging current, derived from the charger power (phase-aware).</param>
/// <param name="BatteryPowerWatts">Battery power: positive = charging, negative = discharging.</param>
/// <param name="GridPowerWatts">Grid meter power: positive = importing, negative = exporting.</param>
/// <param name="BatteryHoldEnabled">Whether the battery discharge hold feature is configured on at all.</param>
/// <param name="BatteryHoldRequested">Whether the hold is switched on (the user's intent).</param>
/// <param name="BatteryHoldActive">
/// Whether a hold command is currently armed on the inverter. This is what we last wrote, not a
/// device read-back — the command register cannot be read (see <see cref="Interfaces.IBatteryDischargeControl"/>).
/// </param>
/// <param name="BatteryHoldTargetWatts">The active-power target currently commanded, or null when not held.</param>
/// <param name="Plan">
/// The forecast-driven day plan behind the decision, or null when the mode isn't using one. Carries
/// the outlook, budgets, SOC floor and charge window reported to Home Assistant.
/// </param>
/// <param name="LoanPowerWatts">How much of the current charge the home battery is lending right now.</param>
/// <param name="SessionEnergyWh">Energy delivered to the car in the current session.</param>
/// <param name="LoanedTodayWh">Energy lent out of the home battery today.</param>
/// <param name="TomorrowForecastWh">Tomorrow's forecast production, purely informational.</param>
/// <param name="Timestamp">When this snapshot was taken.</param>
/// <param name="SessionCompleted">
/// This cycle the controller reported the car had finished and the mode ended itself (only
/// <see cref="ChargeControlMode.FastNoBattery"/> does). True for the single status that carries the
/// transition, and the only way a later reader can tell "the car filled up" from "somebody selected
/// Off" — by the time <see cref="Mode"/> reads <see cref="ChargeControlMode.Off"/> the two look
/// identical. Not published to Home Assistant.
/// </param>
public sealed record ChargeControlStatus(
    ChargeControlMode Mode,
    bool DryRun,
    bool HoldingControl,
    ChargeControlState State,
    double? SurplusWatts,
    int? TargetCurrentAmps,
    int? ActiveCurrentAmps,
    double BatterySocPercent,
    EvChargerStatus ChargerStatus,
    bool CarConnected,
    double SolarPowerWatts,
    double EvChargerPowerWatts,
    int EvChargingCurrentAmps,
    double BatteryPowerWatts,
    double GridPowerWatts,
    bool BatteryHoldEnabled,
    bool BatteryHoldRequested,
    bool BatteryHoldActive,
    double? BatteryHoldTargetWatts,
    SolarDayPlan? Plan,
    double LoanPowerWatts,
    double SessionEnergyWh,
    double LoanedTodayWh,
    double? TomorrowForecastWh,
    DateTimeOffset Timestamp,
    bool SessionCompleted = false);
