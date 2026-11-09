using System.Net;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Devices.Testing;
using Gleanvolt.Discovery;
using Gleanvolt.Hosting.Setup;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// #302: the wizard's device search. Private networks only and no wider than a /22, one scan at a time
/// for the whole process, and progress that a page can follow.
/// </summary>
public class DeviceDiscoveryServiceTests
{
    private static readonly InterfaceAddress Lan = new("eth0", IPAddress.Parse("192.168.1.5"), 24, true, false);

    private static DeviceDiscoveryService Service(IPortProbe probe) =>
        new(
            NullLogger<DeviceDiscoveryService>.Instance,
            counted => new DeviceScanner(portProbe: counted),
            probe,
            () => [Lan],
            inContainer: false);

    private static async Task<DiscoveryScan> Finished(IDeviceDiscovery discovery)
    {
        for (var wait = 0; wait < 200 && discovery.Current is { Running: true }; wait++)
        {
            await Task.Delay(25);
        }

        return discovery.Current!;
    }

    [Fact]
    public void It_suggests_this_machines_own_subnet()
    {
        Assert.Equal(["192.168.1.0/24"], Service(new ClosedPorts()).Suggest().Subnets);
    }

    [Theory]
    [InlineData("8.8.8.0/24")]       // not private
    [InlineData("10.0.0.0/16")]      // wider than a /22
    [InlineData("8.8.8.8")]          // one public address
    [InlineData("not a network")]
    public void Only_a_private_network_of_bounded_size_is_scanned(string subnet)
    {
        var started = Service(new ClosedPorts()).Start(subnet);

        Assert.False(started.Started);
        Assert.NotNull(started.ProblemPhrase);
    }

    [Fact]
    public async Task A_scan_counts_every_probe_and_ends()
    {
        var discovery = Service(new ClosedPorts());
        var changes = 0;
        discovery.Changed += () => Interlocked.Increment(ref changes);

        Assert.True(discovery.Start("192.168.1.0/30").Started);
        var scan = await Finished(discovery);

        Assert.False(scan.Running);
        Assert.Equal(["192.168.1.0/30"], scan.Networks);
        Assert.Equal(2 * 3, scan.ToProbe);   // two hosts, three ports each
        Assert.Equal(scan.ToProbe, scan.Probed);
        Assert.Empty(scan.Devices);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task One_scan_at_a_time_and_it_can_be_stopped()
    {
        var probe = new HeldPorts();
        var discovery = Service(probe);

        Assert.True(discovery.Start("192.168.1.7").Started);
        var second = discovery.Start("192.168.1.0/24");

        Assert.False(second.Started);
        Assert.Equal("Discovery.AlreadyRunning", second.ProblemPhrase!.Key);

        discovery.Cancel();
        var scan = await Finished(discovery);

        Assert.False(scan.Running);
        Assert.True(scan.Cancelled);
        Assert.True(discovery.Start("192.168.1.7").Started);
        discovery.Cancel();
    }

    [Fact]
    public async Task A_found_solax_charger_brings_the_solax_charger_driver()
    {
        // A SolaX charger is identified by its serial number, which names no driver; the wizard still fills the
        // brand (#353), as it does for a SolaX inverter (#324).
        const string serial = "5030BXXXXXXXXX";
        var dump = new RegisterDump
        {
            Holding = Enumerable.Range(0, 7).ToDictionary(i => (ushort)(0x0600 + i), i => (ushort)((serial[i * 2] << 8) | serial[i * 2 + 1])),
            Input = new Dictionary<ushort, ushort> { [0x1D] = 2 },
        };
        var discovery = new DeviceDiscoveryService(
            NullLogger<DeviceDiscoveryService>.Instance,
            counted => new DeviceScanner(
                new DiscoveryOptions { Ports = [502], WebPorts = [], UnitIds = [1], MinRequestInterval = TimeSpan.Zero },
                counted,
                _ => new ReplayModbusClient(dump)),
            new OpenPort(502),
            () => [Lan],
            inContainer: false);

        Assert.True(discovery.Start("192.168.1.6").Started);
        var device = Assert.Single((await Finished(discovery)).Devices);

        Assert.Equal(PvDeviceRole.Charger, device.Role);
        Assert.Equal("solax", device.Driver);
    }

    private sealed class OpenPort(int open) : IPortProbe
    {
        public Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(port == open);
    }

    private sealed class ClosedPorts : IPortProbe
    {
        public Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    // Answers nothing until the scan is cancelled, so a scan stays running for as long as a test needs.
    private sealed class HeldPorts : IPortProbe
    {
        public async Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return false;
        }
    }
}
