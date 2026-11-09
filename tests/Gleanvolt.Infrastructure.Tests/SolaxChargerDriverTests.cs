using Microsoft.Extensions.Logging;
using Gleanvolt.Devices;
using Gleanvolt.Devices.Solax;
using Gleanvolt.Infrastructure.RegisterMaps;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The SolaX X3-HAC behind the charger driver contract (issue #348): its encodings, the setpoint's
/// hysteresis, start and stop as the use-mode, dry-run, and the behaviours that moved here with it from
/// <c>EvChargerControl</c> and <c>EnergyStateReader</c>.
/// </summary>
public class SolaxChargerDriverTests
{
    private static readonly ushort ModeAddress = EvChargerRegisterMap.ChargerUseMode.Address;
    private static readonly ushort CurrentAddress = EvChargerRegisterMap.ChargeCurrentSetpoint.Address;
    private static readonly ushort RunModeAddress = EvChargerRegisterMap.RunMode.Address;
    private static readonly ushort PowerAddress = EvChargerRegisterMap.ChargePowerTotal.Address;

    private static SolaxChargerDriver Create(FakeModbusClient client, int threshold = 1) =>
        new(client, options: new EvChargerDriverOptions { CurrentChangeThresholdAmps = threshold });

    private static SolaxChargerDriver CreateDryRun(FakeModbusClient client, ILogger<SolaxChargerDriver>? logger = null) =>
        new(client, logger, new EvChargerDriverOptions { DryRun = true });

    [Theory]
    [InlineData((ushort)0, EvChargerStatus.Available)]
    [InlineData((ushort)2, EvChargerStatus.Charging)]
    [InlineData((ushort)4, EvChargerStatus.Faulted)]
    [InlineData((ushort)13, EvChargerStatus.Stopping)]
    [InlineData((ushort)14, EvChargerStatus.Unknown)]
    [InlineData((ushort)99, EvChargerStatus.Unknown)]
    public void StatusFromRunMode_MapsKnownAndUnknownCodes(ushort raw, EvChargerStatus expected)
    {
        Assert.Equal(expected, SolaxChargerDriver.StatusFromRunMode(raw));
    }

    [Fact]
    public async Task ReadAsync_DecodesStatusPowerSetpointAndMode()
    {
        var client = new FakeModbusClient();
        client.SetHolding(RunModeAddress, (ushort)EvChargerStatus.Charging);
        client.SetHolding(PowerAddress, 7000);
        client.SetHolding(ModeAddress, (ushort)SolaxChargerUseMode.Fast);
        client.SetHolding(CurrentAddress, 1600);

        var reading = await Create(client).ReadAsync();

        Assert.Equal(new EvChargerReading(EvChargerStatus.Charging, 7000, 16, "Fast"), reading);
    }

    [Fact]
    public async Task ReadAsync_ReportsAJunkUseModeAsNoMode()
    {
        // The link drops about 45 times a day and the charger reports junk use-modes on recovery: a value
        // the driver does not know is no mode at all, not something to act on.
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, 7);

        var reading = await Create(client).ReadAsync();

        Assert.Null(reading.Mode);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task ReadAsync_ReportsTheCarTheChargerStartedOnPlugIn_AndWritesNothing()
    {
        // #192: the charger starts the car at 16 A on plug-in, before any mode is picked. The driver reports
        // that as it is -- charging, at 16 A, in Fast -- and leaves the deciding to the controllers.
        var client = new FakeModbusClient();
        client.SetHolding(RunModeAddress, (ushort)EvChargerStatus.Charging);
        client.SetHolding(PowerAddress, 11000);
        client.SetHolding(ModeAddress, (ushort)SolaxChargerUseMode.Fast);
        client.SetHolding(CurrentAddress, 1600);
        var driver = Create(client);

        var reading = await driver.ReadAsync();
        var settings = await driver.ReadSettingsAsync();

        Assert.Equal(EvChargerStatus.Charging, reading.Status);
        Assert.Equal(16, reading.CurrentAmps);
        Assert.Equal(new EvChargerSettings(true, 16, "Fast"), settings);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task ReadAsync_Throws_WhenTheStatusCannotBeRead()
    {
        // The caller (EnergyStateReader) turns this into Unknown; the driver must not guess.
        var client = new ThrowingInputClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SolaxChargerDriver(client).ReadAsync());
    }

    [Theory]
    [InlineData((ushort)0, true)]
    [InlineData((ushort)13, true)]
    [InlineData((ushort)14, false)]
    [InlineData((ushort)4096, false)]
    public async Task ProbeAsync_RecognisesARunMode(ushort value, bool recognised)
    {
        var client = new FakeModbusClient();
        client.SetHolding(RunModeAddress, value);

        var result = await Create(client).ProbeAsync();

        Assert.Equal(new EvChargerProbeResult(recognised, value), result);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public void TheLanScanAndTheDriverAgreeOnWhatAChargerReads()
    {
        Assert.Equal(SolaxChargerDriver.ProbeRegister, DeviceRoleCheck.Register(Core.Models.PvDeviceRole.Charger));
        Assert.True(DeviceRoleCheck.IsPlausible(Core.Models.PvDeviceRole.Charger, 13));
        Assert.False(DeviceRoleCheck.IsPlausible(Core.Models.PvDeviceRole.Charger, 14));
    }

    [Fact]
    public void TheFactoryMakesADriverOnAModbusConnection()
    {
        var factory = new SolaxChargerDriverFactory();

        Assert.Equal("solax", factory.Id);
        Assert.Equal(EvChargerTransport.Modbus, factory.Transport);
        Assert.Equal(SolaxChargerDriver.SolaxCapabilities, factory.Capabilities);
        Assert.IsType<SolaxChargerDriver>(factory.Create(EvChargerConnection.Over(new FakeModbusClient()), new EvChargerDriverOptions()));
        Assert.Throws<ArgumentException>(() =>
            factory.Create(EvChargerConnection.Over(new HttpClient()), new EvChargerDriverOptions()));
    }

    [Fact]
    public void TheSnapshotCoversEveryRegisterTheDriverReads()
    {
        var spec = new SolaxChargerDriverFactory().Snapshot;
        bool Covers(RegisterTable table, ushort address) =>
            spec.Ranges.Any(range => range.Table == table && range.Addresses.Contains(address));

        Assert.True(Covers(RegisterTable.Input, RunModeAddress));
        Assert.True(Covers(RegisterTable.Input, PowerAddress));
        Assert.True(Covers(RegisterTable.Holding, ModeAddress));
        Assert.True(Covers(RegisterTable.Holding, CurrentAddress));
    }

    [Fact]
    public async Task ReadSettingsAsync_ReadsModeAndDecodesCurrentFrom001AScale()
    {
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)SolaxChargerUseMode.Fast);
        client.SetHolding(CurrentAddress, 1600); // 16A

        var settings = await Create(client).ReadSettingsAsync();

        Assert.Equal(new EvChargerSettings(ChargingAllowed: true, 16, "Fast"), settings);
    }

    [Theory]
    [InlineData((ushort)0, "Stop")]
    [InlineData((ushort)2, "Eco")]
    [InlineData((ushort)3, "Green")]
    [InlineData((ushort)7, "7")]
    public async Task ReadSettingsAsync_OnlyFastAllowsCharging(ushort raw, string mode)
    {
        // Stop, the owner's Eco and Green, and the junk a recovering link reports are all "not allowed";
        // telling them apart over time is ChargerOwnership's job, not one read's.
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, raw);

        var settings = await Create(client).ReadSettingsAsync();

        Assert.Equal(new EvChargerSettings(ChargingAllowed: false, 0, mode), settings);
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
    [InlineData(true, (ushort)1)]   // Fast
    [InlineData(false, (ushort)0)]  // Stop
    public async Task SetChargingAllowedAsync_WritesTheUseModeRegisterRaw(bool allowed, ushort expected)
    {
        var client = new FakeModbusClient();

        await Create(client).SetChargingAllowedAsync(allowed, "action");

        Assert.Equal([(ModeAddress, expected)], client.Writes); // unscaled, unlike the current setpoint
    }

    [Fact]
    public async Task SetChargingAllowedAsync_WritesEveryTime_EvenForTheModeAlreadyOnTheDevice()
    {
        // No hysteresis and no read-back comparison: the Off button says stop charging, and it has to
        // stop charging whatever the register already said.
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)SolaxChargerUseMode.Stop);
        var control = Create(client);

        await control.SetChargingAllowedAsync(false, "off");
        await control.SetChargingAllowedAsync(false, "off again");

        Assert.Equal(2, client.Writes.Count);
    }

    [Fact]
    public async Task SetChargingAllowedAsync_DoesNotTouchTheCurrentSetpoint()
    {
        // Stop is what stops the car; re-writing the setpoint would be a second write to say the same
        // thing, and it would lose whatever the last control cycle left there.
        var client = new FakeModbusClient();

        await Create(client).SetChargingAllowedAsync(false, "off");

        Assert.DoesNotContain(client.Writes, w => w.Address == CurrentAddress);
    }

    [Fact]
    public async Task DryRun_SetChargingAllowedAsync_WritesNothingAndLogsTheEncodedValue()
    {
        var client = new FakeModbusClient();
        var logger = new CapturingLogger();

        await CreateDryRun(client, logger).SetChargingAllowedAsync(true, "Solar started by Web UI.");

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
        client.SetHolding(ModeAddress, (ushort)SolaxChargerUseMode.Green);
        client.SetHolding(CurrentAddress, 600);
        var control = CreateDryRun(client);

        await control.SetChargingAllowedAsync(true, "dry run");

        Assert.True((await control.ReadSettingsAsync()).ChargingAllowed);
    }

    [Fact]
    public async Task DryRun_SetCurrentAsync_WritesNothingButSimulatesTheValue()
    {
        var client = new FakeModbusClient();
        client.SetHolding(ModeAddress, (ushort)SolaxChargerUseMode.Fast);
        client.SetHolding(CurrentAddress, 600); // 6A
        var control = CreateDryRun(client);

        await control.SetCurrentAsync(activeAmps: 6, targetAmps: 16, "dry run");

        Assert.Empty(client.Writes);
        // The next read reflects the simulated setpoint; the use-mode is still read live, because no
        // action has "written" one in this run.
        Assert.Equal(new EvChargerSettings(true, 16, "Fast"), await control.ReadSettingsAsync());
    }

    private sealed class CapturingLogger : ILogger<SolaxChargerDriver>
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

    /// <summary>Connects, then refuses every input-register read.</summary>
    private sealed class ThrowingInputClient : FakeModbusClient
    {
        public override Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
            throw new IOException("connection reset");
    }
}
