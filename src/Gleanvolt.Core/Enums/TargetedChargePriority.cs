namespace Gleanvolt.Core.Enums;

/// <summary>
/// What the owner wants optimised while a <see cref="Models.TargetedChargeRequest"/> is delivered.
/// The deadline is not one of the choices: both priorities keep the promise, and only the
/// <em>shape</em> of the delivery differs.
/// </summary>
public enum TargetedChargePriority
{
    /// <summary>
    /// Use as little grid as possible. The charge is paced across the whole window and every watt of
    /// sun above that pace is taken for free, so the target is met whenever the sun and the pace
    /// happen to land it — usually well before the deadline.
    ///
    /// <para>The default, and the behaviour of every request made before this choice existed.</para>
    /// </summary>
    Cheapest,

    /// <summary>
    /// Reach the target shortly before departure rather than hours before it, so the drive battery
    /// does not sit at a high state of charge overnight.
    ///
    /// <para>Only the <b>last stretch</b> is held back — the energy above
    /// <see cref="Models.TargetedChargeRequest.TailEnergyWh"/>, which is worked out once at activation
    /// from a rest state of charge. Everything below that is delivered exactly as
    /// <see cref="Cheapest"/> would deliver it, on the sun, whenever the sun is there. Delaying the
    /// whole charge would forfeit a sunny day to protect the top of the pack and then buy the lot at
    /// 04:00.</para>
    ///
    /// <para>It is allowed to cost money, and the tail is genuinely refused the sun while it waits —
    /// taking a bright afternoon would land the car at 100% by teatime, which is the one thing this
    /// choice exists to prevent. The preview names that cost in kilowatt-hours before it is chosen.</para>
    /// </summary>
    JustInTime,
}
