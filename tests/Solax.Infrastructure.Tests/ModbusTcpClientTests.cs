using System.Diagnostics;
using Solax.Core.Models;
using Solax.Infrastructure.Modbus;

namespace Solax.Infrastructure.Tests;

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
}
