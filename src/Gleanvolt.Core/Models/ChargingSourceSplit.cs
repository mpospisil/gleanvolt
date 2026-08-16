namespace Gleanvolt.Core.Models;

/// <summary>
/// How the power the EV charger is drawing right now divides between the three places it can come
/// from. The three components sum to the charger's measured draw by construction — see
/// <see cref="Strategies.ChargingSourceAttribution"/> for how the split is derived and why.
/// </summary>
/// <param name="SolarWatts">Covered by PV that the rest of the house wasn't using.</param>
/// <param name="GridWatts">Imported from the grid.</param>
/// <param name="BatteryWatts">Discharged out of the home battery.</param>
public readonly record struct ChargingSourceSplit(double SolarWatts, double GridWatts, double BatteryWatts)
{
    /// <summary>The charger's total draw, i.e. the three components added back together.</summary>
    public double TotalWatts => SolarWatts + GridWatts + BatteryWatts;
}
