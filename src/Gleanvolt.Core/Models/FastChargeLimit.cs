namespace Gleanvolt.Core.Models;

/// <summary>
/// How much energy a fast charge should deliver before it stops itself. The whole of the amount added
/// to <see cref="Enums.ChargeControlMode.FastNoBattery"/> — a <b>stopping condition</b>, and
/// deliberately no more than that: nothing plans around it, nothing forecasts against it, and the
/// current stays pinned at the installation's maximum until it is met.
///
/// <para><b>Not a <see cref="TargetedChargeRequest"/> with the departure left out.</b> Everything that
/// makes that type what it is — a deadline, a priority, a held tail and the rest point it is measured
/// from — exists to spend as little grid as possible over a window. A fast charge has no window. Sharing
/// the type would mean four properties defended as null forever, and a planner call this mode never
/// wants to make.</para>
///
/// <para>Energy rather than a target state of charge, on exactly the reasoning written at length on
/// <see cref="TargetedChargeRequest.TargetSocPercent"/>: the car's cloud-reported SOC is routinely
/// hours stale. The owner may nonetheless <em>ask</em> in state of charge, and
/// <see cref="Strategies.VehicleTargetEnergy"/> converts that to kilowatt-hours once, at the moment the
/// limit is set. What is recorded is the energy; the two SOC figures below are kept only so the limit
/// can be described in the terms it was asked in.</para>
///
/// <para><b>Null is not a value of this type.</b> "Charge until the car says stop" —
/// <see cref="Enums.FastChargeBasis.Full"/> — is the absence of a limit, so it is a null
/// <see cref="FastChargeLimit"/> rather than a sentinel energy inside one.</para>
/// </summary>
/// <param name="RequiredEnergyWh">The energy to deliver, in watt-hours, measured at the charger.</param>
/// <param name="ActivatedAt">
/// When the limit was set. Delivery is metered from here, so energy the car took under some earlier
/// mode — or under an earlier fast charge — does not count towards this one.
/// </param>
/// <param name="TargetSocPercent">
/// The state of charge the owner asked for, when they asked in state of charge; null when they asked in
/// kilowatt-hours, which is the default and the only basis an install without a vehicle feed ever sees.
///
/// <para><b>Not re-derived.</b> Nothing recomputes <paramref name="RequiredEnergyWh"/> from a later
/// reading. A parked car's cloud report arrives when it feels like it, and a SOC that jumps six points
/// because the car finally phoned home would otherwise move a limit that is already half delivered.</para>
/// </param>
/// <param name="VehicleSocPercentAtRequest">
/// What the car was reporting when the conversion was made, so the limit can be read back as
/// "42% → 60%" rather than only as a number of kilowatt-hours. Null on an energy request.
/// </param>
public sealed record FastChargeLimit(
    double RequiredEnergyWh,
    DateTimeOffset ActivatedAt,
    double? TargetSocPercent = null,
    double? VehicleSocPercentAtRequest = null)
{
    /// <summary>
    /// Whether the owner asked in state of charge. Purely about how the limit is <em>described</em> —
    /// everything downstream reads <see cref="RequiredEnergyWh"/> either way.
    /// </summary>
    public bool IsSocBased => TargetSocPercent is not null;

    /// <summary>Whether <paramref name="deliveredWh"/> has met this limit.</summary>
    public bool IsMet(double deliveredWh) => deliveredWh >= RequiredEnergyWh;
}
