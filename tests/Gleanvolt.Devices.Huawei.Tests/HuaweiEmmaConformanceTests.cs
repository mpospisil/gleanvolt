using Gleanvolt.Devices.Testing;

namespace Gleanvolt.Devices.Huawei.Tests;

/// <summary>
/// The SUN2000 driver behind an EMMA (issue #322) against the conformance tests: the grid from the EMMA on
/// unit 0, PV and the battery from the inverter through it. The fixtures are <b>synthetic</b>, from evcc's
/// <c>huawei-emma.yaml</c>; the EMMA's grid sign is the assumption a tester's dump (#325) has to confirm.
/// </summary>
public class HuaweiEmmaConformanceTests : InverterDriverConformanceTests<HuaweiSun2000DriverFactory>
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    protected override HuaweiSun2000DriverFactory CreateFactory() => new();

    protected override InverterDriverOptions Options => new() { Topology = HuaweiSun2000DriverFactory.EmmaTopology };

    protected override RegisterDump NightImporting => Load(Fixture("huawei-emma-night-importing.json"));

    protected override RegisterDump BatteryCharging => Load(Fixture("huawei-emma-battery-charging.json"));

    /// <summary>A SolaX inverter at the address: no EMMA answers on unit 0, and no SUN2000 behind it.</summary>
    protected override RegisterDump OtherFamily => Load(Fixture("solax-inverter.json"));

    [Fact]
    public async Task The_grid_is_read_from_the_emma_and_the_rest_from_the_inverter()
    {
        var client = new ReplayModbusClient(BatteryCharging);

        var reading = await CreateDriver(client).ReadAsync();

        Assert.Equal(-1500, reading.GridWatts);
        Assert.Equal(7200, reading.PvWatts);
        Assert.Equal(4000, reading.BatteryWatts);
        Assert.Equal([null, (byte?)0, null], client.Reads.Select(read => read.UnitId));
        Assert.Equal(HuaweiRegisterMap.EmmaGridActivePower, client.Reads[1].Start);
    }

    [Fact]
    public async Task Through_the_dongle_the_same_recording_has_no_meter_to_read()
    {
        // The topology matters: without it, the driver asks the inverter for a meter it does not have here.
        var driver = CreateFactory().Create(new ReplayModbusClient(NightImporting), new InverterDriverOptions());

        await Assert.ThrowsAsync<ModbusDeviceException>(() => driver.ReadAsync());
    }

    [Fact]
    public async Task The_probe_asks_the_emma_before_the_inverter()
    {
        var client = new ReplayModbusClient(NightImporting);

        var probe = await CreateDriver(client).ProbeAsync();

        Assert.True(probe.Recognised);
        Assert.Equal((byte?)0, client.Reads[0].UnitId);
    }

    [Fact]
    public void An_unknown_topology_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            CreateFactory().Create(new ReplayModbusClient(NightImporting), new InverterDriverOptions { Topology = "smartlogger" }));
    }

    [Fact]
    public void The_factory_offers_the_dongle_first_then_the_emma()
    {
        Assert.Equal(["dongle", "emma"], CreateFactory().Topologies.Select(topology => topology.Id));
    }
}
