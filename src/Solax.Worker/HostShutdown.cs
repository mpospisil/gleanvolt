using Solax.Core.Interfaces;

namespace Solax.Worker;

/// <summary>
/// Turns an operator's "stop the service" into the host's own graceful shutdown, and records that it
/// was asked for so <c>Program.cs</c> can exit 0 rather than the code that means "I was terminated".
///
/// <para><b>The exit code is a contract with the deploy stack.</b> `docker-compose.yml` runs the
/// controller under <c>restart: on-failure</c>, so the exit code alone decides whether the container
/// comes back:</para>
/// <list type="bullet">
/// <item><description><b>0</b> — somebody pressed Stop. The container stays down until it is started
/// again by hand.</description></item>
/// <item><description><b><see cref="TerminatedExitCode"/></b> — SIGTERM: a Pi reboot, a Docker daemon
/// restart, `docker compose restart`. The controller must come back on its own, and does.</description></item>
/// </list>
/// <para>Without the distinction the two are indistinguishable — .NET exits 0 for a SIGTERM too — and
/// a rebooted Pi would sit there with no controller running and nothing saying so.</para>
/// </summary>
public sealed class HostShutdown : IServiceShutdown
{
    /// <summary>
    /// What the process returns when it was terminated rather than asked to stop: the conventional
    /// 128 + SIGTERM(15). Non-zero is the part the restart policy reads; the particular number is
    /// chosen to be the one an operator already recognises in `docker inspect`.
    /// </summary>
    public const int TerminatedExitCode = 143;

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<HostShutdown> _logger;

    // Written from a UI circuit or the MQTT client's callback, read on the main thread once Run()
    // returns. Only ever set to true, but it crosses threads, so it is not a plain bool.
    private volatile bool _stopRequested;

    // Who asked, kept for the closing log line. Same threading story as the flag above.
    private volatile string? _stopSource;

    public HostShutdown(IHostApplicationLifetime lifetime, ILogger<HostShutdown> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Whether this run is ending because somebody asked it to. Read after the host has stopped, to
    /// choose the exit code.
    /// </summary>
    public bool StopRequested => _stopRequested;

    /// <summary>The exit code this run should return: 0 for a requested stop, otherwise "terminated".</summary>
    public int ExitCode => _stopRequested ? 0 : TerminatedExitCode;

    /// <summary>
    /// Arms the line that says the process stopped on purpose. Call once, before the host starts.
    ///
    /// <para><b>Why it needs saying at all.</b> Nothing else marks the end of a run: a graceful stop
    /// simply leaves the log ending after the last poll, which is indistinguishable from the process
    /// dying mid-cycle. On the Pi that distinction is not academic — the journal is RAM-only, so this
    /// file is the only evidence that survives a reboot, and the box does stop abruptly on its own.
    /// A run whose log ends <i>without</i> this line ended badly; that is the whole point of it.</para>
    ///
    /// <para>Hooked to <c>ApplicationStopped</c> rather than written after <c>Run()</c> returns,
    /// because by then the service provider — and with it Serilog and its file sink — has been
    /// disposed, and the line would be swallowed silently.</para>
    /// </summary>
    public void LogWhenStopped() => _lifetime.ApplicationStopped.Register(LogStopped);

    private void LogStopped()
    {
        if (_stopRequested)
        {
            _logger.LogInformation(
                "SolaX Local Controller stopped cleanly at the request of {Source}. Exiting with code "
                + "{ExitCode}: it will NOT be restarted, and stays down until it is started again.",
                _stopSource,
                ExitCode);

            return;
        }

        // A reboot, `docker compose restart|stop`, a Docker daemon shutdown, or Ctrl+C in a terminal.
        // The shutdown itself was still graceful -- the charger was released and the session closed --
        // and the exit code is what gets it started again where a restart policy is watching.
        _logger.LogInformation(
            "SolaX Local Controller stopped cleanly after a termination signal. Exiting with code "
            + "{ExitCode}: where a restart policy is watching, it will be started again.",
            ExitCode);
    }

    public void RequestStop(string source)
    {
        // Warning rather than Information: on a headless Pi the log file is the only place that can
        // answer "why is the controller not running?", and this is the answer.
        _logger.LogWarning(
            "Stop requested by {Source}. Shutting down gracefully — the charger will be paused and the "
            + "open session closed. The service will NOT restart by itself; start it again with "
            + "'docker compose start solax-controller' on the Pi.",
            source);

        _stopSource = source;
        _stopRequested = true;

        // Non-blocking by design: it signals ApplicationStopping and returns, so the caller's own
        // work (rendering the confirmation, acknowledging the MQTT command) completes normally.
        // Idempotent in the host — a second call while stopping does nothing.
        _lifetime.StopApplication();
    }
}
