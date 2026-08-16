using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Reads the charger's settings and writes its charging-current setpoint. This is the only thing the
/// controller ever changes — it never touches the use-mode or issues start/stop commands.
/// </summary>
public interface IEvChargerControl
{
    /// <summary>Reads the charger's currently active settings (use-mode and current setpoint).</summary>
    Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the charging-current setpoint, but only if it differs from <paramref name="activeAmps"/>
    /// (the freshly-read active value) by at least the configured change threshold — so the charger
    /// isn't re-commanded on every small fluctuation. The change is logged. A value of 0 (or the
    /// configured pause current) pauses; charging values are within the hardware range.
    /// </summary>
    Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default);
}
