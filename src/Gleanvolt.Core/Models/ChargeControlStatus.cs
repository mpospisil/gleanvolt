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
/// <param name="ForecastSolarPowerWatts">
/// What the forecast expected the roof to be making <em>at this instant</em> — the direct counterpart
/// to <paramref name="SolarPowerWatts"/>, so the two can be read and charted side by side.
///
/// <para><b>0 when no forecast covers this moment</b>: none fetched yet, the provider is down, or the
/// instant is past the forecast horizon. Reported in every mode, not only the forecast-driven one,
/// because the comparison is worth having whatever is driving the charger.</para>
/// </param>
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
/// <param name="TargetedPlan">
/// The energy-by-departure plan, populated <b>only</b> while
/// <see cref="ChargeControlMode.Targeted"/> is the mode driving the charger — the same rule
/// <paramref name="Plan"/> follows, so a different mode cannot leave a stale target on display.
/// </param>
/// <param name="FastCharge">
/// How a limited fast charge is going, populated <b>only</b> while
/// <see cref="ChargeControlMode.FastNoBattery"/> is the mode driving the charger — the same rule
/// <paramref name="Plan"/> and <paramref name="TargetedPlan"/> follow. Null also means "no limit": the
/// owner asked for <see cref="Enums.FastChargeBasis.Full"/> and the car decides when to stop.
/// </param>
/// <param name="SolarGrid">
/// The forecast's verdict on sun still to come, populated <b>only</b> while
/// <see cref="ChargeControlMode.SolarGrid"/> is the mode driving the charger — the same rule the other
/// mode-owned fields follow.
/// </param>
/// <param name="LowTariff">
/// What <b>low tariff only</b> is doing for the mode driving (issue #274), or null when that mode has
/// the option off or has none: waiting through high tariff, or set with no tariff to wait for.
/// </param>
/// <param name="Timestamp">When this snapshot was taken.</param>
/// <param name="SessionEndReason">
/// This cycle the controller ended the mode itself, and why: the fast, targeted and solar-grid modes all
/// can. Set for the single status that carries the transition, and the only way a later reader can tell
/// "it finished" from "somebody selected Off": by the time <see cref="Mode"/> reads
/// <see cref="ChargeControlMode.Off"/> the two look identical. The session records this reason as its own
/// (#198). Not published to Home Assistant.
/// </param>
/// <param name="ChargerStoodDown">
/// Whether the mode has put the charger in Stop for a wait (#280), rather than holding it in Fast at the
/// pause current. While true the charger's status says nothing about the plug.
/// </param>
/// <param name="WaitUntil">
/// While the mode is waiting, when it expects the wait to end: the next sun, the next tariff edge, the
/// deferred start. Null when it does not know, and whenever it is not waiting.
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
    double ForecastSolarPowerWatts,
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
    TargetedChargePlan? TargetedPlan = null,
    ChargingSessionEndReason? SessionEndReason = null,
    FastChargeProgress? FastCharge = null,
    SolarGridOutlook? SolarGrid = null,
    LowTariffStatus? LowTariff = null,
    bool ChargerStoodDown = false,
    DateTimeOffset? WaitUntil = null)
{
    /// <summary>Whether the mode ended itself this cycle; <see cref="SessionEndReason"/> says why.</summary>
    public bool SessionCompleted => SessionEndReason is not null;
}

/// <summary>
/// What <b>low tariff only</b> is doing for the mode driving (issue #274), for the surfaces to say so.
/// </summary>
/// <param name="Waiting">True while the mode is holding the car back through high tariff.</param>
/// <param name="Until">When low tariff starts, while waiting; null when that is beyond what the tariff knows.</param>
/// <param name="Ignored">
/// True when the option is set but there is no tariff to wait for — none selected on Settings, or the
/// selected one cannot answer — so the mode charges whatever the hour.
/// </param>
public sealed record LowTariffStatus(bool Waiting, DateTimeOffset? Until, bool Ignored);
