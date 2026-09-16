namespace Gleanvolt.Core.Models;

/// <summary>
/// The battery discharge hold's command for one poll: the active-power target, and how much of it
/// reaches past the PV being read.
/// </summary>
/// <param name="ActivePowerWatts">
/// The <c>active_power</c> target, in watts. Always ≤ 0: negative means the inverter pushes power out
/// to the grid-connection point.
/// </param>
/// <param name="HeadroomWatts">
/// How far the push reaches beyond <c>min(house load, PV)</c>, in watts: what a full pack may be asked
/// to cover if the sun really is as weak as the reading. Zero outside the full-pack case.
/// </param>
public readonly record struct BatteryHoldTarget(double ActivePowerWatts, double HeadroomWatts);
