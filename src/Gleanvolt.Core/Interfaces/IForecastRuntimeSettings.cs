namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The handful of forecast-mode parameters that are worth changing without a restart, because they
/// track the day rather than the installation: how low the home battery may go, and how far it must
/// recover before the car comes back. Both protect the battery; the mode takes no amount for the car
/// (issue #249 — that is the car's own charge limit, or Targeted). Seeded from configuration and settable
/// at runtime (e.g. from Home Assistant number entities), exactly like
/// <see cref="IChargeControlModeSelector"/> — and, like it, runtime changes do not persist across
/// restarts.
/// </summary>
public interface IForecastRuntimeSettings
{
    /// <summary>The hard SOC floor the computed trajectory is clamped to.</summary>
    double MinBatterySocFloorPercent { get; }

    /// <summary>
    /// How far above that floor the battery must have recovered before a paused session restarts.
    /// Worth a runtime knob for the same reason as the floor itself: it is the one number to reach for
    /// when a marginal day turns out to start and stop the car more than the owner is comfortable with.
    /// </summary>
    double FloorResumeMarginPercent { get; }

    /// <summary>Sets the hard SOC floor. <paramref name="source"/> names who changed it, for logging.</summary>
    void SetMinBatterySocFloorPercent(double percent, string source);

    /// <summary>Sets the recovery margin required above the floor before charging restarts.</summary>
    void SetFloorResumeMarginPercent(double percent, string source);
}
