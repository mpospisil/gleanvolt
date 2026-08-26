using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Charges the car at the maximum current the installation allows, ignoring solar surplus entirely:
/// PV covers what it can and the grid covers the rest. The counterpart — keeping the home battery out
/// of that draw — is the battery discharge hold, which the orchestrator arms when the mode starts and
/// releases when it ends, and which the owner may switch off in between; this controller only decides
/// the current.
///
/// It is the simplest of the three strategies by design. There is no SOC gate, no surplus threshold,
/// no smoothing and no dwell timer, because none of those inputs can change the answer: the setpoint
/// is a constant. What it does watch is when to <em>stop</em>, which it reports as
/// <see cref="ChargingControlDecision.SessionComplete"/> so the expensive state (maximum current, grid
/// import, battery locked) ends by itself rather than sitting armed until somebody notices.
///
/// <para>Two things can end it (#119). The car reaching its own charge limit and stopping, which is
/// the only one this mode had before and is still what happens when nothing else is asked for; and a
/// <see cref="FastChargeLimit"/> — an amount of energy, or a state of charge converted to one at
/// activation — being delivered. The amount is a <b>stopping condition and nothing more</b>: it does
/// not modulate the current, defer anything to a sunnier hour, or make this mode plan. That is what
/// <see cref="TargetedChargingController"/> is for, and the two should not be confused.</para>
///
/// Like the other controllers it only acts while the charger's own use-mode is
/// <see cref="EvChargerMode.Fast"/> — which starting the mode wrote, and which nothing here
/// re-asserts — and it only ever decides the current setpoint.
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

    /// <summary>
    /// The current this mode runs at — the configured maximum, clamped into the hardware's range. A
    /// constant, which is what makes it worth exposing: <see cref="Interfaces.IChargeActions"/> writes
    /// it to the charger the moment the mode is started rather than leaving the first poll to do it, and
    /// reading it from here is what stops the two disagreeing about the figure.
    /// </summary>
    public int ChargeCurrentAmps => _chargeCurrentAmps;

    public ChargingControlDecision Decide(ChargingControlInput input)
    {
        // Precondition: only modulate the current while the charger is in Fast. Starting the mode wrote
        // Fast once; if the charger has left it since, somebody changed it at the wallbox and we don't
        // control it at all -- and we don't end the session either, since we were never driving it.
        if (input.CurrentSettings.Mode != EvChargerMode.Fast)
        {
            return new ChargingControlDecision(
                ChargingControlAction.None, null, $"Charger use-mode is {input.CurrentSettings.Mode}, not Fast; leaving it untouched.");
        }

        // Checked before the idle branch below, and the order is the whole of why: a car that stops
        // drawing at the very moment it reaches the number would otherwise be reported as having hit
        // *its own* limit. Downstream the two are indistinguishable once the mode reads Off, and this
        // is the true one.
        if (input.FastCharge is { IsMet: true } progress)
        {
            return Complete(
                $"Fast target reached: {progress.DeliveredWh / 1000:F1}kWh of "
                + $"{progress.Limit.RequiredEnergyWh / 1000:F1}kWh delivered");
        }

        // "Has finished" is only meaningful once the car has actually started. Before that, no draw
        // means the car isn't ready yet (Preparing, or waiting on its own timer), and ending the mode
        // then would be the opposite of what was asked for.
        if (input.EvDrewPower)
        {
            // Known-disconnected, not merely "not connected": a charger that has stopped answering
            // reports Unknown, and a dropped read is not a car that has gone away.
            if (input.State.EvChargerStatus.IsCarKnownDisconnected())
            {
                return Complete("Car unplugged");
            }

            if (input.EvIdleFor >= _completionDwell)
            {
                // Said in the terms it happened in: the car reached *its* limit before ours, and how
                // far short of the asked-for amount that left things is the first thing anyone will
                // want to know.
                var shortfall = input.FastCharge is { } p
                    ? $" at {p.DeliveredWh / 1000:F1}kWh of the {p.Limit.RequiredEnergyWh / 1000:F1}kWh asked for"
                    : string.Empty;

                return Complete(
                    $"Car stopped drawing for {input.EvIdleFor.TotalMinutes:F0} min (charge limit reached){shortfall}");
            }
        }

        var towards = input.FastCharge is { } left
            ? $" {left.DeliveredWh / 1000:F1}kWh of {left.Limit.RequiredEnergyWh / 1000:F1}kWh delivered, "
              + $"{left.RemainingWh / 1000:F1}kWh to go."
            : string.Empty;

        return new ChargingControlDecision(
            ChargingControlAction.Charge,
            _chargeCurrentAmps,
            $"Fast charge without the battery -> charge at the maximum {_chargeCurrentAmps}A "
            + $"(grid tops up whatever PV doesn't cover).{towards}");
    }

    // Pause rather than None: the charger must be left idle, not armed at the maximum for whatever
    // plugs in next. The orchestrator writes the pause current and then switches the mode to Off.
    private static ChargingControlDecision Complete(string what) =>
        new(ChargingControlAction.Pause, null, $"{what}; pausing and returning to Off.", SessionComplete: true);
}
