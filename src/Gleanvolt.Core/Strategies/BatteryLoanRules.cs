namespace Gleanvolt.Core.Strategies;

/// <summary>
/// The two numbers the battery loan turns on, in one place because the plan and the controller both
/// need them and used to disagree about them.
///
/// <para>The disagreement was not theoretical (issue #223): <see cref="SolarDayPlanner"/> counted a
/// period feasible from <c>MinChargePowerWatts − MaxLoanPowerWatts</c> (1.64 kW on the reference
/// site) while <see cref="ForecastedChargingController"/> refused to lend below
/// <c>MinBridgeSurplusWatts</c> (2 kW). Every slice between the two was drawn as chargeable on the
/// forecast page and silently was not. One helper, called by both, is what makes a window the plan
/// promises a window the controller will actually enter.</para>
///
/// <para>The <b>spill</b> is what changes the arithmetic. A loan is a round trip — a cycle on both
/// packs and the losses at each end — and paying it to move energy the house battery would otherwise
/// have <em>kept</em> buys nothing. Paying it to catch energy the pack has no room for buys the whole
/// of that energy, which would otherwise be exported or curtailed. So the "is this surplus real?"
/// floor applies to the first case and a much lower one to the second.</para>
/// </summary>
public static class BatteryLoanRules
{
    /// <summary>
    /// The least live surplus that may be topped up at all.
    /// </summary>
    /// <param name="spilling">
    /// Whether the day's remaining surplus outruns what the pack can absorb —
    /// <see cref="Models.SolarDayPlan.SpillWh"/> above zero. On a full pack that is the whole of the
    /// remaining surplus, and every watt of it leaves the house unless the car takes it.
    /// </param>
    /// <param name="minBridgeSurplusWatts">The floor when the pack could still keep the energy itself.</param>
    /// <param name="spillBridgeSurplusWatts">
    /// The floor when it could not. Low, but not zero: cycling the pack to chase a few watts of noise
    /// is wear bought for nothing either way.
    /// </param>
    public static double BridgeSurplusFloorWatts(
        bool spilling,
        double minBridgeSurplusWatts,
        double spillBridgeSurplusWatts) =>
        Math.Max(0, spilling ? spillBridgeSurplusWatts : minBridgeSurplusWatts);

    /// <summary>
    /// The least <em>solar</em> surplus at which the car can charge, loan included — the line between
    /// a period the car can use and one only the battery can. Without a loan it is the charger's own
    /// floor; with one it is that floor less the biggest bridge allowed, but never below the surplus
    /// floor a loan is granted from, because a bridge that would not be granted cannot make a period
    /// chargeable.
    /// </summary>
    public static double MinSolarPowerWatts(
        double minChargePowerWatts,
        bool enableBatteryLoan,
        double maxLoanPowerWatts,
        double bridgeSurplusFloorWatts) =>
        enableBatteryLoan
            ? Math.Max(bridgeSurplusFloorWatts, minChargePowerWatts - Math.Max(0, maxLoanPowerWatts))
            : minChargePowerWatts;
}
