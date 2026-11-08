using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Devices.Solax;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The SolaX driver behind the inverter contract (issue #317): its decoding into the contract's units and
/// signs (moved here from EnergyState.FromRawRegisters), its probe, and that the composer cannot tell it
/// from any other driver reporting the same figures.
/// </summary>
public class SolaxInverterDriverTests
{
    private static ushort[] Block(params (RegisterDescriptor Register, ushort Value)[] values)
    {
        var block = new ushort[InverterRegisterMap.TelemetryBlockCount];
        foreach (var (register, value) in values)
        {
            block[register.Address] = value;
        }

        return block;
    }

    [Fact]
    public void Decode_MapsUnsignedBatterySoc()
    {
        var reading = SolaxInverterDriver.Decode(Block((InverterRegisterMap.BatteryCapacity, 42)));

        Assert.Equal(42, reading.BatterySocPercent);
    }

    [Theory]
    [InlineData((ushort)1500, 1500)] // charging
    [InlineData(unchecked((ushort)(short)-1500), -1500)] // discharging
    public void Decode_MapsSignedBatteryPower_PositiveIsCharging(ushort raw, double expectedWatts)
    {
        var reading = SolaxInverterDriver.Decode(Block((InverterRegisterMap.BatteryPowerCharge1, raw)));

        Assert.Equal(expectedWatts, reading.BatteryWatts);
    }

    [Theory]
    [InlineData((ushort)0, (ushort)0, 0)]
    [InlineData((ushort)1500, (ushort)0, -1500)]                        // exporting 1500W
    [InlineData(unchecked((ushort)(short)-1500), (ushort)0xFFFF, 1500)] // importing 1500W
    public void Decode_DecodesFeedinPowerMeter_PositiveIsImporting(ushort low, ushort high, double expectedWatts)
    {
        var reading = SolaxInverterDriver.Decode(Block(
            (InverterRegisterMap.FeedinPowerLow, low),
            (InverterRegisterMap.FeedinPowerHigh, high)));

        Assert.Equal(expectedWatts, reading.GridWatts);
    }

    [Fact]
    public void Decode_SumsSolarPowerAcrossBothMpptTrackers()
    {
        var reading = SolaxInverterDriver.Decode(Block(
            (InverterRegisterMap.Powerdc1, 300),
            (InverterRegisterMap.Powerdc2, 450)));

        Assert.Equal(750, reading.PvWatts);
    }

    [Fact]
    public void Decode_ReadsTheInverterOutputPerPhase_Signed()
    {
        var reading = SolaxInverterDriver.Decode(Block(
            (InverterRegisterMap.GridPowerR, 459),
            (InverterRegisterMap.GridPowerS, unchecked((ushort)(short)-20)),
            (InverterRegisterMap.GridPowerT, 1735)));

        Assert.Equal(new PhaseWatts(459, -20, 1735), reading.InverterOutputPerPhase);
    }

    [Fact]
    public void Capabilities_match_what_the_driver_has()
    {
        var driver = new SolaxInverterDriver(new FakeModbusClient());

        Assert.True(driver.Capabilities.HasBatteryHold);
        Assert.NotNull(driver.BatteryHold);
        Assert.True(driver.Capabilities.HasPhaseOutput);
    }

    [Fact]
    public async Task Probe_recognises_a_state_of_charge()
    {
        var client = new FakeModbusClient();
        client.SetHolding(SolaxInverterDriver.ProbeRegister, 47);

        var probe = await new SolaxInverterDriver(client).ProbeAsync();

        Assert.Equal(new InverterProbeResult(Recognised: true, Reading: 47), probe);
        Assert.True(client.IsConnected);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task Probe_does_not_recognise_what_no_battery_reports()
    {
        // The charger answers on the same register with a number no battery reports.
        var client = new FakeModbusClient();
        client.SetHolding(SolaxInverterDriver.ProbeRegister, 4096);

        var probe = await new SolaxInverterDriver(client).ProbeAsync();

        Assert.Equal(new InverterProbeResult(Recognised: false, Reading: 4096), probe);
    }

    [Fact]
    public async Task The_factory_makes_a_dry_run_driver_whose_hold_writes_nothing()
    {
        var client = new FakeModbusClient();
        var driver = new SolaxInverterDriverFactory().Create(client, new InverterDriverOptions { DryRun = true });

        var state = await driver.BatteryHold!.ApplyAsync(hold: true, activePowerTargetWatts: -2000, DateTimeOffset.UtcNow);

        Assert.True(state.Held);
        Assert.Empty(client.Writes);
        Assert.Equal("solax", new SolaxInverterDriverFactory().Id);
    }

    /// <summary>
    /// The composer's half of the contract: given the same figures, a driver of any brand produces the same
    /// <see cref="EnergyState"/> -- the correction for a blind phase included -- so nothing downstream can
    /// depend on SolaX. The registers are the reference install at 2026-09-12 13:32 (see
    /// <see cref="EnergyStateReaderUnmeteredPhaseTests"/>).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(GridPhase.L3)]
    public async Task The_reader_builds_the_same_state_from_a_fake_driver_as_from_the_SolaX_driver(GridPhase? unmetered)
    {
        var inverter = new FakeModbusClient();
        inverter.SetHolding(InverterRegisterMap.BatteryCapacity.Address, 99);
        inverter.SetHolding(InverterRegisterMap.BatteryPowerCharge1.Address, unchecked((ushort)(short)-250));
        inverter.SetHolding(InverterRegisterMap.Powerdc1.Address, 1702);
        inverter.SetHolding(InverterRegisterMap.Powerdc2.Address, 1534);
        inverter.SetHolding(InverterRegisterMap.FeedinPowerLow.Address, 1031);
        inverter.SetHolding(InverterRegisterMap.GridPowerR.Address, 459);
        inverter.SetHolding(InverterRegisterMap.GridPowerS.Address, 888);
        inverter.SetHolding(InverterRegisterMap.GridPowerT.Address, 1735);

        var fake = new FixedInverterDriver(new InverterReading(
            PvWatts: 3236,
            GridWatts: -1031,
            BatteryWatts: -250,
            BatterySocPercent: 99,
            InverterOutputPerPhase: new PhaseWatts(459, 888, 1735)));

        var fromSolax = await Reader(new SolaxInverterDriver(inverter), unmetered).ReadAsync();
        var fromFake = await Reader(fake, unmetered).ReadAsync();

        Assert.Equal(fromSolax with { Timestamp = default }, fromFake with { Timestamp = default });
    }

    private static EnergyStateReader Reader(IInverterDriver driver, GridPhase? unmetered) =>
        new(
            driver,
            new FakeModbusClient(),
            NullLogger<EnergyStateReader>.Instance,
            new PvSystemInfo(
                Id: "", Name: "", Address: "", Latitude: null, Longitude: null, AzimuthDegrees: null, TiltDegrees: null,
                PeakPowerWatts: null, InverterCapacityWatts: null, LossFactor: null, InstallDate: null,
                Inverter: new PvDeviceInfo("Inverter", "", "", new DeviceConfig { Host = "inverter" }),
                Chargers: [])
            {
                UnmeteredGridPhase = unmetered,
            },
            new ChargingLimits(6, 16, 3));

    /// <summary>A driver of no particular brand that reports one fixed reading.</summary>
    private sealed class FixedInverterDriver(InverterReading reading) : IInverterDriver
    {
        public InverterCapabilities Capabilities { get; } = new(HasBatteryHold: false, HasPhaseOutput: true);

        public IBatteryDischargeControl? BatteryHold => null;

        public Task<InverterReading> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(reading);

        public Task<InverterProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InverterProbeResult(true, reading.BatterySocPercent));
    }
}
