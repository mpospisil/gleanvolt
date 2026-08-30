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

        // Sustained, so the charger really has left Fast underneath us: the car finished and the wallbox
        // went Finishing -> Stop, or its owner took it. Either way this mode is no longer driving
        // anything and should say so rather than sit "on" doing nothing -- observed on 2026-08-24 as
        // Targeted logging "leaving it untouched" every eight seconds for 2 h 19 min after the car
        // stopped.
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
                SessionComplete: true);
        }

        return new ChargingControlDecision(
            ChargingControlAction.None, null, $"Charger use-mode is {input.CurrentSettings.Mode}, not Fast; leaving it untouched.");
    }
}
