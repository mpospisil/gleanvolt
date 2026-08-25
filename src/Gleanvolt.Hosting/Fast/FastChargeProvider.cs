using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Hosting.Fast;

/// <summary>
/// Meters what a limited fast charge has actually delivered, and reports how it is going. The
/// <see cref="Targeting.TargetedChargeProvider"/> pattern with everything the fast mode does not need
/// taken out — no forecast, no house-load profile, no planner, no preview — because the amount is a
/// stopping condition rather than a plan and there is nothing here to compute beyond a running total.
///
/// <para>Singleton: it accumulates delivery against one limit. Power is integrated <b>here and nowhere
/// else</b> — the coordinator's own session meter answers a different question (energy since the car
/// was plugged in, across every mode) and cannot stand in for this one.</para>
/// </summary>
public sealed class FastChargeProvider
{
    private readonly IFastChargeSelector _selector;
    private readonly ILogger<FastChargeProvider> _logger;

    // Delivery is metered from activation, not from the plug-in: energy the car took under some earlier
    // mode -- or under an earlier fast charge -- is not part of this one.
    private readonly EnergyIntegrator _delivered = new();
    private DateTimeOffset? _activatedAt;

    // Whether the "limit met" line has been logged for the current limit, so a mode that takes a poll
    // or two to wind down says it once rather than on every cycle.
    private bool _reportedMet;

    public FastChargeProvider(IFastChargeSelector selector, ILogger<FastChargeProvider> logger)
    {
        _selector = selector;
        _logger = logger;
    }

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
            _reportedMet = false;
        }

        if (limit is null)
        {
            return null;
        }

        _delivered.Add(state.Timestamp, Math.Max(0, state.EvChargerPowerWatts));

        var progress = new FastChargeProgress(limit, _delivered.EnergyWattHours);

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
}
