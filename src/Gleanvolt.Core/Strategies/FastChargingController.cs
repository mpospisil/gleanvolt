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
/// <para>One thing can defer it (#122). Given a departure, <see cref="FastChargePlanner"/> works out
/// the latest moment the charge can begin and still be finished in time, and until then this pauses.
/// It still changes nothing about <em>how</em> the charge runs — when it does run, it runs flat out.
/// The reason to want it is the pack: a car asked to go above 80% and charged at 22:00 sits there all
/// night, and it is the sitting rather than the charging that ages the cells.</para>
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
        // Unless the Stop it is showing is our own: a deferred charge stands the charger down for the
        // wait (ChargingControlAction.StandDown), and a guard that could not tell that from the owner's
        // Stop would lock this mode out of the charger it is waiting to arm. That is not hypothetical --
        // before the stand-down existed the wallbox reverted to Stop by itself after ~7 minutes at 0A,
        // and this branch then returned None every poll for 8.5 hours while a 19.7kWh overnight charge
        // was due. The flag comes from what we commanded, never from the register.
        if (ChargerOwnership.NotOurs(input) is { } notOurs)
        {
            return notOurs;
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

        if (input.FastCharge?.Plan is { } plan)
        {
            var now = input.State.Timestamp;

            // After the target check above, so a charge that lands exactly on the deadline is reported
            // as having succeeded rather than as having run out of time.
            if (plan.HasDepartedAt(now))
            {
                return Complete(
                    $"Departure {plan.DepartBy.LocalDateTime:HH:mm} has passed with "
                    + $"{input.FastCharge.DeliveredWh / 1000:F1}kWh of "
                    + $"{input.FastCharge.Limit.RequiredEnergyWh / 1000:F1}kWh delivered");
            }

            // Before the idle and unplug branches below, and not merely for tidiness: a car that drew
            // power earlier in this session and is now being held back draws nothing, so the completion
            // dwell would otherwise fire and end the very charge this plan exists to schedule. A mode
            // waiting for 04:12 keeps its appointment; it does not decide the car has finished.
            if (plan.IsWaitingAt(now) && !input.WaitAlreadyReleased)
            {
                // Stood down rather than paused, and the difference is the whole of #135: a pause holds
                // the charger in Fast at 0A, which this wallbox tolerates for minutes and not for hours.
                // A wait that can be most of a night has to be expressed as Stop, the state the hardware
                // actually has for it.
                return new ChargingControlDecision(
                    ChargingControlAction.StandDown,
                    null,
                    $"Waiting until {plan.StartNoLaterThan.LocalDateTime:HH:mm} to start: "
                    + $"{plan.RemainingEnergyWh / 1000:F1}kWh at {plan.ChargePowerWatts / 1000:F1}kW needs "
                    + $"{Describe(plan.Duration)}, and the car is wanted by {plan.ReadyBy.LocalDateTime:HH:mm}.");
            }
        }

        // "Has finished" is only meaningful once the car has actually started *and* while we are still
        // asking it to charge. Before it starts, no draw means the car isn't ready yet (Preparing, or
        // waiting on its own timer). While a plan holds it at the pause current, no draw is our own
        // doing -- and the idle clock runs through that wait, so on the poll the appointment arrives
        // the dwell is already long expired and the mode would end itself at the exact moment it was
        // due to begin. A 07:47 start observed ending as "car stopped drawing for 41 min" is what this
        // guard is for. <see cref="TargetedChargingController"/> has always tested Charging here, for
        // the same reason between its blocks; the departure plan gave this mode the same waiting state
        // without the same guard.
        if (input.Charging && input.EvDrewPower)
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

        // Said while it charges rather than only when the plan is made: a shortfall that appears
        // mid-charge -- because the car turned out to be slower than the wallbox -- is the case nobody
        // is watching for, and it is exactly the case this mode has no slack for.
        if (input.FastCharge?.Plan is { IsFeasible: false } tight)
        {
            towards += $" Not enough time: about {tight.ShortfallWh / 1000:F1}kWh of it will not fit before "
                + $"{tight.ReadyBy.LocalDateTime:HH:mm}.";
        }

        return new ChargingControlDecision(
            ChargingControlAction.Charge,
            _chargeCurrentAmps,
            $"Fast charge without the battery -> charge at the maximum {_chargeCurrentAmps}A "
            + $"(grid tops up whatever PV doesn't cover).{towards}");
    }

    /// <summary>"2 h 44 m", or "44 m" when there is no hour to report. For one log line and one web page.</summary>
    private static string Describe(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours} h {duration.Minutes} m"
        : $"{Math.Max(1, (int)duration.TotalMinutes)} m";

    // Pause rather than None: the charger must be left idle, not armed at the maximum for whatever
    // plugs in next. The orchestrator writes the pause current and then switches the mode to Off.
    private static ChargingControlDecision Complete(string what) =>
        new(ChargingControlAction.Pause, null, $"{what}; pausing and returning to Off.", SessionComplete: true);
}
