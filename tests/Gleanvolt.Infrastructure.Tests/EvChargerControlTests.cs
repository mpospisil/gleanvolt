using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.RegisterMaps;

namespace Gleanvolt.Infrastructure.Tests;

public class EvChargerControlTests
{
    private static readonly ushort ModeAddress = EvChargerRegisterMap.ChargerUseMode.Address;
    private static readonly ushort CurrentAddress = EvChargerRegisterMap.ChargeCurrentSetpoint.Address;

    private static EvChargerControl Create(FakeModbusClient client, int threshold = 1) =>
        new(client, NullLogger<EvChargerControl>.Instance, dryRun: false, currentChangeThresholdAmps: threshold);

    private static EvChargerControl CreateDryRun(FakeModbusClient client, ILogger<EvChargerControl>? logger = null) =>
        new(client, logger ?? NullLogger<EvChargerControl>.Instance, dryRun: true);

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

    // The use-mode write is new hardware ground (#89): 0x60D has only ever been read here, and the
    // values come from the wills106 map rather than from anything this project has confirmed by
    // writing. These tests pin the encoding; the register itself still wants a dry run first.
    [Theory]
    [InlineData(EvChargerMode.Fast, (ushort)1)]
    [InlineData(EvChargerMode.Stop, (ushort)0)]
    public async Task SetModeAsync_WritesTheUseModeRegisterRaw(EvChargerMode mode, ushort expected)
    {
        var client = new FakeModbusClient();

        await Create(client).SetModeAsync(mode, "action");

        Assert.Equal([(ModeAddress, expected)], client.Writes); // unscaled, unlike the current setpoint
    }

    [Fact]
    public async Task SetModeAsync_WritesEveryTime_EvenForTheModeAlreadyOnTheDevice()
    {
        // No hysteresis and no read-back comparison: the Off button says stop charging, and it has to
        // stop charging whatever the register already said.
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)EvChargerMode.Stop);
        var control = Create(client);

        await control.SetModeAsync(EvChargerMode.Stop, "off");
        await control.SetModeAsync(EvChargerMode.Stop, "off again");

        Assert.Equal(2, client.Writes.Count);
    }

    [Fact]
    public async Task SetModeAsync_DoesNotTouchTheCurrentSetpoint()
    {
        // Stop is what stops the car; re-writing the setpoint would be a second write to say the same
        // thing, and it would lose whatever the last control cycle left there.
        var client = new FakeModbusClient();

        await Create(client).SetModeAsync(EvChargerMode.Stop, "off");

        Assert.DoesNotContain(client.Writes, w => w.Address == CurrentAddress);
    }

    [Fact]
    public async Task DryRun_SetModeAsync_WritesNothingAndLogsTheEncodedValue()
    {
        var client = new FakeModbusClient();
        var logger = new CapturingLogger();

        await CreateDryRun(client, logger).SetModeAsync(EvChargerMode.Fast, "Solar started by Web UI.");

        Assert.Empty(client.Writes);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("[DRY RUN]", message);
        Assert.Contains("Fast", message);
        Assert.Contains("register 1", message);   // the value that would have gone to 0x60D
    }

    [Fact]
    public async Task DryRun_ReadSettingsAsync_ReportsTheSimulatedUseMode()
    {
        // Otherwise a dry run would report every controller Idle -- the charger still reading Green --
        // and say nothing about the setpoints the run exists to check.
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)EvChargerMode.Green);
        client.SetHolding(CurrentAddress, 600);
        var control = CreateDryRun(client);

        await control.SetModeAsync(EvChargerMode.Fast, "dry run");

        Assert.Equal(EvChargerMode.Fast, (await control.ReadSettingsAsync()).Mode);
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
        // The next read reflects the simulated setpoint; the use-mode is still read live, because no
        // action has "written" one in this run.
        Assert.Equal(new EvChargerSettings(EvChargerMode.Fast, 16), await control.ReadSettingsAsync());
    }

    private sealed class CapturingLogger : ILogger<EvChargerControl>
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
