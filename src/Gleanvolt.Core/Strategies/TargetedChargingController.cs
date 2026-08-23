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
/// the charger's own use-mode is <see cref="EvChargerMode.Fast"/> — written once when the target was
/// activated, and never re-asserted from here. There is still no battery loan in this mode: the home
/// battery keeps its priority. What the mode <em>will</em> do is bridge from the <b>grid</b> — see
/// <see cref="GridBridgeWatts"/> — which is the same trade the plan already made, taken live.</para>
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

        // Precondition, unchanged from every other mode: the charger is in Fast (activating the target
        // wrote it) and we only modulate the current under it.
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

        // Deliberately idle, and the one place in this mode where a real surplus is turned down. Under
        // JustInTime everything below the rest point has been delivered and only the held tail is left,
        // so charging now -- on sun or on anything else -- would put the car at its target hours early,
        // which is the single thing the priority exists to prevent. Checked before the pace, because the
        // pace is zero here and the sun would otherwise walk straight through DecideFromPace.
        if (plan.IsHoldingAt(now))
        {
            return SoftPause(
                input,
                $"Holding the last {plan.TailEnergyWh / 1000:F1}kWh until "
                + $"{plan.HoldUntil?.LocalDateTime:HH:mm} so the car reaches its target just before "
                + $"{plan.DepartBy.LocalDateTime:HH:mm} rather than sitting full overnight. "
                + "Not waiting for sun -- this is the plan working.");
        }

        // Everything else is one rule: hold the pace, and take the sun whenever it beats it. There are
        // no blocks to be inside or outside of any more -- the charge runs across the whole window, and
        // its rate is the only thing that moves.
        return DecideFromPace(input, plan);
    }

    /// <summary>
    /// The one decision the mode makes while it is running: <b>charge at <c>max(live surplus, pace)</c></b>,
    /// clamped into the charger's range and recomputed every poll.
    ///
    /// <para>The pace is the promise — <see cref="TargetedChargePlan.RequiredPaceWatts"/>, what must be
    /// averaged from here to the deadline — and the sun raises it whenever it can. Taking sun above the
    /// pace is free and buys the rest of the window a slower one; falling behind raises it again on the
    /// next poll. Nothing here is remembered, so the loop self-corrects either way.</para>
    ///
    /// <para>Why not simply run flat out and finish early? Because the charger can only use the sun that
    /// shines <em>while it runs</em>. At 16 A the car outruns the roof by several kW whatever the
    /// weather, and then stops — which on 2026-08-22 turned a 13 kWh target on a day peaking at 8.5 kW
    /// into 9 kWh of grid in 87 minutes. Pacing the same energy across the window puts the draw near the
    /// roof's own output, where the sun can actually cover it.</para>
    ///
    /// <para>The home battery keeps its priority: below the plan's SOC floor the sun is not the car's to
    /// take, so only the pace runs and the grid funds it, with the discharge hold armed.</para>
    /// </summary>
    private ChargingControlDecision DecideFromPace(ChargingControlInput input, TargetedChargePlan plan)
    {
        var soc = input.State.BatterySocPercent;
        var surplusWatts = Math.Max(0, input.SurplusWatts);
        var paceWatts = Math.Max(0, plan.RequiredPaceWatts);

        // Above the floor the surplus is the car's to take; below it the pack has priority and the car
        // gets only what the deadline actually demands.
        // Sum, not max: the pace is what the sun is *not* forecast to cover, so the two are additive.
        // Taking the greater would let a bright hour swallow the grid's share and finish short.
        var aboveFloor = soc >= plan.SocFloorPercent;
        var wantWatts = aboveFloor ? surplusWatts + paceWatts : paceWatts;

        if (wantWatts <= 0)
        {
            return Waiting(input, plan, surplusWatts, paceWatts, aboveFloor, soc);
        }

        // Restart dwell still applies -- a contactor cycle and a vehicle wake are not free -- but only
        // when we are choosing to start, never when the deadline is the thing asking.
        var targetAmps = ToHardwareCurrent(Math.Min(wantWatts, _power.AmpsToWatts(_maxAmps)));
        if (targetAmps < _minAmps)
        {
            // Wanted less than the charger's floor, so there is nothing to start and the restart dwell
            // has no bearing on it.
            //
            // A non-zero pace is, by definition, energy the forecast says the sun will *not* cover, so
            // there is nothing to be gained by deferring it -- and everything to lose, because deferral
            // ends with the whole remainder crammed into the last minutes of the window where a dropout
            // has no room to recover. Run at the floor instead. This self-regulates: delivering ahead
            // of the pace drives it to zero, at which point the car goes back to living on surplus.
            if (paceWatts > 0)
            {
                var bridged = Math.Max(0, MinChargePowerWatts - (aboveFloor ? surplusWatts : 0));

                return new ChargingControlDecision(
                    ChargingControlAction.Charge,
                    _minAmps,
                    $"A {paceWatts:F0}W pace is under the {MinChargePowerWatts:F0}W the charger can run at, and it is "
                    + $"energy the sun is not forecast to cover -> hold the {_minAmps}A floor rather than defer "
                    + $"{plan.RemainingEnergyWh / 1000:F1}kWh to the last minutes before {plan.Deadline.LocalDateTime:HH:mm}.",
                    GridBridgeWatts: bridged);
            }

            return Bridge(input, plan) ?? Waiting(input, plan, surplusWatts, paceWatts, aboveFloor, soc);
        }

        // The dwell spares the contactor when *we* are choosing to start. It must not defer a pace the
        // charger could hold outright -- that is the deadline asking, not us -- and it must not run at
        // all before the first charge: see HasChargedThisTarget.
        if (HasChargedThisTarget(plan)
            && !input.Charging
            && input.TimeInCurrentState < _options.MinPauseTime
            && paceWatts < MinChargePowerWatts)
        {
            return Pause($"Paused {input.TimeInCurrentState.TotalMinutes:F0}min of the {_options.MinPauseTime.TotalMinutes:F0}min minimum before restarting.");
        }

        var commandedWatts = _power.AmpsToWatts(targetAmps);

        // Below the floor the surplus is the pack's, not the car's, so none of it counts against the
        // import -- and this figure is what arms the discharge hold. Netting it off the raw surplus
        // would leave the hold released while the car ran on a pace the grid was supposed to fund,
        // which is the pack quietly paying for it: the one thing this mode promises never to do.
        var usableSurplusWatts = aboveFloor ? surplusWatts : 0;
        var fromGridWatts = Math.Max(0, commandedWatts - usableSurplusWatts);

        var how = !aboveFloor
            ? $"battery {soc:F0}% is under the plan's {plan.SocFloorPercent:F0}% floor, so the pack keeps the sun and the grid funds the pace"
            : paceWatts <= 0
                ? $"surplus {surplusWatts:F0}W and nothing owed to the grid, so the sun sets the rate"
                : $"surplus {surplusWatts:F0}W plus a {paceWatts:F0}W pace, so the grid covers {fromGridWatts:F0}W of it";

        return new ChargingControlDecision(
            ChargingControlAction.Charge,
            targetAmps,
            $"{plan.RemainingEnergyWh / 1000:F1}kWh still needed by {plan.Deadline.LocalDateTime:HH:mm} -> "
            + $"charge at {targetAmps}A: {how}.",
            GridBridgeWatts: fromGridWatts);
    }

    /// <summary>
    /// The <b>grid bridge</b>: run at the charger's floor and let the grid make up what the sun is short
    /// of, rather than export a real surplus and buy the same kilowatt-hours back after dark.
    ///
    /// <para>On three phases the floor is ~4.14 kW, so a perfectly good 3.5 kW surplus charges nothing
    /// at all. The forecast-driven mode bridges that gap from the home battery and repays it from sun
    /// that would otherwise have been exported. This mode will not touch the pack — but it does not
    /// need to, because it is <em>already</em> committed to buying this energy: the only question a
    /// <see cref="TargetedChargeStrategy.SolarPlusGrid"/> plan leaves open is when. Bridging now buys
    /// <c>floor − surplus</c>; refusing buys the whole floor later, and exports the surplus meanwhile.
    /// It is strictly the cheaper of the two, and it shrinks the planned grid block watt for watt.</para>
    ///
    /// <para>Three things it will not do. It will not fire on a plan the sun covers outright
    /// (<see cref="TargetedChargeStrategy.Solar"/>) — there is no committed import to bring forward,
    /// and buying now would spend money on energy the day was about to hand over free. It will not fire
    /// below the plan's SOC floor, because the pack's priority is not suspended for this any more than
    /// it is for a solar block. And it will not fire on a surplus too small to be worth it: below
    /// <see cref="TargetedChargingOptions.MinBridgeSurplusWatts"/> the sun is not really contributing,
    /// and this would just be an unplanned grid block in the wrong place.</para>
    ///
    /// <para>Sized to reach exactly the floor and never past it — a bridge, not a booster. The host
    /// arms the battery discharge hold on the returned <see cref="ChargingControlDecision.GridBridgeWatts"/>,
    /// without which the pack, not the grid, would quietly pay for the gap.</para>
    /// </summary>
    /// <returns>A charge decision at the floor, or null when no bridge is warranted.</returns>
    private ChargingControlDecision? Bridge(ChargingControlInput input, TargetedChargePlan plan)
    {
        if (!_options.GridBridge || !plan.ImportsFromGrid)
        {
            return null;
        }

        if (input.State.BatterySocPercent < plan.SocFloorPercent)
        {
            return null;
        }

        // The same restart dwell a solar block waits out: a bridge is still a contactor cycle and a
        // vehicle wake.
        if (HasChargedThisTarget(plan) && !input.Charging && input.TimeInCurrentState < _options.MinPauseTime)
        {
            return null;
        }

        var surplusWatts = input.SurplusWatts;
        if (surplusWatts < _options.MinBridgeSurplusWatts)
        {
            return null;
        }

        var gapWatts = MinChargePowerWatts - surplusWatts;
        if (gapWatts <= 0)
        {
            // The sun already clears the floor; there is nothing to bridge, and the caller's own
            // hysteresis is the thing holding the session back.
            return null;
        }

        return new ChargingControlDecision(
            ChargingControlAction.Charge,
            _minAmps,
            $"Grid bridge: surplus {surplusWatts:F0}W is under the {MinChargePowerWatts:F0}W floor, so +{gapWatts:F0}W "
            + $"from the grid takes it there rather than exporting it -> charge at {_minAmps}A. "
            + $"Comes straight off the {plan.GridEnergyWh / 1000:F1}kWh already planned for import.",
            GridBridgeWatts: gapWatts);
    }

    /// <summary>
    /// Not enough sun, and nothing worth bridging. The sentence matters more than the action here: the
    /// owner watching a car sit idle at 22:00 with a 07:00 deadline needs to be told that this is the
    /// plan working, not the plan failing.
    /// </summary>
    private ChargingControlDecision Waiting(
        ChargingControlInput input,
        TargetedChargePlan plan,
        double surplusWatts,
        double paceWatts,
        bool aboveFloor,
        double soc)
    {
        // Two different reasons to be idle, and the owner needs to be able to tell them apart: the sun
        // has not turned up, or it has and the pack is keeping it.
        var why = aboveFloor
            ? $"Surplus {surplusWatts:F0}W and a {paceWatts:F0}W pace are both under the {MinChargePowerWatts:F0}W the charger needs."
            : $"Battery {soc:F0}% is under the plan's {plan.SocFloorPercent:F0}% floor, so the pack keeps the sun, and the "
                + $"{paceWatts:F0}W pace alone is under the {MinChargePowerWatts:F0}W the charger needs.";

        return SoftPause(
            input,
            $"{why} Waiting for sun; there is still time for the pace to carry "
            + $"{plan.RemainingEnergyWh / 1000:F1}kWh by {plan.Deadline.LocalDateTime:HH:mm}.");
    }

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

    /// <summary>
    /// Whether anything has actually been delivered against this target yet.
    ///
    /// <para>Gates the restart dwell, which exists to stop the charger flapping when the surplus wobbles
    /// around the threshold — a <em>restart</em> timer. Before the first watt there is no charge to
    /// restart, and applying it there had a real cost: activating a target set the coordinator's
    /// state-changed clock, the controller immediately answered "pause", and the dwell then counted its
    /// full 15 minutes from the moment the owner pressed <b>Activate</b>. Observed live on 2026-08-23
    /// with 5 kW of surplus going to waste and the log reading "Paused 10min of the 15min minimum
    /// before restarting" — a quarter of an hour of free sun lost to a timer guarding nothing.</para>
    ///
    /// <para>Metered from activation rather than from the plug-in, so re-activating a target starts a
    /// fresh count and gets a prompt start too.</para>
    /// </summary>
    private static bool HasChargedThisTarget(TargetedChargePlan plan) => plan.DeliveredEnergyWh > 0;

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
