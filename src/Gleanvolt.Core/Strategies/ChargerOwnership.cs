using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// The one rule every controller applies before it decides anything: is this charger ours to drive?
///
/// <para>A wallbox whose owner has set it to Eco, Green or Stop is not, and each mode used to say so in
/// its own copy of the same four lines. It is one rule and it now lives in one place, because the two
/// exceptions below both had to be added to all four at once and a rule kept in four copies is how the
/// fourth one gets missed.</para>
/// </summary>
public static class ChargerOwnership
{
    /// <summary>
    /// How long a non-Fast use-mode has to persist before it is believed rather than ignored.
    ///
    /// <para>Comfortably longer than the transients this installation actually produces — its charger
    /// drops its Modbus link about 45 times a day and reports junk use-modes on recovery, all ten
    /// lifetime sightings of <see cref="EvChargerMode.Eco"/> falling inside one two-minute window
    /// immediately after "reachable again", interleaved with Stop. Those blips last seconds. A reading
    /// held for two minutes is the hardware's actual state.</para>
    /// </summary>
    public static readonly TimeSpan DropoutGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The one rule for "the car is done" (#280), together with the ownership guard, in the order they
    /// have to be asked. Null when the caller should carry on deciding. The car is only ever judged while
    /// the mode is asking it to charge: between charges, the car drawing nothing is the mode's own doing.
    ///
    /// <list type="number">
    /// <item><description><b>The wallbox closed the session.</b> On a full car this one reads
    /// <c>Charging / Fast</c> at full power on one poll and <c>Finishing / Stop / 0 W</c> on the next:
    /// no taper and no <see cref="EvChargerStatus.SuspendedEv"/>. All three
    /// <see cref="ChargingSessionEndReason.ChargerTakenOver"/> endings of 13–26 October 2026 were this.
    /// Believed at once when the car was drawing on the poll before, which is what tells it from the
    /// wallbox giving up on our own 0 A pause: that also reads Finishing / Stop, but after a pause, with
    /// nothing drawing. Asked <em>before</em> the ownership guard, because the wallbox has already left
    /// Fast by the time it is seen, and two minutes later the guard would file it as taken
    /// over.</description></item>
    /// <item><description><b>The charger is not ours</b> (<see cref="NotOurs"/>).</description></item>
    /// <item><description><b>The car went away</b>, positively reported: Unknown is a failed read, and
    /// ending the mode on one costs the owner the whole charge.</description></item>
    /// <item><description><b>The car stopped drawing</b> for <paramref name="completionDwell"/>: the
    /// fallback for a wallbox that stays in Fast at 0 W. Fast, Targeted and SolarGrid each kept a copy of
    /// this; Solar and Forecasted had none and never ended on a full car at all.</description></item>
    /// </list>
    ///
    /// <para>A mode's own endings — the energy delivered, the departure passed — are checked by the mode
    /// before this, so a car that stops at the very moment it reaches the number is reported as having
    /// reached it.</para>
    /// </summary>
    /// <param name="input">This cycle's input.</param>
    /// <param name="completionDwell">How long a car in Fast may draw nothing before it counts as finished.</param>
    /// <param name="finishedDetail">Appended to the reason for a full car, e.g. how much of an amount it took.</param>
    public static ChargingControlDecision? Check(ChargingControlInput input, TimeSpan completionDwell, string? finishedDetail = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        var judged = input.Charging && !input.ChargerStoodDown;
        var status = input.State.EvChargerStatus;

        if (judged && input.EvWasDrawing && status.IsChargeWindingDown())
        {
            return End(
                ChargingSessionEndReason.SessionComplete,
                $"The charger reports {status} straight after the car was drawing (charge limit reached){finishedDetail}");
        }

        if (NotOurs(input) is { } notOurs)
        {
            return notOurs;
        }

        if (!judged || !input.EvDrewPower)
        {
            return null;
        }

        if (status.IsCarKnownDisconnected())
        {
            return End(ChargingSessionEndReason.CarUnplugged, "Car unplugged");
        }

        if (input.EvIdleFor >= completionDwell)
        {
            return End(
                ChargingSessionEndReason.SessionComplete,
                $"Car stopped drawing for {input.EvIdleFor.TotalMinutes:F0} min (charge limit reached){finishedDetail}");
        }

        return null;
    }

    /// <summary>
    /// A decision that ends the mode. Pause rather than None: the charger must be left idle, not armed at
    /// the last setpoint for whatever plugs in next. The orchestrator writes the pause current and then
    /// switches the mode to Off.
    /// </summary>
    public static ChargingControlDecision End(ChargingSessionEndReason reason, string what) =>
        new(ChargingControlAction.Pause, null, $"{what}; pausing and returning to Off.", EndsSession: reason);

    /// <summary>
    /// The decision to return when the charger is not ours, or <c>null</c> when it is and the caller
    /// should carry on deciding.
    /// </summary>
    public static ChargingControlDecision? NotOurs(ChargingControlInput input)
    {
        if (input.CurrentSettings.Mode == EvChargerMode.Fast)
        {
            return null;
        }

        // Our own Stop, set to wait out a deferred start. Without this the mode is locked out of the
        // charger it is waiting to arm -- the 8.5-hour inert overnight charge of 2026-08-28.
        if (input.ChargerStoodDown)
        {
            return null;
        }

        // Sustained, so the charger really has left Fast underneath us: its owner took it, or the car
        // finished in a way Check could not see (the poll that showed it drawing was lost). Either
        // way this mode is no longer driving anything and should say so rather than sit "on" doing
        // nothing -- observed on 2026-08-24 as Targeted logging "leaving it untouched" every eight
        // seconds for 2 h 19 min after the car stopped.
        //
        // Action is None, never Pause: writing a current to a charger that is not ours is exactly what
        // the guard exists to prevent, and the orchestrator's stop path writes the use-mode anyway.
        if (input.ChargerNotFastFor >= DropoutGrace)
        {
            return new ChargingControlDecision(
                ChargingControlAction.None,
                null,
                $"The charger has been in {input.CurrentSettings.Mode} rather than Fast for "
                + $"{input.ChargerNotFastFor.TotalMinutes:F0} min, so it is no longer ours to drive; returning to Off.",
                EndsSession: ChargingSessionEndReason.ChargerTakenOver);
        }

        return new ChargingControlDecision(
            ChargingControlAction.None, null, $"Charger use-mode is {input.CurrentSettings.Mode}, not Fast; leaving it untouched.");
    }
}
