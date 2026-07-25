using Microsoft.Extensions.Logging.Abstractions;
using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Infrastructure.RegisterMaps;

namespace Solax.Infrastructure.Tests;

public class EvChargerControlTests
{
    private static readonly ushort ModeAddress = EvChargerRegisterMap.ChargerUseMode.Address;
    private static readonly ushort CurrentAddress = EvChargerRegisterMap.ChargeCurrentSetpoint.Address;
    private static readonly ushort CommandAddress = EvChargerRegisterMap.ControlCommand.Address;

    private static EvChargerControl Create(FakeModbusClient client) =>
        new(client, NullLogger<EvChargerControl>.Instance);

    private static EvChargerControl CreateDryRun(FakeModbusClient client) =>
        new(client, NullLogger<EvChargerControl>.Instance, dryRun: true);

    [Fact]
    public async Task ReadSettingsAsync_DecodesCurrentFrom001AScale()
    {
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)EvChargerMode.Eco);
        client.SetHolding(CurrentAddress, 800); // 0.01A scale -> 8A

        var settings = await Create(client).ReadSettingsAsync();

        Assert.Equal(new EvChargerSettings(EvChargerMode.Eco, 8), settings);
    }

    [Fact]
    public async Task ApplyAsync_NoChange_WritesNothing()
    {
        var client = new FakeModbusClient();
        var settings = new EvChargerSettings(EvChargerMode.Fast, 10);

        await Create(client).ApplyAsync(current: settings, target: settings, "no change");

        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task ApplyAsync_OnlyModeDiffers_WritesOnlyModeRegister()
    {
        var client = new FakeModbusClient();

        await Create(client).ApplyAsync(
            current: new EvChargerSettings(EvChargerMode.Stop, 10),
            target: new EvChargerSettings(EvChargerMode.Fast, 10),
            "mode only");

        Assert.Equal([(ModeAddress, (ushort)EvChargerMode.Fast)], client.Writes);
    }

    [Fact]
    public async Task ApplyAsync_OnlyCurrentDiffers_WritesCurrentEncodedWith001AScale()
    {
        var client = new FakeModbusClient();

        await Create(client).ApplyAsync(
            current: new EvChargerSettings(EvChargerMode.Fast, 6),
            target: new EvChargerSettings(EvChargerMode.Fast, 16),
            "current only");

        Assert.Equal([(CurrentAddress, (ushort)1600)], client.Writes); // 16A * 100
    }

    [Fact]
    public async Task ApplyAsync_BothDiffer_WritesBothRegisters()
    {
        var client = new FakeModbusClient();

        await Create(client).ApplyAsync(
            current: new EvChargerSettings(EvChargerMode.Stop, 6),
            target: new EvChargerSettings(EvChargerMode.Fast, 12),
            "both");

        Assert.Contains((ModeAddress, (ushort)EvChargerMode.Fast), client.Writes);
        Assert.Contains((CurrentAddress, (ushort)1200), client.Writes); // 12A * 100
        Assert.Equal(2, client.Writes.Count);
    }

    [Fact]
    public async Task ApplyAsync_CurrentAboveHardwareMax_ClampsTo32A()
    {
        var client = new FakeModbusClient();

        await Create(client).ApplyAsync(
            current: new EvChargerSettings(EvChargerMode.Fast, 10),
            target: new EvChargerSettings(EvChargerMode.Fast, 40), // beyond the 32A hardware max
            "clamp");

        Assert.Equal([(CurrentAddress, (ushort)3200)], client.Writes); // clamped to 32A -> 3200
    }

    // 1A hysteresis: a charger sitting at 10A is only re-commanded once the target reaches 11A or 9A.
    [Theory]
    [InlineData(10, 10, false)] // no change
    [InlineData(10, 11, true)]  // +1A -> write
    [InlineData(10, 9, true)]   // -1A -> write
    public async Task ApplyAsync_OnlyWritesCurrentWhenItMovesByAtLeastTheThreshold(int currentAmps, int targetAmps, bool expectWrite)
    {
        var client = new FakeModbusClient();

        await Create(client).ApplyAsync(
            current: new EvChargerSettings(EvChargerMode.Fast, currentAmps),
            target: new EvChargerSettings(EvChargerMode.Fast, targetAmps),
            "threshold");

        Assert.Equal(expectWrite, client.Writes.Count > 0);
    }

    [Fact]
    public async Task ApplyAsync_LargerThreshold_SuppressesSmallSetpointMoves()
    {
        // With a 3A threshold, 10A -> 12A is not worth a write, but 10A -> 13A is.
        var client = new FakeModbusClient();
        var control = new EvChargerControl(client, NullLogger<EvChargerControl>.Instance, currentChangeThresholdAmps: 3);

        await control.ApplyAsync(new EvChargerSettings(EvChargerMode.Fast, 10), new EvChargerSettings(EvChargerMode.Fast, 12), "small move");
        Assert.Empty(client.Writes);

        await control.ApplyAsync(new EvChargerSettings(EvChargerMode.Fast, 10), new EvChargerSettings(EvChargerMode.Fast, 13), "big move");
        Assert.Equal([(CurrentAddress, (ushort)1300)], client.Writes);
    }

    [Fact]
    public async Task PauseAsync_SuspendsViaModeAndNeverSendsAStopCommand()
    {
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)EvChargerMode.Fast);
        client.SetHolding(CurrentAddress, 2000); // 20A

        await Create(client).PauseAsync("no surplus");

        Assert.Contains((ModeAddress, (ushort)EvChargerMode.Stop), client.Writes);
        Assert.Contains((CurrentAddress, (ushort)600), client.Writes); // 6A * 100
        Assert.Equal(2, client.Writes.Count);

        // Critically: the session is NOT terminated, so it can resume without a re-plug.
        Assert.DoesNotContain(client.Writes, w => w.Address == CommandAddress);
    }

    [Fact]
    public async Task StopAsync_SuspendsAndAlsoTerminatesTheSessionWithAStopCommand()
    {
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)EvChargerMode.Fast);
        client.SetHolding(CurrentAddress, 2000); // 20A

        await Create(client).StopAsync("off");

        Assert.Contains((ModeAddress, (ushort)EvChargerMode.Stop), client.Writes);
        Assert.Contains((CurrentAddress, (ushort)600), client.Writes); // 6A * 100
        // Unlike PauseAsync, the session IS torn down.
        Assert.Contains((CommandAddress, (ushort)EvChargerControlCommand.StopCharging), client.Writes);
    }

    [Fact]
    public async Task PauseAsync_AlreadySuspended_WritesNothing()
    {
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)EvChargerMode.Stop);
        client.SetHolding(CurrentAddress, 600); // already 6A

        await Create(client).PauseAsync("no surplus");

        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task SendCommandAsync_StartCharging_WritesTheCommandRegister()
    {
        var client = new FakeModbusClient();

        await Create(client).SendCommandAsync(EvChargerControlCommand.StartCharging, "surplus available");

        Assert.Equal([(CommandAddress, (ushort)EvChargerControlCommand.StartCharging)], client.Writes);
    }

    [Fact]
    public async Task DryRun_PauseAsync_WritesNothingToHardware()
    {
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)EvChargerMode.Fast);
        client.SetHolding(CurrentAddress, 2000);

        await CreateDryRun(client).PauseAsync("no surplus");

        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task DryRun_ApplyAsync_WritesNothingToHardware()
    {
        var client = new FakeModbusClient();

        await CreateDryRun(client).ApplyAsync(
            current: new EvChargerSettings(EvChargerMode.Stop, 6),
            target: new EvChargerSettings(EvChargerMode.Fast, 16),
            "dry run");

        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task DryRun_ReadSettings_ReflectsPriorSimulatedApply()
    {
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)EvChargerMode.Green);
        client.SetHolding(CurrentAddress, 600); // 6A
        var control = CreateDryRun(client);

        var target = new EvChargerSettings(EvChargerMode.Fast, 16);
        await control.ApplyAsync(await control.ReadSettingsAsync(), target, "dry run");

        // No hardware write, but the next read reflects the simulated state.
        Assert.Empty(client.Writes);
        Assert.Equal(target, await control.ReadSettingsAsync());
    }
}
