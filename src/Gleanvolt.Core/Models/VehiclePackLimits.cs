namespace Gleanvolt.Core.Models;

/// <summary>
/// What the car's drive battery holds, and how much of what the charger delivers reaches it. The two
/// figures any "charge it to 60%" has to be converted through, and the whole of what
/// <see cref="Strategies.VehicleTargetEnergy"/> needs beyond the readings themselves.
///
/// <para>Passed in rather than read, because the factories that use it are pure and both figures live
/// in a configuration section <c>Gleanvolt.Core</c> must not be able to see. Each surface hands over
/// the values its host bound for it.</para>
///
/// <para>Separate from <see cref="TargetedChargeRequestLimits"/>, which carries these two plus a
/// departure horizon and a rest point. A fast charge has no deadline and no held tail, so asking it to
/// supply a <c>MaxHorizon</c> would mean inventing one at every call site purely to be ignored.
/// <see cref="TargetedChargeRequestLimits.Pack"/> projects one onto the other, so the two factories
/// cannot drift apart on what a capacity means.</para>
/// </summary>
/// <param name="BatteryCapacityKWh">
/// The car's usable capacity, or 0 when it has not been configured. Zero withdraws the SOC basis
/// entirely rather than guessing at a pack size.
/// </param>
/// <param name="ChargeEfficiency">
/// Charger-meter → cells efficiency. Some of what the charger delivers is spent on the on-board
/// rectifier and the cells rather than reaching the state of charge; ask for the difference in the
/// pack and the car arrives short.
/// </param>
public sealed record VehiclePackLimits(double BatteryCapacityKWh = 0, double ChargeEfficiency = 0.9)
{
    /// <summary>Whether a target may be asked for as a state of charge — i.e. whether a capacity is known.</summary>
    public bool CanTargetSoc => BatteryCapacityKWh > 0;

    /// <summary>The pack's usable capacity in watt-hours, which is what the conversions are stated in.</summary>
    public double BatteryCapacityWh => BatteryCapacityKWh * 1000;
}
