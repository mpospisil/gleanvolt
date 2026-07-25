using Solax.Core.Models;

namespace Solax.Core.Interfaces;

/// <summary>
/// Reads and writes the EV charger's controllable settings. Writes are idempotent: only registers
/// whose target differs from the currently active value are written, and every actual change is
/// logged. This is where the "write only on change" and "log every hardware change" rules live.
/// </summary>
public interface IEvChargerControl
{
    /// <summary>Reads the charger's currently active settings (use-mode and current setpoint).</summary>
    Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="target"/> to the charger, but only the fields that differ from
    /// <paramref name="current"/> (the freshly-read active settings). Each change is logged with its
    /// old → new value and <paramref name="reason"/>. Returns the settings now active on the device.
    /// </summary>
    Task<EvChargerSettings> ApplyAsync(
        EvChargerSettings current,
        EvChargerSettings target,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a one-shot control command (e.g. <see cref="Enums.EvChargerControlCommand.StartCharging"/>)
    /// to the charger. Commands are actions rather than stored settings, so they are always written
    /// (there is nothing to compare against) — send them only on a real transition.
    /// </summary>
    Task SendCommandAsync(
        Enums.EvChargerControlCommand command,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses charging: sets use-mode <see cref="Enums.EvChargerMode.Stop"/> and the minimum 6 A
    /// current setpoint, written only if they differ from what's active.
    ///
    /// Deliberately does NOT send <see cref="Enums.EvChargerControlCommand.StopCharging"/> — that
    /// would terminate the session, which on many cars needs a re-plug to restart. Suspending via the
    /// use-mode keeps the session alive so charging can resume as soon as surplus returns, while still
    /// halting the draw (so the charger never makes up a shortfall from the grid).
    /// </summary>
    Task PauseAsync(string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops charging and <em>terminates</em> the session: sets use-mode
    /// <see cref="Enums.EvChargerMode.Stop"/> at the 6 A minimum <em>and</em> issues
    /// <see cref="Enums.EvChargerControlCommand.StopCharging"/>. Unlike <see cref="PauseAsync"/> this
    /// ends the session (which on many cars needs a re-plug to restart) — used when charge control is
    /// switched Off.
    /// </summary>
    Task StopAsync(string reason, CancellationToken cancellationToken = default);
}
