namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Turns "charge it to 80%" into the kilowatt-hours the charger has to deliver to get there.
///
/// <para>The whole of the SOC-based target, and deliberately no more than arithmetic:
/// <c>(target − now) / 100 × usable capacity ÷ charge efficiency</c>. The division is the part worth
/// stating out loud — the target is measured <b>at the charger</b>, where
/// <see cref="Models.TargetedChargeRequest"/> is metered, and some of what the charger delivers is
/// spent on the on-board rectifier and the cells rather than reaching the state of charge. Ask for
/// the difference in the pack and the car arrives short.</para>
///
/// <para>Pure, and used exactly once per request — at activation. It deliberately does not re-derive
/// itself from later readings: see <see cref="Models.TargetedChargeRequest.TargetSocPercent"/> for
/// why a cloud SOC that arrives at 02:00 must not move a promise that is already half delivered.</para>
/// </summary>
public static class VehicleTargetEnergy
{
    /// <summary>
    /// The energy to ask the charger for, or <c>null</c> when the conversion cannot honestly be made:
    /// no SOC has been reported, or the pack's usable capacity has not been configured.
    ///
    /// <para>Null and zero are different answers. Null is "don't offer this basis at all"; zero is
    /// "the car is already there", which is a perfectly good reading and the caller's to refuse.</para>
    /// </summary>
    /// <param name="currentSocPercent">What the car last reported, 0-100, or null if it reports no SOC.</param>
    /// <param name="targetSocPercent">The state of charge asked for, 0-100.</param>
    /// <param name="usableCapacityWh">The drive battery's usable capacity. Zero or less means unconfigured.</param>
    /// <param name="chargeEfficiency">
    /// Charger-meter → cells efficiency, (0..1]. Clamped to a sane band rather than trusted: a
    /// mistyped <c>0.09</c> would otherwise ask for ten times the energy the car can even hold.
    /// </param>
    public static double? RequiredWh(
        double? currentSocPercent,
        double targetSocPercent,
        double usableCapacityWh,
        double chargeEfficiency)
    {
        if (currentSocPercent is not { } current || double.IsNaN(current) || usableCapacityWh <= 0)
        {
            return null;
        }

        if (double.IsNaN(targetSocPercent))
        {
            return null;
        }

        var gapPercent = Math.Clamp(targetSocPercent, 0, 100) - Math.Clamp(current, 0, 100);

        return gapPercent <= 0
            ? 0
            : gapPercent / 100 * usableCapacityWh / Math.Clamp(chargeEfficiency, 0.5, 1.0);
    }
}
