namespace Gleanvolt.Core.Models;

/// <summary>
/// What the owner asked for: an amount of energy and the time it has to be in the car by. The whole
/// input to <see cref="Enums.ChargeControlMode.Targeted"/>.
///
/// <para>Energy rather than a target state of charge, deliberately: the car's cloud-reported SOC is
/// routinely hours stale, and a promise built on it would be a promise built on a guess. Prefilling
/// the kilowatt-hours from the vehicle feed is a convenience that can come later; the planner's
/// contract does not depend on it.</para>
/// </summary>
/// <param name="RequiredEnergyWh">The energy to deliver, in watt-hours, measured at the charger.</param>
/// <param name="DepartBy">When it has to be there.</param>
/// <param name="ActivatedAt">
/// When the request was made. Delivery is metered from here, so energy the car took under some earlier
/// mode does not count towards this target.
/// </param>
public sealed record TargetedChargeRequest(
    double RequiredEnergyWh,
    DateTimeOffset DepartBy,
    DateTimeOffset ActivatedAt);
