namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The handful of forecast-mode parameters that are worth changing without a restart, because they
/// track the day rather than the installation: how much the car should get today, how much it may
/// take in one session, and how low the home battery may go. Seeded from configuration and settable
/// at runtime (e.g. from Home Assistant number entities), exactly like
/// <see cref="IChargeControlModeSelector"/> — and, like it, runtime changes do not persist across
/// restarts.
/// </summary>
public interface IForecastRuntimeSettings
{
    /// <summary>What the owner would like the car to receive today, in watt-hours.</summary>
    double DailyEvTargetWh { get; }

    /// <summary>Ceiling on energy delivered in one charging session, in watt-hours. 0 = unlimited.</summary>
    double SessionEnergyTargetWh { get; }

    /// <summary>The hard SOC floor the computed trajectory is clamped to.</summary>
    double MinBatterySocFloorPercent { get; }

    /// <summary>Sets the daily EV target. <paramref name="source"/> names who changed it, for logging.</summary>
    void SetDailyEvTargetWh(double wattHours, string source);

    /// <summary>Sets the per-session energy ceiling.</summary>
    void SetSessionEnergyTargetWh(double wattHours, string source);

    /// <summary>Sets the hard SOC floor.</summary>
    void SetMinBatterySocFloorPercent(double percent, string source);
}
