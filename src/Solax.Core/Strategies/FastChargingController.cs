using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;

namespace Solax.Core.Strategies;

/// <summary>
/// Charges the car at the maximum current the installation allows, ignoring solar surplus entirely:
/// PV covers what it can and the grid covers the rest. The counterpart — keeping the home battery out
/// of that draw — is the battery discharge hold, armed by the orchestrator for as long as this mode is
/// selected; this controller only decides the current.
///
/// It is the simplest of the three strategies by design. There is no SOC gate, no surplus threshold,
/// no smoothing and no dwell timer, because none of those inputs can change the answer: the setpoint
/// is a constant. The one thing it does watch is when the car <em>stops</em> drawing, which it reports
/// as <see cref="ChargingControlDecision.SessionComplete"/> so the expensive state (maximum current,
/// grid import, battery locked) ends by itself rather than sitting armed until somebody notices.
///
/// Like the other controllers it only acts while the charger's own use-mode is
/// <see cref="EvChargerMode.Fast"/>, and it only ever writes the current setpoint.
/// </summary>
public sealed class FastChargingController : IChargingController
{
    private readonly int _chargeCurrentAmps;
    private readonly TimeSpan _completionDwell;

    /// <param name="maxChargingCurrentAmps">
    /// The current to pin the charger at, clamped into the hardware's accepted range. This is the
    /// site's supply limit rather than a preference: this mode is the one that will actually sit at it
    /// for hours, drawing the shortfall from the grid.
    /// </param>
    /// <param name="completionDwell">
    /// How long the car must draw nothing before the session counts as finished. Guards against a
    /// momentary dip — and against the gap between the charger accepting our setpoint and the car
    /// actually starting.
    /// </param>
    public FastChargingController(int maxChargingCurrentAmps, TimeSpan completionDwell)
    {
        _chargeCurrentAmps = Math.Clamp(maxChargingCurrentAmps, EvChargerLimits.MinCurrentAmps, EvChargerLimits.MaxCurrentAmps);
        _completionDwell = completionDwell;
    }

    public ChargingControlDecision Decide(ChargingControlInput input)
    {
        // Precondition: only modulate the current while the owner has the charger in Fast mode. In any
        // other mode we don't control it at all -- and we don't end the session either, since we were
        // never driving it.
        if (input.CurrentSettings.Mode != EvChargerMode.Fast)
        {
            return new ChargingControlDecision(
                ChargingControlAction.None, null, $"Charger use-mode is {input.CurrentSettings.Mode}, not Fast; leaving it untouched.");
        }

        // "Has finished" is only meaningful once the car has actually started. Before that, no draw
        // means the car isn't ready yet (Preparing, or waiting on its own timer), and ending the mode
        // then would be the opposite of what was asked for.
        if (input.EvDrewPower)
        {
            if (!input.State.EvChargerStatus.IsCarConnected())
            {
                return Complete("Car unplugged");
            }

            if (input.EvIdleFor >= _completionDwell)
            {
                return Complete($"Car stopped drawing for {input.EvIdleFor.TotalMinutes:F0} min (charge limit reached)");
            }
        }

        return new ChargingControlDecision(
            ChargingControlAction.Charge,
            _chargeCurrentAmps,
            $"Fast charge without the battery -> charge at the maximum {_chargeCurrentAmps}A (grid tops up whatever PV doesn't cover).");
    }

    // Pause rather than None: the charger must be left idle, not armed at the maximum for whatever
    // plugs in next. The orchestrator writes the pause current and then switches the mode to Off.
    private static ChargingControlDecision Complete(string what) =>
        new(ChargingControlAction.Pause, null, $"{what}; pausing and returning to Off.", SessionComplete: true);
}
