namespace Gleanvolt.Core.Interfaces;

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
/// <para>Implemented in <c>Gleanvolt.Worker</c>, which owns the host; a surface only asks.</para>
/// </summary>
public interface IServiceShutdown
{
    /// <summary>
    /// Begins a graceful shutdown of the service. Returns immediately — the host stops on its own
    /// thread, so the caller can still finish rendering a page or acknowledging an MQTT command.
    /// <paramref name="source"/> names who asked, for the log. Calling twice is harmless.
    /// </summary>
    void RequestStop(string source);

    /// <summary>
    /// Begins the same graceful shutdown as <see cref="RequestStop"/>, then starts the service again so
    /// that it reads the configuration afresh; that is how a saved change is applied (issue #204).
    /// Where a supervisor restarts on failure -- <c>restart: on-failure</c> in Docker,
    /// <c>Restart=on-failure</c> under systemd -- the process exits with the code that means "bring me
    /// back" and the supervisor starts a new one. Where nothing supervises it (<c>dotnet run</c>, the
    /// debugger, the Windows zip) a fresh host is built in the same process. Returns immediately, like
    /// <see cref="RequestStop"/>.
    /// </summary>
    void RequestRestart(string source);

    /// <summary>
    /// What, if anything, runs this process as a service: what a restart is handed to, and what the
    /// person who pressed Stop has to type to start it again.
    /// </summary>
    ServiceSupervisor Supervisor { get; }
}

/// <summary>What runs Gleanvolt as a service, as far as stopping and starting it is concerned.</summary>
public enum ServiceSupervisor
{
    /// <summary>Nothing: <c>dotnet run</c>, the debugger, the Windows zip, winget. A restart happens in
    /// the process; after a stop, it is started again the way it was started.</summary>
    None,

    /// <summary>The image's container under Docker's <c>restart: on-failure</c>.</summary>
    Container,

    /// <summary>The .deb's systemd unit under <c>Restart=on-failure</c>.</summary>
    Systemd,
}
