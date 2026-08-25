namespace Gleanvolt.Core.Enums;

/// <summary>
/// What a fast charge is being asked for — the three-way question the targeted form already asks,
/// minus the deadline. It says <b>when to stop</b>, and nothing else: no plan is built from it, no
/// forecast is consulted, and the current stays pinned at the installation's maximum throughout.
/// </summary>
public enum FastChargeBasis
{
    /// <summary>
    /// No limit. The car decides when it has had enough and stops drawing, and the mode ends itself on
    /// that — which is what <see cref="ChargeControlMode.FastNoBattery"/> did before it could be asked
    /// for an amount, and what it still does for anyone who does not ask.
    /// </summary>
    Full,

    /// <summary>An amount of energy, at the charger. "20 kWh, now."</summary>
    Energy,

    /// <summary>
    /// A state of charge, converted to energy once at activation. "To 60%, now." Offered only where it
    /// can be honoured — see <see cref="Models.VehiclePackLimits.CanTargetSoc"/>.
    /// </summary>
    Soc,
}
