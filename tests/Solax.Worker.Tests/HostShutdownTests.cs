using Microsoft.Extensions.Hosting;
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

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        public int StopCalls { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopCalls++;
    }
}
