using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Fast;

/// <summary>
/// Meters what a limited fast charge has actually delivered, and reports how it is going. The
/// <see cref="Targeting.TargetedChargeProvider"/> pattern with everything the fast mode does not need
/// taken out — no forecast, no house-load profile, no preview — because the amount is a stopping
/// condition rather than a plan and there is little here to compute beyond a running total.
///
/// <para>Since #122 it also holds the deferred charge's schedule, which is the same arrangement one step
/// on: <see cref="FastChargePlanner"/> stays pure, and this is where its arithmetic meets the clock,
/// the configuration and the log.</para>
///
/// <para>Singleton: it accumulates delivery against one limit. Power is integrated <b>here and nowhere
/// else</b> — the coordinator's own session meter answers a different question (energy since the car
/// was plugged in, across every mode) and cannot stand in for this one.</para>
/// </summary>
public sealed class FastChargeProvider
{
    /// <summary>
    /// Below this draw the car counts as not charging, so the reading says nothing about what it is
    /// capable of taking. Well above a charger's standby reading and well below its 6 A floor.
    /// </summary>
    private const double MeaningfulDrawWatts = 200;

    private readonly IFastChargeSelector _selector;
    private readonly ChargePowerConverter _power;
    private readonly int _maxChargingCurrentAmps;
    private readonly TimeSpan _safetyMargin;
    private readonly ILogger<FastChargeProvider> _logger;
    private readonly TimeProvider _timeProvider;

    // Delivery is metered from activation, not from the plug-in: energy the car took under some earlier
    // mode -- or under an earlier fast charge -- is not part of this one.
    private readonly EnergyIntegrator _delivered = new();
    private DateTimeOffset? _activatedAt;

    // What the car was last seen to actually take, held across the pauses in between. Sticky on
    // purpose: the plan is recomputed every poll, and while the charge is being held back the car draws
    // nothing -- so a value read fresh each cycle would fall back to the installation's maximum and
    // forget, every single poll, that this car only does 7.4kW. Forgetting that is how a deferred
    // charge starts an hour too late.
    private double? _observedPowerWatts;

    // Whether the "limit met" line has been logged for the current limit, so a mode that takes a poll
    // or two to wind down says it once rather than on every cycle.
    private bool _reportedMet;

    // The last plan logged, so a schedule that has not moved is not restated on every poll.
    private string? _lastPlanSignature;

    /// <param name="safetyMargin">
    /// How long before the departure a deferred charge must be finished. Deliberately the targeted
    /// mode's figure (<c>ChargeControl:Targeted:SafetyMargin</c>) rather than one of this mode's own:
    /// "ready at 07:00 must not mean still charging at 07:00" is a fact about the owner and their
    /// morning, not about which strategy happens to be running, and two settings for it would only ever
    /// drift apart. The same reasoning the dwell timers are shared under.
    /// </param>
    public FastChargeProvider(
        IFastChargeSelector selector,
        ChargePowerConverter power,
        IOptions<ChargeControlOptions> chargeControlOptions,
        IOptions<TargetedChargeOptions> targetedOptions,
        ILogger<FastChargeProvider> logger,
        TimeProvider? timeProvider = null)
    {
        _selector = selector;
        _power = power;
        _maxChargingCurrentAmps = chargeControlOptions.Value.MaxChargingCurrentAmps;
        _safetyMargin = targetedOptions.Value.SafetyMargin;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// What the installation can deliver to the car, in watts — the plan's fallback and ceiling.
    ///
    /// <para>From the <b>clamped</b> current, the same clamp <see cref="FastChargingController"/>
    /// applies, so the plan is computed at the power that will actually be commanded rather than at a
    /// configured figure the hardware would never accept.</para>
    /// </summary>
    public double MaxChargePowerWatts => _power.AmpsToWatts(
        Math.Clamp(_maxChargingCurrentAmps, EvChargerLimits.MinCurrentAmps, EvChargerLimits.MaxCurrentAmps));

    /// <summary>Energy the charger has delivered since the active limit was set, in watt-hours.</summary>
    public double DeliveredWh => _delivered.EnergyWattHours;

    /// <summary>
    /// Folds one telemetry reading into the limit's progress and returns it, or null when no limit is
    /// set — which is the ordinary <see cref="Core.Enums.FastChargeBasis.Full"/> case and not a failure.
    /// </summary>
    public FastChargeProgress? Update(EnergyState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var limit = _selector.Limit;

        // A new limit starts a new count. Keyed on the activation instant rather than on reference
        // equality, so re-activating the same figures still resets -- which is what pressing the button
        // again means.
        if (limit?.ActivatedAt != _activatedAt)
        {
            _activatedAt = limit?.ActivatedAt;
            _delivered.Reset();
            _observedPowerWatts = null;
            _reportedMet = false;
            _lastPlanSignature = null;
        }

        if (limit is null)
        {
            return null;
        }

        _delivered.Add(state.Timestamp, Math.Max(0, state.EvChargerPowerWatts));

        // Only while the car is actually drawing. A reading taken through a pause -- which is most of a
        // deferred charge -- describes our own setpoint, not the car's capability.
        if (state.EvChargerPowerWatts > MeaningfulDrawWatts)
        {
            _observedPowerWatts = state.EvChargerPowerWatts;
        }

        var plan = FastChargePlanner.Plan(
            limit,
            _delivered.EnergyWattHours,
            _observedPowerWatts,
            MaxChargePowerWatts,
            _safetyMargin,
            state.Timestamp);

        var progress = new FastChargeProgress(limit, _delivered.EnergyWattHours, plan);

        LogPlan(plan);

        if (progress.IsMet && !_reportedMet)
        {
            _reportedMet = true;
            _logger.LogInformation(
                "Fast charge limit met: {DeliveredKWh:F1}kWh of the {RequiredKWh:F1}kWh asked for{AsSoc}. "
                + "The mode will pause the charger and return to Off.",
                progress.DeliveredWh / 1000,
                limit.RequiredEnergyWh / 1000,
                limit.IsSocBased ? $" ({limit.TargetSocPercent:F0}%)" : string.Empty);
        }

        return progress;
    }

    /// <summary>
    /// Says what the schedule is, and then stops saying it. The plan is rebuilt on every poll and moves
    /// continuously as energy is delivered, so the interesting moments are the ones where something
    /// changes shape — the start time to the minute, whether it still fits, and whether the power it is
    /// computed from is the car's or a guess.
    /// </summary>
    private void LogPlan(FastChargePlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        var signature =
            $"{plan.StartNoLaterThan:yyyy-MM-ddTHH:mm}|{plan.IsFeasible}|{plan.PowerObserved}";

        if (signature == _lastPlanSignature)
        {
            return;
        }

        _lastPlanSignature = signature;

        _logger.LogInformation(
            "Fast charge plan: start by {StartAt}, ready by {ReadyBy}, leaving {DepartBy}. "
            + "{RemainingKWh:F1}kWh at {PowerKW:F1}kW ({PowerSource}) takes {Hours:F1}h.{Shortfall}",
            plan.StartNoLaterThan.LocalDateTime.ToString("HH:mm"),
            plan.ReadyBy.LocalDateTime.ToString("HH:mm"),
            plan.DepartBy.LocalDateTime.ToString("HH:mm"),
            plan.RemainingEnergyWh / 1000,
            plan.ChargePowerWatts / 1000,
            plan.PowerObserved ? "measured at the car" : "the installation's maximum, not yet measured",
            plan.Duration.TotalHours,
            plan.IsFeasible
                ? string.Empty
                : $" Not enough time: about {plan.ShortfallWh / 1000:F1}kWh will not fit.");
    }
}
