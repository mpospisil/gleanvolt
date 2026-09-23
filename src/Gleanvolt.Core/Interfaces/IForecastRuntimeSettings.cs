namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The handful of forecast-mode parameters that are worth changing without a restart, because they
/// track the day rather than the installation: how much the car may take in one session, and how low
/// the home battery may go. No two of them answer the same question — one ceiling and two
/// battery-protection numbers. Seeded from configuration and settable
/// at runtime (e.g. from Home Assistant number entities), exactly like
/// <see cref="IChargeControlModeSelector"/> — and, like it, runtime changes do not persist across
/// restarts.
/// </summary>
public interface IForecastRuntimeSettings
{
    /// <summary>
    /// Ceiling on energy delivered in one charging session, in watt-hours. 0 = unlimited.
    ///
    /// <para>A ceiling, not a request: it resets per plug-in and stands in for "charge to 80%" on a
    /// charger that cannot see the car's own SOC. An owner who wants to <em>ask</em> for an amount
    /// wants <c>Targeted</c>, which takes a departure time and paces a charge to meet it — something
    /// "stop at 10 kWh, whenever that happens" has no way to express.</para>
    /// </summary>
    double SessionEnergyTargetWh { get; }

    /// <summary>The hard SOC floor the computed trajectory is clamped to.</summary>
    double MinBatterySocFloorPercent { get; }

    /// <summary>
    /// How far above that floor the battery must have recovered before a paused session restarts.
    /// Worth a runtime knob for the same reason as the floor itself: it is the one number to reach for
    /// when a marginal day turns out to start and stop the car more than the owner is comfortable with.
    /// </summary>
    double FloorResumeMarginPercent { get; }

    /// <summary>Sets the per-session energy ceiling. <paramref name="source"/> names who changed it, for logging.</summary>
    void SetSessionEnergyTargetWh(double wattHours, string source);

    /// <summary>Sets the hard SOC floor.</summary>
    void SetMinBatterySocFloorPercent(double percent, string source);

    /// <summary>Sets the recovery margin required above the floor before charging restarts.</summary>
    void SetFloorResumeMarginPercent(double percent, string source);
}
