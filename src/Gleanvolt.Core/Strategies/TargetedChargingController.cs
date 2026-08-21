using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Drives the charger from a <see cref="TargetedChargePlan"/>: charge flat out inside the planned grid
/// block (and whenever there isn't enough time left), take what the sun offers inside a solar block,
/// and wait in between.
///
/// <para>Almost all of the intelligence is in the planner, which is rebuilt every poll — this
/// controller only asks where "now" falls in the current plan. That is what makes the mode
/// self-correcting: nothing here remembers a decision, so a sunnier afternoon than forecast simply
/// arrives as a plan with a smaller grid block, and the block is never drawn.</para>
///
/// <para>Pure and side-effect free, like every other strategy, and it only modulates the current while
/// the charger's own use-mode is <see cref="EvChargerMode.Fast"/>. There is no battery loan in this
/// mode: the home battery keeps its priority and the grid is the honest source for the gap.</para>
/// </summary>
public sealed class TargetedChargingController : IChargingController
{
    private readonly ChargePowerConverter _power;
    private readonly TargetedChargingOptions _options;
    private readonly int _minAmps;
    private readonly int _maxAmps;

    public TargetedChargingController(ChargePowerConverter powerConverter, TargetedChargingOptions options)
    {
        _power = powerConverter ?? throw new ArgumentNullException(nameof(powerConverter));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.CurrentStepAmps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.CurrentStepAmps, "Current step must be positive.");
        }

        _minAmps = Math.Clamp(options.MinChargingCurrentAmps, EvChargerLimits.MinCurrentAmps, EvChargerLimits.MaxCurrentAmps);
        _maxAmps = Math.Clamp(options.MaxChargingCurrentAmps, _minAmps, EvChargerLimits.MaxCurrentAmps);
    }

    /// <summary>The charger's minimum viable power — the line a solar block's surplus has to clear.</summary>
    public double MinChargePowerWatts => _power.AmpsToWatts(_minAmps);

    public ChargingControlDecision Decide(ChargingControlInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Precondition, unchanged from every other mode: the owner keeps the charger in Fast mode and
        // we only modulate the current under it.
        if (input.CurrentSettings.Mode != EvChargerMode.Fast)
        {
            return new ChargingControlDecision(
                ChargingControlAction.None, null, $"Charger use-mode is {input.CurrentSettings.Mode}, not Fast; leaving it untouched.");
        }

        var plan = input.TargetedPlan;
        if (plan is null)
        {
            // The mode is selected but nothing has been asked for. Pausing rather than completing:
            // "no target set" is a state the owner can fix from the page, not a finished session.
            return Pause("No target set: enter the energy and departure time to start.");
        }

        var now = input.State.Timestamp;

        if (plan.IsComplete)
        {
            return Complete($"Target reached: {plan.DeliveredEnergyWh / 1000:F1}kWh of {plan.RequiredEnergyWh / 1000:F1}kWh delivered");
        }

        if (now >= plan.DepartBy)
        {
            return Complete(
                $"Departure {plan.DepartBy.LocalDateTime:HH:mm} has passed with {plan.DeliveredEnergyWh / 1000:F1}kWh "
                + $"of {plan.RequiredEnergyWh / 1000:F1}kWh delivered");
        }

        // "The car has stopped" is only meaningful while we are asking it to charge. Between blocks we
        // are the ones holding it at the pause current, and reading that as a finished session would
        // end the mode every time it waited for the sun.
        if (input.Charging && input.EvDrewPower)
        {
            // Known-disconnected, not merely "not connected": Unknown is a failed read, and ending
            // the mode on one costs the owner the whole request — there is nothing left to restart it.
            if (input.State.EvChargerStatus.IsCarKnownDisconnected())
            {
                return Complete("Car unplugged");
            }

            if (input.EvIdleFor >= _options.CompletionDwell)
            {
                return Complete(
                    $"Car stopped drawing for {input.EvIdleFor.TotalMinutes:F0} min at {plan.DeliveredEnergyWh / 1000:F1}kWh "
                    + $"of {plan.RequiredEnergyWh / 1000:F1}kWh — its own limit, short of the target");
            }
        }

        // The two cases that take everything: no time left to be clever, or the planned import window
        // has arrived. Both run at the maximum current with the discharge hold armed by the host.
        if (plan.Strategy == TargetedChargeStrategy.Maximum)
        {
            return new ChargingControlDecision(
                ChargingControlAction.Charge,
                _maxAmps,
                $"Not enough time for {plan.RemainingEnergyWh / 1000:F1}kWh by {plan.DepartBy.LocalDateTime:HH:mm} -> "
                + $"charge at the maximum {_maxAmps}A; expect {plan.ExpectedEnergyWh / 1000:F1}kWh ({plan.ShortfallWh / 1000:F1}kWh short).");
        }

        if (plan.IsInGridBlock(now))
        {
            return new ChargingControlDecision(
                ChargingControlAction.Charge,
                _maxAmps,
                $"Grid top-up until {plan.Deadline.LocalDateTime:HH:mm} for the last {plan.RemainingEnergyWh / 1000:F1}kWh "
                + $"-> charge at the maximum {_maxAmps}A.");
        }

        if (plan.IsInSolarBlock(now))
        {
            return DecideFromSurplus(input, plan);
        }

        return Waiting(input, plan);
    }

    /// <summary>
    /// Inside a solar block: take what the surplus supports, with the same dwell hysteresis the other
    /// solar modes use — and the plan's SOC floor still gating, because the home battery's priority is
    /// not suspended just because the car has a deadline. Anything the sun cannot cover is the grid
    /// block's problem, and the grid block is placed to solve it.
    /// </summary>
    private ChargingControlDecision DecideFromSurplus(ChargingControlInput input, TargetedChargePlan plan)
    {
        var soc = input.State.BatterySocPercent;
        if (soc < plan.SocFloorPercent)
        {
            return Pause(
                $"Battery {soc:F0}% is below the plan's {plan.SocFloorPercent:F0}% floor; the pack has priority "
                + $"and the {GridText(plan)} covers the target.");
        }

        if (!input.Charging && input.TimeInCurrentState < _options.MinPauseTime)
        {
            return Pause($"Paused {input.TimeInCurrentState.TotalMinutes:F0}min of the {_options.MinPauseTime.TotalMinutes:F0}min minimum before restarting.");
        }

        var surplusWatts = input.SurplusWatts;

        // Asymmetric, as everywhere else: keep charging down to the minimum, but only (re)start a
        // hysteresis margin above it.
        var startThresholdWatts = input.Charging ? MinChargePowerWatts : MinChargePowerWatts + _options.ResumeHysteresisWatts;
        if (surplusWatts < startThresholdWatts)
        {
            return SoftPause(input, $"Surplus {surplusWatts:F0}W below the {(input.Charging ? "minimum" : "start")} threshold {startThresholdWatts:F0}W.");
        }

        var targetAmps = ToHardwareCurrent(surplusWatts);
        if (targetAmps < _minAmps)
        {
            return SoftPause(input, $"Surplus {surplusWatts:F0}W quantises below the minimum {_minAmps}A.");
        }

        return new ChargingControlDecision(
            ChargingControlAction.Charge,
            targetAmps,
            $"Solar share of the target: {plan.SolarEnergyWh / 1000:F1}kWh planned from sun, surplus {surplusWatts:F0}W -> charge at {targetAmps}A.");
    }

    /// <summary>
    /// Between the blocks. The sentence matters more than the action here: the owner watching a car sit
    /// idle at 22:00 with a 07:00 deadline needs to be told that this is the plan working, not the plan
    /// failing.
    /// </summary>
    private ChargingControlDecision Waiting(ChargingControlInput input, TargetedChargePlan plan) =>
        SoftPause(input, $"Waiting for sun; {GridText(plan)}.");

    private static string GridText(TargetedChargePlan plan) => plan.GridStart is { } start
        ? $"grid top-up starts at {start.LocalDateTime:HH:mm} if it is still needed"
        : "no grid import is planned";

    /// <summary>
    /// A pause for a soft reason (a surplus dip, a gap between blocks). While the minimum run time is
    /// unexpired the session is held at the minimum current instead: stopping and restarting a charge
    /// costs a contactor cycle and a vehicle wake, and a few minutes at 6 A is the cheaper trade.
    /// </summary>
    private ChargingControlDecision SoftPause(ChargingControlInput input, string reason)
    {
        if (input.Charging && input.TimeInCurrentState < _options.MinRunTime)
        {
            return new ChargingControlDecision(
                ChargingControlAction.Charge,
                _minAmps,
                $"{reason} Holding at {_minAmps}A for the {_options.MinRunTime.TotalMinutes:F0}min minimum run time.");
        }

        return Pause(reason);
    }

    private static ChargingControlDecision Pause(string reason) => new(ChargingControlAction.Pause, null, reason);

    // Pause rather than None: the charger must be left idle, not armed at the maximum for whatever
    // plugs in next. The orchestrator writes the pause current and then switches the mode to Off.
    private static ChargingControlDecision Complete(string what) =>
        new(ChargingControlAction.Pause, null, $"{what}; pausing and returning to Off.", SessionComplete: true);

    // Whole-amp setpoint the charger accepts: convert (phase-aware), floor to the step, clamp to max.
    private int ToHardwareCurrent(double availableWatts)
    {
        var rawAmps = _power.WattsToAmps(availableWatts);
        var steppedAmps = (int)Math.Floor(rawAmps / _options.CurrentStepAmps) * _options.CurrentStepAmps;
        return Math.Min(steppedAmps, _maxAmps);
    }
}
