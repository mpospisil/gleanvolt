namespace Gleanvolt.Core.Enums;

/// <summary>
/// How the rest of today looks for the car, judged once the forecast is known. Reported to Home
/// Assistant and logged, so a shortfall is announced in the morning rather than discovered at dusk
/// with a paused charger and no explanation.
/// </summary>
public enum DayOutlook
{
    /// <summary>No usable forecast yet (or it has gone stale), so no judgement can be made.</summary>
    Unknown,

    /// <summary>The forecast covers the house, the battery to 100%, and the whole EV target.</summary>
    Surplus,

    /// <summary>The car gets something, but less than its daily target.</summary>
    Tight,

    /// <summary>The car gets substantially less than its target; the battery keeps priority.</summary>
    Shortfall,

    /// <summary>
    /// There is no window today in which the car could charge at all — either the sun never clears the
    /// charger's minimum power, or the battery needs everything the day produces.
    /// </summary>
    NoChargeToday,
}
