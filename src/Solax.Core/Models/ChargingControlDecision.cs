using Solax.Core.Enums;

namespace Solax.Core.Models;

/// <summary>
/// The input a <see cref="Interfaces.IChargingController"/> needs to decide the charging current this
/// cycle.
/// </summary>
/// <param name="State">The latest energy snapshot (carries SOC and the solar surplus source).</param>
/// <param name="SurplusWatts">
/// The solar surplus to decide on — the <em>smoothed</em> (moving-average) value, not the
/// instantaneous <see cref="EnergyState.SolarSurplusPowerWatts"/>, so a momentary cloud doesn't
/// interrupt a long charging session.
/// </param>
/// <param name="CurrentSettings">The charger's currently active settings (use-mode + current setpoint), read from the hardware.</param>
/// <param name="Charging">Whether we are currently charging (vs paused) — our own state, used for hysteresis.</param>
/// <param name="Plan">
/// The forecast-driven day plan, when one is available. Only the forecast-driven controller uses it;
/// live-solar control ignores it entirely, which is why it defaults to null.
/// </param>
/// <param name="TimeInCurrentState">
/// How long we have been charging (or paused) without interruption, for the dwell timers that stop the
/// charger being started and stopped every few minutes.
/// </param>
/// <param name="SessionEnergyWh">Energy delivered to the car in the current session (since it was plugged in).</param>
/// <param name="LoanedTodayWh">Energy lent out of the home battery to the car so far today.</param>
/// <param name="EvDrewPower">
/// Whether the car has drawn meaningful power at least once since it was plugged in. Distinguishes a
/// car that has finished from one that has not started yet (still Preparing, or waiting on its own
/// schedule) — the two look identical on power alone.
/// </param>
/// <param name="EvIdleFor">
/// How long the charger has continuously reported no meaningful draw (or a car-initiated wind-down).
/// Zero while the car is drawing. Only the fast mode acts on it, which is why both default away.
/// </param>
public sealed record ChargingControlInput(
    EnergyState State,
    double SurplusWatts,
    EvChargerSettings CurrentSettings,
    bool Charging,
    SolarDayPlan? Plan = null,
    TimeSpan TimeInCurrentState = default,
    double SessionEnergyWh = 0,
    double LoanedTodayWh = 0,
    bool EvDrewPower = false,
    TimeSpan EvIdleFor = default);

/// <summary>
/// The controller's intent for this cycle. <see cref="ChargeCurrentAmps"/> is populated only for
/// <see cref="ChargingControlAction.Charge"/> (the current to set); <see cref="ChargingControlAction.Pause"/>
/// and <see cref="ChargingControlAction.None"/> leave it null. <see cref="Reason"/> is a short
/// human-readable explanation for logging.
/// </summary>
/// <param name="LoanPowerWatts">
/// How much of the commanded current is being covered by the home battery rather than live sun — the
/// bridge that lets a sub-minimum surplus still reach the charger's 6 A floor. Zero unless the
/// forecast-driven controller granted a loan; the orchestrator meters it against the daily cap.
/// </param>
/// <param name="SessionComplete">
/// The controller's one way of saying "this is over": the car has finished (or has gone away) and
/// there is nothing left to control. Accompanies a <see cref="ChargingControlAction.Pause"/> so the
/// charger is left idle rather than armed at the last setpoint; the orchestrator then switches the
/// mode back to <see cref="ChargeControlMode.Off"/>. Only the fast mode ever sets it.
/// </param>
public sealed record ChargingControlDecision(
    ChargingControlAction Action,
    int? ChargeCurrentAmps,
    string Reason,
    double LoanPowerWatts = 0,
    bool SessionComplete = false);
