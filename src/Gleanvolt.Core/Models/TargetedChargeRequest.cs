namespace Gleanvolt.Core.Models;

/// <summary>
/// What the owner asked for: an amount of energy and the time it has to be in the car by. The whole
/// input to <see cref="Enums.ChargeControlMode.Targeted"/>.
///
/// <para>Energy rather than a target state of charge, deliberately: the car's cloud-reported SOC is
/// routinely hours stale, and a promise built on it would be a promise built on a guess. The owner may
/// nonetheless <em>ask</em> in state of charge — "80% by seven" is how anyone actually thinks about it
/// — and <see cref="Strategies.VehicleTargetEnergy"/> converts that to kilowatt-hours once, here, at
/// the moment the request is made. What is recorded is the energy. The two SOC figures below are kept
/// only so the request can be described in the terms it was asked in.</para>
/// </summary>
/// <param name="RequiredEnergyWh">The energy to deliver, in watt-hours, measured at the charger.</param>
/// <param name="DepartBy">When it has to be there.</param>
/// <param name="ActivatedAt">
/// When the request was made. Delivery is metered from here, so energy the car took under some earlier
/// mode does not count towards this target.
/// </param>
/// <param name="TargetSocPercent">
/// The state of charge the owner asked for, when they asked in state of charge; null when they asked
/// in kilowatt-hours, which is still the default and the only basis an install without a vehicle feed
/// ever sees.
///
/// <para><b>Not re-derived.</b> Nothing recomputes <paramref name="RequiredEnergyWh"/> from a later
/// reading, and that is the point: a parked car's cloud report arrives when it feels like it, so a SOC
/// that jumps six points at 02:00 because the car finally phoned home would otherwise move a promise
/// that is already half delivered — quietly, overnight, with nobody watching. The conversion happens
/// once and the energy stands.</para>
/// </param>
/// <param name="VehicleSocPercentAtRequest">
/// What the car was reporting when the conversion was made, so the request can be read back as
/// "42% → 80%" rather than only as a number of kilowatt-hours. Null on an energy request.
/// </param>
public sealed record TargetedChargeRequest(
    double RequiredEnergyWh,
    DateTimeOffset DepartBy,
    DateTimeOffset ActivatedAt,
    double? TargetSocPercent = null,
    double? VehicleSocPercentAtRequest = null)
{
    /// <summary>
    /// Whether the owner asked in state of charge. Purely about how the request is <em>described</em>
    /// — every consumer downstream of here reads <see cref="RequiredEnergyWh"/> either way.
    /// </summary>
    public bool IsSocBased => TargetSocPercent is not null;
}
