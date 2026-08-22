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

    /// <summary>
    /// The state of charge <paramref name="energyWh"/> at the charger would leave the car at — the
    /// inverse of <see cref="RequiredWh"/>, and the only thing needed to make a
    /// <see cref="Enums.TargetedChargePriority.JustInTime"/> hold work for an owner who asked in
    /// kilowatt-hours rather than in percent.
    ///
    /// <para>Null on the same terms as <see cref="RequiredWh"/>: no reported SOC, or no configured
    /// capacity, means there is no honest answer and the caller must do without one.</para>
    /// </summary>
    public static double? ResultingSocPercent(
        double? currentSocPercent,
        double energyWh,
        double usableCapacityWh,
        double chargeEfficiency)
    {
        if (currentSocPercent is not { } current || double.IsNaN(current) || usableCapacityWh <= 0)
        {
            return null;
        }

        if (double.IsNaN(energyWh) || energyWh < 0)
        {
            return null;
        }

        var gained = energyWh * Math.Clamp(chargeEfficiency, 0.5, 1.0) / usableCapacityWh * 100;

        return Math.Clamp(Math.Clamp(current, 0, 100) + gained, 0, 100);
    }

    /// <summary>
    /// The <b>held tail</b>: how much of a request sits above a rest state of charge, and so is the part
    /// a <see cref="Enums.TargetedChargePriority.JustInTime"/> plan schedules to land at the deadline
    /// rather than whenever the sun offers it.
    ///
    /// <para>The rest point is measured from wherever the car actually is: a car already past it has no
    /// tail to hold — everything left is above the rest point, and holding all of it would defer the
    /// entire charge, which is not what this priority means. The result is watt-hours <b>at the
    /// charger</b>, consistent with <see cref="RequiredWh"/> and with how the request is metered, so the
    /// planner never has to convert anything back.</para>
    ///
    /// <para>Computed once, at activation, and never re-derived — the same rule the SOC target itself
    /// follows, and for the same reason.</para>
    /// </summary>
    /// <param name="currentSocPercent">What the car last reported, 0-100, or null if it reports no SOC.</param>
    /// <param name="targetSocPercent">The state of charge this request will finish at, 0-100.</param>
    /// <param name="restSocPercent">Where the car should wait before the last stretch is released, 0-100.</param>
    /// <param name="usableCapacityWh">The drive battery's usable capacity. Zero or less means unconfigured.</param>
    /// <param name="chargeEfficiency">Charger-meter → cells efficiency, (0..1], clamped as above.</param>
    /// <returns>
    /// The tail in watt-hours at the charger; <c>0</c> when the target does not reach past the rest
    /// point, so there is nothing to hold; <c>null</c> when the split cannot honestly be made at all.
    /// </returns>
    public static double? TailAboveRestWh(
        double? currentSocPercent,
        double targetSocPercent,
        double restSocPercent,
        double usableCapacityWh,
        double chargeEfficiency)
    {
        if (currentSocPercent is not { } current || double.IsNaN(current) || usableCapacityWh <= 0)
        {
            return null;
        }

        if (double.IsNaN(targetSocPercent) || double.IsNaN(restSocPercent))
        {
            return null;
        }

        // From the rest point or from the car, whichever is higher. A car sitting at 90% under an 80%
        // rest point is already past it: the tail is the whole 90 → 100, not a phantom 80 → 100.
        var from = Math.Max(Math.Clamp(restSocPercent, 0, 100), Math.Clamp(current, 0, 100));
        var gapPercent = Math.Clamp(targetSocPercent, 0, 100) - from;

        return gapPercent <= 0
            ? 0
            : gapPercent / 100 * usableCapacityWh / Math.Clamp(chargeEfficiency, 0.5, 1.0);
    }
}
