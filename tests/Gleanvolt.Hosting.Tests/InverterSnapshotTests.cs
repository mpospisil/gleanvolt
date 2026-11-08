using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Devices.Solax;
using Gleanvolt.Devices.Testing;
using Gleanvolt.Hosting.Setup;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The running inverter's register snapshot (issue #319): the configured driver's ranges, read on the
/// inverter's client, written as a dump that loads back into a replay.
/// </summary>
public class InverterSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 21, 30, 0, TimeSpan.Zero);

    private static PvSystemInfo Site() => new(
        Id: "", Name: "", Address: "", Latitude: null, Longitude: null, AzimuthDegrees: null, TiltDegrees: null,
        PeakPowerWatts: null, InverterCapacityWatts: null, LossFactor: null, InstallDate: null,
        Inverter: new PvDeviceInfo("Inverter", "", "SolaX X3-HYB-G4 PRO", new DeviceConfig { Host = "192.168.2.10" }),
        Chargers: []);

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
        var snapshot = new InverterSnapshot(
            device, Site(), [new SolaxInverterDriverFactory()], new FixedTime(Now), NullLogger<InverterSnapshot>.Instance);

        var file = await snapshot.TakeAsync("  night, importing ");

        Assert.Equal("gleanvolt-registers-solax-20261108-213000.json", file.FileName);
        Assert.Empty(device.Writes);

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

        var snapshot = provider.GetRequiredService<IInverterSnapshot>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.TakeAsync(string.Empty));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
