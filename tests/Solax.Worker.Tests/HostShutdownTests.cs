using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Solax.Worker.Tests;

/// <summary>
/// The exit code is the whole feature: `docker-compose.yml` runs the controller under
/// <c>restart: on-failure</c>, so 0 is what keeps a stopped service stopped and non-zero is what
/// brings a rebooted Pi's controller back. Getting it the wrong way round produces either a Stop
/// button that restarts the service a second later, or a Pi that comes up after a power cut with no
/// controller running — neither of which announces itself.
/// </summary>
public class HostShutdownTests
{
    private readonly FakeHostApplicationLifetime _lifetime = new();

    private HostShutdown Shutdown() => new(_lifetime, NullLogger<HostShutdown>.Instance);

    [Fact]
    public void A_run_nobody_asked_to_stop_exits_as_terminated()
    {
        var shutdown = Shutdown();

        Assert.False(shutdown.StopRequested);
        Assert.Equal(HostShutdown.TerminatedExitCode, shutdown.ExitCode);
        Assert.NotEqual(0, shutdown.ExitCode);
    }

    [Fact]
    public void A_requested_stop_exits_zero_so_the_container_stays_down()
    {
        var shutdown = Shutdown();

        shutdown.RequestStop("Web UI");

        Assert.True(shutdown.StopRequested);
        Assert.Equal(0, shutdown.ExitCode);
    }

    [Fact]
    public void Requesting_a_stop_actually_stops_the_host()
    {
        var shutdown = Shutdown();

        shutdown.RequestStop("Home Assistant");

        Assert.Equal(1, _lifetime.StopCalls);
    }

    [Fact]
    public void Asking_twice_is_harmless()
    {
        var shutdown = Shutdown();

        shutdown.RequestStop("Web UI");
        shutdown.RequestStop("Home Assistant");

        Assert.Equal(0, shutdown.ExitCode);
        Assert.Equal(2, _lifetime.StopCalls);
    }

    // The closing line. A run's log ending without one is how an operator tells a deliberate stop from
    // the box dying mid-cycle -- on the Pi the file log is the only evidence that survives a reboot --
    // so what it says, and that it is written at all, is the point rather than a formatting detail.

    [Fact]
    public void Says_who_asked_and_that_it_stays_down()
    {
        var log = new CapturingLogger();
        var shutdown = new HostShutdown(_lifetime, log);
        shutdown.LogWhenStopped();

        shutdown.RequestStop("Web UI");
        _lifetime.NotifyStopped();

        var line = Assert.Single(log.Messages, m => m.Contains("stopped cleanly"));
        Assert.Contains("Web UI", line);
        Assert.Contains("NOT be restarted", line);
        Assert.Contains("code 0", line);
    }

    [Fact]
    public void Says_a_terminated_run_will_come_back()
    {
        var log = new CapturingLogger();
        var shutdown = new HostShutdown(_lifetime, log);
        shutdown.LogWhenStopped();

        // No RequestStop: a SIGTERM from a reboot, a daemon restart, or Ctrl+C.
        _lifetime.NotifyStopped();

        var line = Assert.Single(log.Messages, m => m.Contains("stopped cleanly"));
        Assert.Contains("termination signal", line);
        Assert.Contains($"code {HostShutdown.TerminatedExitCode}", line);
    }

    [Fact]
    public void Writes_nothing_until_the_host_has_actually_stopped()
    {
        var log = new CapturingLogger();
        var shutdown = new HostShutdown(_lifetime, log);
        shutdown.LogWhenStopped();

        shutdown.RequestStop("Home Assistant");

        // The stop was only requested; the hosted services are still shutting down. Claiming it
        // stopped here would put the line in front of the last thing the poll loop logs.
        Assert.DoesNotContain(log.Messages, m => m.Contains("stopped cleanly"));
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopped = new();

        public int StopCalls { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => StopCalls++;

        /// <summary>What the host itself does once every hosted service has stopped.</summary>
        public void NotifyStopped() => _stopped.Cancel();
    }

    private sealed class CapturingLogger : ILogger<HostShutdown>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
