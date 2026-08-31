using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

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
/// <param name="TargetedPlan">
/// The energy-by-departure plan, when the targeted mode is the one driving. Only
/// <see cref="Strategies.TargetedChargingController"/> reads it, which is why it defaults to null and
/// no other controller or test has to know it exists.
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
/// <param name="FastCharge">
/// How a limited fast charge is going — the amount asked for and what has been delivered against it —
/// when the fast mode is the one driving and a limit was set. Null both when another mode is driving
/// and when the owner asked for <see cref="Enums.FastChargeBasis.Full"/>, which is the ordinary case:
/// only <see cref="Strategies.FastChargingController"/> reads it, and it charges the same either way
/// until the number is met.
/// </param>
/// <param name="ChargerStoodDown">
/// Whether the charger's use-mode reads Stop because <em>we</em> put it there for a long wait
/// (<see cref="Enums.ChargingControlAction.StandDown"/>), rather than because its owner did.
///
/// <para>Every controller refuses to touch a charger whose use-mode is not Fast, and must: a wallbox
/// the owner has set to Eco, Green or Stop is not ours to drive. But a deferred charge that stands the
/// charger down would then be locked out of the very charger it is waiting to arm, and would sit inert
/// until somebody noticed. This says "that Stop is ours", and it is tracked from what the coordinator
/// commanded, never inferred from the register: this installation's charger drops its Modbus link about
/// 45 times a day and reports transient junk use-modes on recovery (Eco has been observed), so a
/// reading is not a statement of intent.</para>
/// </param>
/// <param name="ChargerNotFastFor">
/// How long the charger's use-mode has read something other than Fast, without interruption. Zero
/// while it reads Fast. Lets <see cref="Strategies.ChargerOwnership"/> tell a charger that has really
/// left our control from the seconds-long junk values this installation's charger emits when its
/// Modbus link recovers.
/// </param>
/// <param name="WaitAlreadyReleased">
/// Whether a wait this mode was standing the charger down for has already been released -- the
/// charger was armed out of a stand-down and the car started drawing.
///
/// <para>An appointment is a one-way gate, and this is what makes it one. The start time is recomputed
/// every poll from the measured charge rate, and the rate <em>rises</em> once the car is actually
/// drawing: 10.5kW became 10.9kW on 2026-08-31, which cut the estimate from 28 minutes to 27 and so
/// moved the latest safe start from 04:16 to 04:17 -- back into the future. The mode stood the charger
/// down again seventeen seconds after starting it, and only got going for good a minute later. Once a
/// wait has been released it stays released.</para>
/// </param>
public sealed record ChargingControlInput(
    EnergyState State,
    double SurplusWatts,
    EvChargerSettings CurrentSettings,
    bool Charging,
    SolarDayPlan? Plan = null,
    TargetedChargePlan? TargetedPlan = null,
    TimeSpan TimeInCurrentState = default,
    double SessionEnergyWh = 0,
    double LoanedTodayWh = 0,
    bool EvDrewPower = false,
    TimeSpan EvIdleFor = default,
    FastChargeProgress? FastCharge = null,
    bool ChargerStoodDown = false,
    TimeSpan ChargerNotFastFor = default,
    bool WaitAlreadyReleased = false);

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
/// <param name="GridBridgeWatts">
/// The same bridge, paid for by the <b>grid</b> instead of the pack — the targeted mode's answer to a
/// surplus that is real but under the charger's floor. Non-zero only while that bridge is being
/// granted, and the host arms the battery discharge hold on it exactly as it does inside a planned
/// grid block: the car is running partly on imported energy either way, and the pack must stay out
/// of it.
/// </param>
/// <param name="SessionComplete">
/// The controller's one way of saying "this is over": what was asked for has been delivered, or the
/// car has finished on its own (or gone away), and there is nothing left to control. Accompanies a
/// <see cref="ChargingControlAction.Pause"/> so the charger is left idle rather than armed at the last
/// setpoint; the orchestrator then switches the mode back to <see cref="ChargeControlMode.Off"/>,
/// which also releases the hold the fast mode armed.
///
/// <para>Set by the fast and targeted modes. The solar and forecast-driven ones never do: they follow
/// the sun for as long as they are selected, and a car that has stopped taking their surplus has not
/// ended anything.</para>
/// </param>
public sealed record ChargingControlDecision(
    ChargingControlAction Action,
    int? ChargeCurrentAmps,
    string Reason,
    double LoanPowerWatts = 0,
    bool SessionComplete = false,
    double GridBridgeWatts = 0);
