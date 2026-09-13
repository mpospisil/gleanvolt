using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// The one rule for the restart dwell, shared by every mode that has one (SolarGrid, Forecasted and
/// Targeted): a paused charge this mode ran is not restarted until it has been paused for
/// <c>MinPauseTime</c>.
///
/// <para><b>Why the dwell exists</b> (2026-08-08). Every stop and start is a contactor cycle in the wallbox
/// and a vehicle wake, and a surplus hovering at a threshold would otherwise cost one of each per passing
/// cloud. On 2026-08-18 it was the only thing keeping a floor that flipped on one percent of SOC from
/// cycling the car every few polls.</para>
///
/// <para><b>Why it is gated on this mode's own charge.</b> It guards a <em>restart</em>. Before the running
/// mode has charged the car there is nothing to restart, and applying it there only throws sun away.
/// Each mode used to decide that for itself, and three got it wrong in different ways: Targeted counted
/// the wait from the moment a target was activated (fixed 2026-08-23), Forecasted from the moment the
/// mode was selected, and SolarGrid from any draw since the plug-in. This charger starts a car by itself
/// when it is plugged in, so on 2026-09-12 and 2026-09-13 SolarGrid's first decision stopped a running
/// car and waited 15 minutes with 2–5 kW of surplus.
/// <see cref="ChargingControlInput.ChargedThisMode"/> is the coordinator's answer to "did this mode make
/// the car draw?", and this is the only place a mode asks the question.</para>
///
/// <para>Where a mode asks it stays the mode's business: a hard stop still pauses regardless, and a
/// deadline-driven pace is never deferred by it.</para>
/// </summary>
public static class RestartDwell
{
    /// <summary>
    /// Whether a restart has to wait: paused, after a charge this mode ran, for less than
    /// <paramref name="minPauseTime"/>.
    /// </summary>
    public static bool Holds(ChargingControlInput input, TimeSpan minPauseTime)
    {
        ArgumentNullException.ThrowIfNull(input);

        return !input.Charging
            && input.ChargedThisMode
            && input.TimeInCurrentState < minPauseTime;
    }

    /// <summary>The log line for a restart held back by the dwell.</summary>
    public static string Reason(ChargingControlInput input, TimeSpan minPauseTime)
    {
        ArgumentNullException.ThrowIfNull(input);

        return $"Paused {input.TimeInCurrentState.TotalMinutes:F0}min of the {minPauseTime.TotalMinutes:F0}min minimum before restarting.";
    }
}
