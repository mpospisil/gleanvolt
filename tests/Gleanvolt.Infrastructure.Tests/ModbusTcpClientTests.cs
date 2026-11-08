using System.Diagnostics;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Modbus;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// Exercises <see cref="ModbusTcpClient"/> against a real loopback socket (see
/// <see cref="FakeModbusTcpServer"/>). These are the regression tests for issue #24: a single bad
/// exchange used to poison the stream permanently, because the TCP connection stayed healthy and
/// nothing ever tore it down.
/// </summary>
public class ModbusTcpClientTests
{
    private static DeviceConfig Device(int port, TimeSpan? minRequestInterval = null) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        UnitId = 1,
        MinRequestInterval = minRequestInterval ?? TimeSpan.Zero,
    };

    [Fact]
    public async Task ReadsRegisters()
    {
        await using var server = new FakeModbusTcpServer { RegisterValue = 1234 };
        await using var client = new ModbusTcpClient(Device(server.Port));

        var values = await client.ReadInputRegistersAsync(0x0A, 2);

        Assert.Equal([1234, 1234], values);
    }

    [Fact]
    public async Task ConnectsOnDemandWithoutAnExplicitConnectCall()
    {
        await using var server = new FakeModbusTcpServer();
        await using var client = new ModbusTcpClient(Device(server.Port));

        await client.ReadHoldingRegistersAsync(0x60D, 1);

        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task ATransactionIdMismatchDropsTheConnection()
    {
        await using var server = new FakeModbusTcpServer();
        await using var client = new ModbusTcpClient(Device(server.Port));

        await client.ReadInputRegistersAsync(0x1D, 1);
        Assert.True(client.IsConnected);

        // Persistent, as in the field: once a stale reply is in the buffer every response is offset,
        // so NModbus's own retries cannot dig out of it.
        server.CorruptNextResponses(FakeModbusTcpServer.Persistent);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReadInputRegistersAsync(0x1D, 1));

        // The socket itself was fine -- it is the stream that was poisoned, so the connection has to
        // go regardless of what a liveness check would say.
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task TheNextCallAfterAFailureSucceedsOnAFreshConnection()
    {
        await using var server = new FakeModbusTcpServer { RegisterValue = 7 };
        await using var client = new ModbusTcpClient(Device(server.Port));

        await client.ReadInputRegistersAsync(0x1D, 1);
        server.CorruptNextResponses(FakeModbusTcpServer.Persistent);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReadInputRegistersAsync(0x1D, 1));

        server.StopCorrupting();
        var recovered = await client.ReadInputRegistersAsync(0x1D, 1);

        Assert.Equal([7], recovered);
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task RepeatedFailuresDoNotBecomeAPermanentOutage()
    {
        // The field symptom: fifteen minutes of normal operation, then every poll failing for
        // three quarters of an hour. Each failure must cost one request, not the whole session.
        await using var server = new FakeModbusTcpServer();
        await using var client = new ModbusTcpClient(Device(server.Port));

        for (var i = 0; i < 3; i++)
        {
            server.CorruptNextResponses(FakeModbusTcpServer.Persistent);
            await Assert.ThrowsAnyAsync<Exception>(() => client.ReadInputRegistersAsync(0x1D, 1));

            server.StopCorrupting();
            var recovered = await client.ReadInputRegistersAsync(0x1D, 1);
            Assert.Single(recovered);
        }
    }

    [Fact]
    public async Task WritesRecoverTheSameWay()
    {
        await using var server = new FakeModbusTcpServer();
        await using var client = new ModbusTcpClient(Device(server.Port));

        server.CorruptNextResponses(FakeModbusTcpServer.Persistent);
        await Assert.ThrowsAnyAsync<Exception>(() => client.WriteSingleRegisterAsync(0x628, 1600));

        server.StopCorrupting();
        await client.WriteSingleRegisterAsync(0x628, 1600);
        await client.WriteMultipleRegistersAsync(0x7C, [1, 1, 0]);

        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task ConcurrentCallersDoNotShareAStream()
    {
        // Two requests in flight on one connection interleave their transaction ids -- another route
        // to the same desync. The client serialises instead of assuming callers behave.
        await using var server = new FakeModbusTcpServer();
        await using var client = new ModbusTcpClient(Device(server.Port));

        var reads = Enumerable.Range(0, 8).Select(_ => Task.Run(() => client.ReadInputRegistersAsync(0x1D, 1)));
        var results = await Task.WhenAll(reads);

        Assert.All(results, r => Assert.Single(r));
        Assert.Equal(1, server.Connections);
        Assert.Equal(8, server.RequestsHandled);
    }

    [Fact]
    public async Task ConsecutiveRequestsAreSpacedByTheConfiguredInterval()
    {
        await using var server = new FakeModbusTcpServer();
        await using var client = new ModbusTcpClient(Device(server.Port, TimeSpan.FromMilliseconds(200)));

        await client.ReadInputRegistersAsync(0x1D, 1);

        var stopwatch = Stopwatch.StartNew();
        await client.ReadInputRegistersAsync(0x1D, 1);
        stopwatch.Stop();

        // Generous lower bound: the point is that a gap was enforced at all, not its precision.
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 150,
            $"expected the second request to wait out the interval, waited {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task AnUnreachableDeviceFailsRatherThanHanging()
    {
        await using var server = new FakeModbusTcpServer();
        var port = server.Port;
        await server.DisposeAsync();

        await using var client = new ModbusTcpClient(Device(port));

        await Assert.ThrowsAnyAsync<Exception>(() => client.ReadInputRegistersAsync(0x1D, 1));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task WithABackoffAFailedConnectIsNotRetriedUntilTheBackoffHasPassedAndThenItDoubles()
    {
        // Huawei's SDongle (#320): a connect every poll keeps a struggling gateway from recovering.
        var clock = new ManualClock();
        var device = new DeviceConfig
        {
            Host = "127.0.0.1",
            Port = ClosedPort(),
            MinRequestInterval = TimeSpan.Zero,
            ReconnectBackoff = TimeSpan.FromSeconds(5),
        };
        await using var client = new ModbusTcpClient(device, clock);

        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => client.ReadHoldingRegistersAsync(0, 1));

        clock.Now += TimeSpan.FromSeconds(4);
        var waiting = await Assert.ThrowsAsync<IOException>(() => client.ReadHoldingRegistersAsync(0, 1));
        Assert.Contains("Not reconnecting", waiting.Message);

        clock.Now += TimeSpan.FromSeconds(1);
        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => client.ReadHoldingRegistersAsync(0, 1));

        // The second failure doubles the wait.
        clock.Now += TimeSpan.FromSeconds(9);
        await Assert.ThrowsAsync<IOException>(() => client.ReadHoldingRegistersAsync(0, 1));
        clock.Now += TimeSpan.FromSeconds(1);
        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => client.ReadHoldingRegistersAsync(0, 1));
    }

    [Fact]
    public async Task WithABackoffASuccessfulConnectStartsItOver()
    {
        var clock = new ManualClock();
        await using var server = new FakeModbusTcpServer();
        var device = new DeviceConfig
        {
            Host = "127.0.0.1",
            Port = server.Port,
            MinRequestInterval = TimeSpan.Zero,
            ReconnectBackoff = TimeSpan.FromSeconds(5),
        };
        await using var client = new ModbusTcpClient(device, clock);

        await client.ReadInputRegistersAsync(0x1D, 1);
        server.CorruptNextResponses(FakeModbusTcpServer.Persistent);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReadInputRegistersAsync(0x1D, 1));
        server.StopCorrupting();

        // A failed exchange is not a failed connect: the next call reconnects at once.
        Assert.Single(await client.ReadInputRegistersAsync(0x1D, 1));
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task WithoutABackoffEveryCallTriesToConnect()
    {
        await using var client = new ModbusTcpClient(Device(ClosedPort()));

        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => client.ReadHoldingRegistersAsync(0, 1));
        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => client.ReadHoldingRegistersAsync(0, 1));
    }

    // A loopback port nothing listens on, so a connect is refused at once.
    private static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
