using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using System.Text.Json.Nodes;
using Gleanvolt.Devices.GoE;
using Gleanvolt.Devices.Solax;
using Gleanvolt.Devices.Testing;
using Gleanvolt.Hosting.Setup;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The running devices' register snapshots (issues #319, #350): the configured driver's ranges, read on the
/// device's own client, written as a dump that loads back into a replay.
/// </summary>
public class DeviceSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 21, 30, 0, TimeSpan.Zero);

    private static PvSystemInfo Site() => new(
        Id: "", Name: "", Address: "", Latitude: null, Longitude: null, AzimuthDegrees: null, TiltDegrees: null,
        PeakPowerWatts: null, InverterCapacityWatts: null, LossFactor: null, InstallDate: null,
        Inverter: new PvDeviceInfo("Inverter", "", "SolaX X3-HYB-G4 PRO", new DeviceConfig { Host = "192.168.2.10" }),
        Chargers: [new PvDeviceInfo("wallbox", "", "SolaX X3-HAC", new DeviceConfig { Host = "192.168.2.6" }) { Driver = "solax" }]);

    private static RegisterDump Device()
    {
        var input = Enumerable.Range(0, InverterRegisterMap.TelemetryBlockCount).ToDictionary(address => (ushort)address, _ => (ushort)0);
        input[InverterRegisterMap.BatteryCapacity.Address] = 57;

        const string serial = "10K34567890ABC";
        var holding = Enumerable.Range(0, 7).ToDictionary(i => (ushort)i, i => (ushort)((serial[i * 2] << 8) | serial[i * 2 + 1]));

        return new RegisterDump { Holding = holding, Input = input };
    }

    [Fact]
    public async Task Reads_the_configured_drivers_ranges_into_a_dump_that_replays()
    {
        var device = new ReplayModbusClient(Device());
        var charger = new ReplayModbusClient(new RegisterDump());
        var snapshot = Snapshot(device, charger);

        var file = await snapshot.TakeAsync(PvDeviceRole.Inverter, "  night, importing ");

        Assert.Equal("gleanvolt-registers-solax-20261108-213000.json", file.FileName);
        Assert.Empty(device.Writes);
        Assert.Empty(charger.Reads);

        var dump = RegisterDump.FromJson(file.Json);
        Assert.Equal("solax", dump.Driver);
        Assert.Equal("SolaX X3-HYB-G4 PRO", dump.Family);
        Assert.Equal("night, importing", dump.Note);
        Assert.Equal(Now, dump.CapturedAt);
        Assert.Equal(BuildInfo.Version, dump.GleanvoltVersion);
        Assert.Equal(0x3130, dump.Holding[0]);                   // "10"
        Assert.Equal(0x4B33, dump.Holding[1]);                   // "K3": the four-character prefix
        Assert.Equal(0x2A2A, dump.Holding[2]);                   // "**"

        var reading = await new SolaxInverterDriver(new ReplayModbusClient(dump)).ReadAsync();
        Assert.Equal(57, reading.BatterySocPercent);
    }

    [Fact]
    public async Task In_setup_mode_there_is_no_inverter_to_read()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddGleanvolt(builder.Configuration, new SetupHost(TimeZoneInfo.Utc, IsWindows: false));
        await using var provider = builder.Services.BuildServiceProvider();

        var snapshot = provider.GetRequiredService<IDeviceSnapshot>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.TakeAsync(PvDeviceRole.Inverter, string.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.TakeAsync(PvDeviceRole.Charger, string.Empty));
    }

    [Fact]
    public async Task Reads_the_chargers_ranges_on_the_chargers_client()
    {
        var inverter = new ReplayModbusClient(Device());
        var charger = new ReplayModbusClient(Charger());
        var snapshot = Snapshot(inverter, charger);

        var file = await snapshot.TakeAsync(PvDeviceRole.Charger, "car plugged in, charging at 16 A");

        Assert.Equal("gleanvolt-registers-solax-charger-20261108-213000.json", file.FileName);
        Assert.Empty(inverter.Reads);
        Assert.Empty(charger.Writes);

        var dump = RegisterDump.FromJson(file.Json);
        Assert.Equal("solax", dump.Driver);
        Assert.Equal("SolaX X3-HAC", dump.Family);
        Assert.Equal("car plugged in, charging at 16 A", dump.Note);
        Assert.Equal(0x3530, dump.Holding[EvChargerRegisterMap.SerialNumberStart]);          // "50"
        Assert.Equal(0x3331, dump.Holding[EvChargerRegisterMap.SerialNumberStart + 1]);      // "31": the prefix
        Assert.Equal(0x2A2A, dump.Holding[EvChargerRegisterMap.SerialNumberStart + 2]);      // "**"

        var reading = await new SolaxChargerDriver(new ReplayModbusClient(dump)).ReadAsync();
        Assert.Equal(EvChargerStatus.Charging, reading.Status);
        Assert.Equal(11040, reading.PowerWatts);
        Assert.Equal(16, reading.CurrentAmps);
    }

    [Fact]
    public async Task A_charger_reached_over_http_is_recorded_as_its_drivers_responses()
    {
        // go-e (#352): no registers, so the recording is the API's answers to the driver's own requests, taken on
        // the charger's HTTP client and never on its Modbus one.
        var modbus = new ReplayModbusClient(Charger());
        var handler = new ReplayHttpHandler(new HttpDump
        {
            Responses = new Dictionary<string, JsonNode?>
            {
                [GoeChargerDriver.StatusRequest] = JsonNode.Parse(
                    """{"car":2,"alw":true,"frc":2,"amp":16,"ama":32,"var":22,"nrg":[230,230,230,0,16,16,16,3680,3680,3680,0,11040,99,99,99,0],"fwv":"59.4"}"""),
            },
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://192.168.2.6/") };
        var site = Site() with { Chargers = [Site().Chargers[0] with { Driver = GoeChargerDriverFactory.DriverId, Model = "go-e Gemini 22" }] };
        var snapshot = new DeviceSnapshot(
            new ReplayModbusClient(Device()), modbus, http, site, [new SolaxInverterDriverFactory()],
            [new SolaxChargerDriverFactory(), new GoeChargerDriverFactory()], new FixedTime(Now), NullLogger<DeviceSnapshot>.Instance);

        var file = await snapshot.TakeAsync(PvDeviceRole.Charger, "charging at 16 A");

        Assert.Equal("gleanvolt-http-goe-charger-20261108-213000.json", file.FileName);
        Assert.Empty(modbus.Reads);
        Assert.Empty(handler.Writes);

        var dump = HttpDump.FromJson(file.Json);
        Assert.Equal("goe", dump.Driver);
        Assert.Equal("go-e Gemini 22", dump.Family);
        Assert.Equal("charging at 16 A", dump.Note);

        using var replay = new HttpClient(new ReplayHttpHandler(dump)) { BaseAddress = new Uri("http://charger.test/") };
        var reading = await new GoeChargerDriver(replay).ReadAsync();
        Assert.Equal(EvChargerStatus.Charging, reading.Status);
        Assert.Equal(11040, reading.PowerWatts);
    }

    private static DeviceSnapshot Snapshot(IModbusClient inverter, IModbusClient charger) => new(
        inverter, charger, new HttpClient(), Site(), [new SolaxInverterDriverFactory()], [new SolaxChargerDriverFactory()],
        new FixedTime(Now), NullLogger<DeviceSnapshot>.Instance);

    // A SolaX charger charging at 16 A on three phases: what the charger driver reads, and the serial number.
    private static RegisterDump Charger()
    {
        var input = Enumerable.Range(0, EvChargerRegisterMap.RunMode.Address + 1).ToDictionary(address => (ushort)address, _ => (ushort)0);
        input[EvChargerRegisterMap.RunMode.Address] = (ushort)EvChargerStatus.Charging;
        input[EvChargerRegisterMap.ChargePowerTotal.Address] = 11040;

        const string serial = "50311234567890";
        var holding = Enumerable.Range(0, EvChargerRegisterMap.SerialNumberCount)
            .ToDictionary(i => (ushort)(EvChargerRegisterMap.SerialNumberStart + i), i => (ushort)((serial[i * 2] << 8) | serial[i * 2 + 1]));
        holding[EvChargerRegisterMap.ChargerUseMode.Address] = 1;          // Fast
        holding[EvChargerRegisterMap.ChargeCurrentSetpoint.Address] = 1600; // 16 A

        return new RegisterDump { Holding = holding, Input = input };
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
