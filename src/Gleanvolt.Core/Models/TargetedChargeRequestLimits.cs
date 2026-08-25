namespace Gleanvolt.Core.Models;

/// <summary>
/// The installation-level figures a <see cref="TargetedChargeRequest"/> cannot be composed without:
/// how far ahead a departure may be set, and what the car's pack holds.
///
/// <para>Passed in rather than read, because <see cref="Strategies.TargetedChargeRequestFactory"/> is
/// pure and every one of these lives in a configuration section <c>Gleanvolt.Core</c> must not be able
/// to see. Each surface hands over the values its host bound for it.</para>
/// </summary>
/// <param name="MaxHorizon">
/// How far ahead a departure may be set. Beyond it neither the forecast nor a request that does not
/// survive a restart can honestly promise anything.
/// </param>
/// <param name="BatteryCapacityKWh">
/// The car's usable capacity, or 0 when it has not been configured. Zero withdraws the SOC-based
/// target entirely: see <see cref="Strategies.VehicleTargetEnergy"/>.
/// </param>
/// <param name="ChargeEfficiency">Charger-meter → cells efficiency, applied to a SOC-based target.</param>
/// <param name="DefaultRestSocPercent">
/// Where a just-in-time hold parks the car when the caller does not say. Means nothing under
/// <see cref="Enums.TargetedChargePriority.Cheapest"/>.
/// </param>
public sealed record TargetedChargeRequestLimits(
    TimeSpan MaxHorizon,
    double BatteryCapacityKWh = 0,
    double ChargeEfficiency = 0.9,
    double DefaultRestSocPercent = 80)
{
    /// <summary>
    /// The pack figures on their own, for the callers that need a capacity and an efficiency but have
    /// no departure to speak of — <see cref="Strategies.FastChargeLimitFactory"/>. One projection
    /// rather than two parallel records, so the two factories cannot disagree about what a capacity
    /// means.
    /// </summary>
    public VehiclePackLimits Pack => new(BatteryCapacityKWh, ChargeEfficiency);

    /// <summary>Whether a target may be asked for as a state of charge — i.e. whether a capacity is known.</summary>
    public bool CanTargetSoc => Pack.CanTargetSoc;

    /// <summary>The pack's usable capacity in watt-hours, which is what the conversions are stated in.</summary>
    public double BatteryCapacityWh => Pack.BatteryCapacityWh;
}
