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
/// <param name="LoanOutstandingWh">
/// Of that, the part not yet recovered — lending less everything the pack has charged back since, never
/// below zero. What the daily cap is measured against (issue #223): a pack refilled to 100% by lunchtime
/// is physically where it started, and owes nothing.
/// </param>
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
///
/// <para>Only an <see cref="ChargingControlDecision.Appointment"/> latches it. Since #280 every mode stands
/// the charger down for a long wait for sun, and a targeted charge re-armed out of one overnight still has
/// its held tail to keep in the afternoon.</para>
/// </param>
/// <param name="Outlook">
/// What the rest of today's forecast says about sun clearing the driving mode's minimum: the owner's
/// minimum for <see cref="Enums.ChargeControlMode.SolarGrid"/>, the charger's floor for
/// <see cref="Enums.ChargeControlMode.Solar"/>, and the lowest surplus a battery loan can bridge for
/// <see cref="Enums.ChargeControlMode.Forecasted"/>. It says until when a wait for sun lasts
/// (<see cref="Strategies.ChargerWait.NextSun"/>) and when the day is over (#280). Null for the modes
/// that do not wait for sun.
/// </param>
/// <param name="ChargedThisMode">
/// Whether the car has drawn power while this mode had it charging — since the running mode was
/// selected, not since the car was plugged in. What a <em>restart</em> dwell has to know:
/// <see cref="EvDrewPower"/> also counts a charge the charger started by itself at plug-in, before any
/// mode was chosen, and a dwell gated on that stops a running car on the mode's first decision to wait
/// out a "restart" of a charge the mode never ran (2026-09-13).
/// </param>
/// <param name="LowTariffOnly">
/// The tariff, when the owner has set the mode driving to <b>low tariff only</b> and a tariff is known
/// (issue #274); null otherwise, which means no restriction. Only
/// <see cref="Strategies.FastChargingController"/> and <see cref="Strategies.SolarGridChargingController"/>
/// read it, through <see cref="Strategies.TariffWait"/>.
/// </param>
/// <param name="EvWasDrawing">
/// Whether the car was drawing on the last poll the charger answered before this one. What tells a
/// full car from the wallbox giving up on our own pause: both read Finishing / Stop, and only the
/// first comes straight after a draw (see <see cref="Strategies.ChargerOwnership.Check"/>).
/// </param>
/// <param name="UntilFull">
/// Whether the owner has set <see cref="Enums.ChargeControlMode.Forecasted"/>, the mode driving, to
/// <b>until full</b> (issue #279): the end of the day stands the charger down until the next day's sun
/// instead of ending the mode (<see cref="Strategies.ChargerWait.DayIsOver"/>). False for every other mode.
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
    bool WaitAlreadyReleased = false,
    SolarGridOutlook? Outlook = null,
    bool ChargedThisMode = false,
    double LoanOutstandingWh = 0,
    TariffGate? LowTariffOnly = null,
    bool EvWasDrawing = false,
    bool UntilFull = false);

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
/// <param name="EndsSession">
/// The controller's one way of saying "this is over", and why: what was asked for has been delivered, the
/// departure has passed, the car has finished on its own or gone away, the day's sun is spent, or the
/// charger is no longer ours to drive. Accompanies a <see cref="ChargingControlAction.Pause"/> so the
/// charger is left idle rather than armed at the last setpoint (or a <see cref="ChargingControlAction.None"/>
/// when the charger is not ours to write to); the orchestrator then switches the mode back to
/// <see cref="ChargeControlMode.Off"/>, which also releases the hold the fast mode armed.
///
/// <para>A reason rather than a flag (#198). With a flag, every one of those endings was recorded as "the
/// car finished charging"; now a completion cannot be written without saying which it is. Every mode can
/// set it: the car finishing, going away or the charger being taken are one rule in
/// <see cref="Strategies.ChargerOwnership"/>, and the day's sun running out is the end of the three solar
/// modes (#280).</para>
/// </param>
/// <param name="WaitUntil">
/// For a pause or a stand-down, when the mode expects the wait to end; null when it does not know. For the
/// surfaces to say "stood down until 09:40".
/// </param>
/// <param name="Appointment">
/// This stand-down waits for a time the mode set itself — the fast mode's deferred start, the targeted
/// mode's held tail — and its release is one-way (<see cref="ChargingControlInput.WaitAlreadyReleased"/>).
/// A wait for sun or for low tariff is not one: re-arming out of it must not stop a later appointment
/// from being kept.
/// </param>
public sealed record ChargingControlDecision(
    ChargingControlAction Action,
    int? ChargeCurrentAmps,
    string Reason,
    double LoanPowerWatts = 0,
    ChargingSessionEndReason? EndsSession = null,
    double GridBridgeWatts = 0,
    DateTimeOffset? WaitUntil = null,
    bool Appointment = false)
{
    /// <summary>Whether this decision ends the mode; <see cref="EndsSession"/> says why.</summary>
    public bool SessionComplete => EndsSession is not null;
}
