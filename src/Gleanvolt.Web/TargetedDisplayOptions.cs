namespace Gleanvolt.Web;

/// <summary>
/// The one piece of the targeted mode's configuration the UI needs in order to reject a request
/// before it is made. Handed over by the host exactly as <see cref="VehicleDisplayOptions"/> is:
/// Gleanvolt.Web cannot see the hosting assembly's options classes, and should not have to.
/// </summary>
/// <param name="MaxHorizon">How far ahead a departure may be set.</param>
/// <param name="DefaultRestSocPercent">
/// Where a just-in-time hold parks the car by default, before the last stretch is released. Only ever
/// a starting value for the field — the owner moves it per request, and it means nothing under
/// <see cref="Core.Enums.TargetedChargePriority.Cheapest"/>.
/// </param>
public sealed record TargetedDisplayOptions(TimeSpan MaxHorizon, double DefaultRestSocPercent = 80);
