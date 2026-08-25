namespace Gleanvolt.Core.Models;

/// <summary>
/// How a limited fast charge is going: the limit that was set, and what the charger has measurably
/// delivered against it since. The fast mode's whole equivalent of a
/// <see cref="TargetedChargePlan"/> — and a great deal smaller, because there is nothing to plan. No
/// window, no forecast, no split between sun and grid: the current is pinned at the maximum and the
/// only open question is whether the number has been reached yet.
///
/// <para>Null, everywhere it appears, means <see cref="Enums.FastChargeBasis.Full"/> — no limit was
/// asked for, and the car decides when it has had enough.</para>
/// </summary>
/// <param name="Limit">What was asked for, and what it was asked in.</param>
/// <param name="DeliveredWh">
/// Energy the charger has delivered since <see cref="FastChargeLimit.ActivatedAt"/>, in watt-hours.
/// Metered from activation rather than from the plug-in, so energy the car took under some earlier
/// mode does not count towards this limit.
/// </param>
public sealed record FastChargeProgress(FastChargeLimit Limit, double DeliveredWh)
{
    /// <summary>What is still to come, floored at zero — the last poll usually overshoots slightly.</summary>
    public double RemainingWh => Math.Max(0, Limit.RequiredEnergyWh - DeliveredWh);

    /// <summary>Whether the limit has been reached, which is the signal for the mode to end itself.</summary>
    public bool IsMet => Limit.IsMet(DeliveredWh);

    /// <summary>How much of the limit is delivered, 0..1. Zero for a limit of zero rather than NaN.</summary>
    public double Fraction => Limit.RequiredEnergyWh > 0
        ? Math.Clamp(DeliveredWh / Limit.RequiredEnergyWh, 0, 1)
        : 0;
}
