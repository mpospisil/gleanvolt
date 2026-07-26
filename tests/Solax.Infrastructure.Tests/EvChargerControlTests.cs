using Microsoft.Extensions.Logging.Abstractions;
using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Infrastructure.RegisterMaps;

namespace Solax.Infrastructure.Tests;

public class EvChargerControlTests
{
    private static readonly ushort ModeAddress = EvChargerRegisterMap.ChargerUseMode.Address;
    private static readonly ushort CurrentAddress = EvChargerRegisterMap.ChargeCurrentSetpoint.Address;

    private static EvChargerControl Create(FakeModbusClient client, int threshold = 1) =>
        new(client, NullLogger<EvChargerControl>.Instance, dryRun: false, currentChangeThresholdAmps: threshold);

    private static EvChargerControl CreateDryRun(FakeModbusClient client) =>
        new(client, NullLogger<EvChargerControl>.Instance, dryRun: true);

    [Fact]
    public async Task ReadSettingsAsync_ReadsModeAndDecodesCurrentFrom001AScale()
    {
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)EvChargerMode.Fast);
        client.SetHolding(CurrentAddress, 1600); // 16A

        var settings = await Create(client).ReadSettingsAsync();

        Assert.Equal(new EvChargerSettings(EvChargerMode.Fast, 16), settings);
    }

    [Fact]
    public async Task SetCurrentAsync_WritesCurrentEncodedWith001AScale()
    {
        var client = new FakeModbusClient();

        await Create(client).SetCurrentAsync(activeAmps: 6, targetAmps: 16, "charge");

        Assert.Equal([(CurrentAddress, (ushort)1600)], client.Writes); // 16A * 100, current register only
    }

    [Fact]
    public async Task SetCurrentAsync_ChangeBelowThreshold_WritesNothing()
    {
        var client = new FakeModbusClient();

        await Create(client).SetCurrentAsync(activeAmps: 10, targetAmps: 10, "no change");

        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task SetCurrentAsync_PauseValueZero_IsAllowed()
    {
        var client = new FakeModbusClient();

        await Create(client).SetCurrentAsync(activeAmps: 16, targetAmps: 0, "pause");

        Assert.Equal([(CurrentAddress, (ushort)0)], client.Writes); // 0 = pause, not clamped up to 6A
    }

    [Fact]
    public async Task SetCurrentAsync_AboveHardwareMax_ClampsTo32A()
    {
        var client = new FakeModbusClient();

        await Create(client).SetCurrentAsync(activeAmps: 10, targetAmps: 40, "clamp");

        Assert.Equal([(CurrentAddress, (ushort)3200)], client.Writes); // clamped to 32A -> 3200
    }

    [Fact]
    public async Task SetCurrentAsync_LargerThreshold_SuppressesSmallMoves()
    {
        var client = new FakeModbusClient();
        var control = Create(client, threshold: 3);

        await control.SetCurrentAsync(activeAmps: 10, targetAmps: 12, "small"); // +2 < 3 -> no write
        Assert.Empty(client.Writes);

        await control.SetCurrentAsync(activeAmps: 10, targetAmps: 13, "big"); // +3 -> write
        Assert.Equal([(CurrentAddress, (ushort)1300)], client.Writes);
    }

    [Fact]
    public async Task SetCurrentAsync_NeverWritesTheModeRegister()
    {
        var client = new FakeModbusClient();

        await Create(client).SetCurrentAsync(activeAmps: 0, targetAmps: 16, "charge");

        Assert.DoesNotContain(client.Writes, w => w.Address == ModeAddress);
    }

    [Fact]
    public async Task DryRun_SetCurrentAsync_WritesNothingButSimulatesTheValue()
    {
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)EvChargerMode.Fast);
        client.SetHolding(CurrentAddress, 600); // 6A
        var control = CreateDryRun(client);

        await control.SetCurrentAsync(activeAmps: 6, targetAmps: 16, "dry run");

        Assert.Empty(client.Writes);
        // The next read reflects the simulated setpoint, while the mode is still read live.
        Assert.Equal(new EvChargerSettings(EvChargerMode.Fast, 16), await control.ReadSettingsAsync());
    }
}
