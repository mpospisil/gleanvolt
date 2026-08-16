namespace Solax.Core.Interfaces;

/// <summary>
/// A deliberate stop of the whole service, asked for by an operator from one of the control surfaces
/// (the web UI or Home Assistant) rather than by the platform.
///
/// <para><b>Why this exists rather than killing the process.</b> A stop that runs through the host's
/// shutdown drops the charger's setpoint to the pause current, closes the open charging session as
/// <c>ServiceStopped</c> instead of leaving it to be recovered as interrupted on next start, flushes
/// the session store and disposes the Modbus and MQTT clients. Killing the process skips all of it,
/// and the part that matters is the first: the car keeps drawing at whatever current we last wrote,
/// because nothing revokes it.</para>
///
/// <para><b>It stays stopped.</b> A requested stop is what makes the process exit 0, and the deploy
/// stack's <c>restart: on-failure</c> policy therefore leaves the container down until somebody
/// starts it again (see deploy/README.md). Every other way the process can end — SIGTERM from a host
/// reboot, a crash, a power cut — exits non-zero and is brought back automatically. The exit code is
/// the whole distinction, so it must be set by whoever ends the process, not guessed afterwards.</para>
///
/// <para>Implemented in <c>Solax.Worker</c>, which owns the host; a surface only asks.</para>
/// </summary>
public interface IServiceShutdown
{
    /// <summary>
    /// Begins a graceful shutdown of the service. Returns immediately — the host stops on its own
    /// thread, so the caller can still finish rendering a page or acknowledging an MQTT command.
    /// <paramref name="source"/> names who asked, for the log. Calling twice is harmless.
    /// </summary>
    void RequestStop(string source);
}
