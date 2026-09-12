namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The one number the <see cref="Enums.ChargeControlMode.SolarGrid"/> mode is steered by: how much
/// surplus the sun has to be providing before the grid may help the car. Seeded from configuration and
/// settable at runtime (the web UI and a Home Assistant number), exactly like
/// <see cref="IForecastRuntimeSettings"/> — and, like it, a runtime change does not persist across
/// restarts.
/// </summary>
public interface ISolarGridSettings
{
    /// <summary>
    /// The smoothed surplus, in watts, below which the car is paused rather than bridged from the grid —
    /// and the line the rest of today's forecast has to clear for the mode to keep waiting.
    /// </summary>
    double MinSurplusWatts { get; }

    /// <summary>Sets the minimum. <paramref name="source"/> names who changed it, for logging.</summary>
    void SetMinSurplusWatts(double watts, string source);
}
