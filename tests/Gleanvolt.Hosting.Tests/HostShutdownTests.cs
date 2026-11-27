using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Hosting.Tests;

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

        _lifetime.WaitForStopCalls(1);
    }

    [Fact]
    public void Asking_twice_is_harmless()
    {
        var shutdown = Shutdown();

        shutdown.RequestStop("Web UI");
        shutdown.RequestStop("Home Assistant");

        Assert.Equal(0, shutdown.ExitCode);
        _lifetime.WaitForStopCalls(2);
    }

    // Restart (#204): the same graceful stop, with a code of its own that is non-zero -- so the restart
    // policy brings it back -- and is neither 0 nor the SIGTERM code, so `docker inspect` can tell it apart.

    /// <summary>
    /// <see cref="IHostApplicationLifetime.StopApplication"/> runs every ApplicationStopping callback on
    /// the thread that calls it, and SignalR's waits up to 5 s for its connections to close. A restart
    /// is pressed from a Blazor circuit -- one of those connections -- so called inline it waited on
    /// itself, and every restart from the web UI sat out the 5 s. The request has to return at once.
    /// </summary>
    [Fact]
    public void A_request_returns_without_waiting_for_the_hosts_stopping_callbacks()
    {
        using var release = new ManualResetEventSlim();
        _lifetime.OnStop = () => release.Wait(TimeSpan.FromSeconds(10));
        var shutdown = Shutdown();

        var asked = System.Diagnostics.Stopwatch.StartNew();
        shutdown.RequestRestart("Web UI");
        asked.Stop();

        try
        {
            Assert.True(asked.Elapsed < TimeSpan.FromSeconds(2), $"RequestRestart took {asked.Elapsed}.");
            _lifetime.WaitForStopCalls(1);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void A_requested_restart_exits_with_its_own_non_zero_code()
    {
        var shutdown = Shutdown();

        shutdown.RequestRestart("Web UI");

        Assert.True(shutdown.RestartRequested);
        Assert.Equal(HostShutdown.RestartExitCode, shutdown.ExitCode);
        Assert.NotEqual(0, shutdown.ExitCode);
        Assert.NotEqual(HostShutdown.TerminatedExitCode, shutdown.ExitCode);
        _lifetime.WaitForStopCalls(1);
    }

    [Fact]
    public void A_stop_is_not_a_restart()
    {
        var shutdown = Shutdown();

        shutdown.RequestStop("Web UI");

        Assert.False(shutdown.RestartRequested);
        Assert.Equal(0, shutdown.ExitCode);
    }

    [Fact]
    public void Says_who_asked_for_the_restart_and_that_it_comes_back()
    {
        var log = new CapturingLogger();
        var shutdown = new HostShutdown(_lifetime, log, supervised: true);
        shutdown.LogWhenStopped();

        shutdown.RequestRestart("Web UI");
        _lifetime.NotifyStopped();

        var line = Assert.Single(log.Messages, m => m.Contains("stopped cleanly"));
        Assert.Contains("restart requested by Web UI", line);
        Assert.Contains($"code {HostShutdown.RestartExitCode}", line);
    }

    [Fact]
    public void Where_nothing_supervises_it_a_restart_happens_in_the_process()
    {
        var log = new CapturingLogger();
        var shutdown = new HostShutdown(_lifetime, log, supervised: false);
        shutdown.LogWhenStopped();

        shutdown.RequestRestart("Web UI");
        _lifetime.NotifyStopped();

        Assert.True(shutdown.RestartsInProcess);
        Assert.True(shutdown.RestartRequested);
        var line = Assert.Single(log.Messages, m => m.Contains("stopped cleanly"));
        Assert.Contains("restart requested by Web UI", line);
        Assert.Contains("same process", line);
    }

    [Fact]
    public void A_stop_says_how_to_start_it_again_where_it_runs()
    {
        var log = new CapturingLogger();
        new HostShutdown(_lifetime, log, supervised: false).RequestStop("Web UI");

        var line = Assert.Single(log.Messages, m => m.Contains("Stop requested"));
        Assert.Contains("the same way it was started", line);
        Assert.DoesNotContain("docker", line);
    }

    [Fact]
    public void In_the_container_a_stop_names_the_compose_command()
    {
        var log = new CapturingLogger();
        new HostShutdown(_lifetime, log, supervised: true).RequestStop("Web UI");

        var line = Assert.Single(log.Messages, m => m.Contains("Stop requested"));
        Assert.Contains("docker compose start gleanvolt-controller", line);
    }

    [Fact]
    public void Under_a_supervisor_a_restart_leaves_it_to_the_supervisor()
    {
        var shutdown = new HostShutdown(_lifetime, new CapturingLogger(), supervised: true);

        Assert.False(shutdown.RestartsInProcess);
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
        private int _stopCalls;

        public int StopCalls => Volatile.Read(ref _stopCalls);

        /// <summary>Runs inside StopApplication, the way the host's stopping callbacks do.</summary>
        public Action? OnStop { get; set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            Interlocked.Increment(ref _stopCalls);
            OnStop?.Invoke();
        }

        /// <summary>The host is stopped off the caller's thread, so a test waits for it to have been.</summary>
        public void WaitForStopCalls(int expected)
        {
            Assert.True(
                SpinWait.SpinUntil(() => StopCalls >= expected, TimeSpan.FromSeconds(5)),
                $"StopApplication was called {StopCalls} time(s), expected {expected}.");
            Assert.Equal(expected, StopCalls);
        }

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
