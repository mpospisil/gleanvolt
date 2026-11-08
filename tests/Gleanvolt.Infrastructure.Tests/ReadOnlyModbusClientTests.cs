using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Infrastructure.Modbus;

namespace Gleanvolt.Infrastructure.Tests;

public class ReadOnlyModbusClientTests
{
    private static ReadOnlyModbusClient Wrap(FakeModbusClient inner) =>
        new(inner, NullLogger<ReadOnlyModbusClient>.Instance);

    [Fact]
    public async Task WriteSingleRegisterAsync_NeverReachesTheDevice()
    {
        var inner = new FakeModbusClient();

        await Wrap(inner).WriteSingleRegisterAsync(0x60D, 1);

        Assert.Empty(inner.Writes);
    }

    [Fact]
    public async Task WriteMultipleRegistersAsync_NeverReachesTheDevice()
    {
        var inner = new FakeModbusClient();

        await Wrap(inner).WriteMultipleRegistersAsync(0x60D, [1, 2, 3]);

        Assert.Empty(inner.Writes);
    }

    [Fact]
    public async Task ReadsAndConnectPassThrough()
    {
        var inner = new FakeModbusClient();
        inner.SetHolding(0x628, 1600);
        var client = Wrap(inner);

        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        var holding = await client.ReadHoldingRegistersAsync(0x628, 1);
        var input = await client.ReadInputRegistersAsync(0x628, 1);

        Assert.Equal(1600, holding[0]);
        Assert.Equal(1600, input[0]);
    }

    [Fact]
    public async Task SuppressedWrite_DoesNotCorruptSubsequentReads()
    {
        var inner = new FakeModbusClient();
        inner.SetHolding(0x628, 1600);
        var client = Wrap(inner);

        await client.WriteSingleRegisterAsync(0x628, 600);

        // The device still holds its original value — the write was dropped, not applied locally.
        var holding = await client.ReadHoldingRegistersAsync(0x628, 1);
        Assert.Equal(1600, holding[0]);
    }

    [Fact]
    public async Task AnotherUnitIsJustAsReadOnly()
    {
        // The EMMA's own unit (#322): reads reach it, on the one connection; writes reach nothing.
        await using var server = new FakeModbusTcpServer();
        await using var tcp = new ModbusTcpClient(new Gleanvolt.Core.Models.DeviceConfig
        {
            Host = "127.0.0.1", Port = server.Port, UnitId = 1, MinRequestInterval = TimeSpan.Zero,
        });
        var emma = new ReadOnlyModbusClient(tcp, NullLogger<ReadOnlyModbusClient>.Instance).ForUnit(0);

        await emma.ReadHoldingRegistersAsync(31657, 2);
        await emma.WriteSingleRegisterAsync(40000, 1);
        await emma.WriteMultipleRegistersAsync(40000, [1, 2]);

        Assert.Equal(new byte[] { 0 }, server.UnitIds);
    }
}
