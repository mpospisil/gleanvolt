using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// The knobs <see cref="Strategies.TargetedChargePlanner"/> plans with — a plain Core record, for the
/// same reason <see cref="SolarDayPlannerOptions"/> is one: the planner stays free of configuration
/// and hosting types, and a test can construct one in a line.
/// </summary>
/// <param name="BatteryCapacityWh">Usable capacity of the home battery, which everything SOC-related scales off.</param>
/// <param name="ChargeEfficiency">
/// PV → battery efficiency (0..1]. Applied so "the battery needs 20% of 11.6 kWh" becomes the larger
/// figure the sun actually has to deliver before any of it reaches the car.
/// </param>
/// <param name="MinChargePowerWatts">
/// The charger's minimum viable power — 6 A through the configured phase count. A slice whose surplus
/// doesn't clear it is worth nothing to the car, however much energy it holds.
/// </param>
/// <param name="MaxChargePowerWatts">
/// The charger's maximum power. This is the whole feasibility question: <c>MaxChargePowerWatts ×
/// (departure − now)</c> is the most that can physically reach the car, whatever the plan does.
/// </param>
/// <param name="MinBatterySocFloorPercent">The hard floor the computed SOC trajectory is clamped to.</param>
/// <param name="SafetyMargin">
/// How far before departure the plan finishes. "Ready at 07:00" must not mean "still charging at
/// 07:00" — the car needs a moment to settle and the owner needs to be able to unplug and go.
/// </param>
/// <param name="ReleaseSlack">
/// How much earlier than strictly necessary a held tail is released under
/// <see cref="TargetedChargePriority.JustInTime"/>.
///
/// <para>The release point is <c>deadline − tail ÷ MaxChargePowerWatts − ReleaseSlack</c>, and this is
/// the only allowance made for the tail running slower than the charger's rated maximum — which it
/// usually will, because a car near full tapers. Nothing here tries to predict by how much: the plan is
/// rebuilt every poll from measured delivery, so a slow tail raises its own pace until it saturates at
/// maximum current, and <see cref="TargetedChargePlan.ShortfallWh"/> reports the gap before departure
/// if even that will not do it. Guessing the taper in advance buys nothing this does not.</para>
/// </param>
/// <param name="Confidence">Which forecast band to plan on. P10 by default, as elsewhere.</param>
public sealed record TargetedChargePlannerOptions(
    double BatteryCapacityWh,
    double ChargeEfficiency,
    double MinChargePowerWatts,
    double MaxChargePowerWatts,
    double MinBatterySocFloorPercent,
    TimeSpan SafetyMargin,
    TimeSpan ReleaseSlack = default,
    ForecastConfidence Confidence = ForecastConfidence.P10);
