using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Reads the charger's settings, writes its charging-current setpoint, and — only when an action asks
/// for it — its use-mode.
///
/// <para>The two writes belong to different callers on purpose. The <b>control loop</b> writes the
/// current setpoint and nothing else, exactly as it always has. The <b>use-mode</b> is written once
/// per action: <see cref="EvChargerMode.Fast"/> when a strategy is started, <see cref="EvChargerMode.Stop"/>
/// when it is switched off. Nothing re-asserts it afterwards, so a charger changed at the wallbox
/// mid-session leaves the controllers idle rather than being fought for.</para>
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

    /// <summary>
    /// Writes the charger's use-mode. Unconditional — unlike <see cref="SetCurrentAsync"/> there is no
    /// hysteresis and no read-back comparison, because this is only ever called from an action the
    /// owner just took, and "stop charging" has to stop charging whatever the register already said.
    /// Throws if the charger does not accept the write; the caller reports that rather than pretending
    /// the mode was entered.
    /// </summary>
    Task SetModeAsync(EvChargerMode mode, string reason, CancellationToken cancellationToken = default);
}
