using Gleanvolt.Devices.Testing;

namespace Gleanvolt.Devices.Huawei.Tests;

/// <summary>
/// The SUN2000 driver against the conformance tests every driver is held to (issues #319, #320).
///
/// <para>Every fixture under Fixtures/ is <b>synthetic</b>, written from Huawei's interface definitions and
/// labelled so in its <c>note</c>: the signs in them are the documented ones, not observed ones. A tester's
/// snapshot (#325) replaces each; until both signs are confirmed by one, the family stays "Expected".</para>
/// </summary>
public class HuaweiConformanceTests : InverterDriverConformanceTests<HuaweiSun2000DriverFactory>
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    protected override HuaweiSun2000DriverFactory CreateFactory() => new();

    protected override RegisterDump NightImporting => Load(Fixture("huawei-night-importing.json"));

    protected override RegisterDump BatteryCharging => Load(Fixture("huawei-battery-charging.json"));

    /// <summary>A SolaX inverter at the address: it keeps nothing at the SUN2000's model name.</summary>
    protected override RegisterDump OtherFamily => Load(Fixture("solax-inverter.json"));

    /// <summary>A single-tower MAP0 at night, the battery carrying the house.</summary>
    private static RegisterDump Map0NightDischarging => Load(Fixture("huawei-map0-night-discharging.json"));

    protected override IEnumerable<RegisterDump> Dumps => [NightImporting, BatteryCharging, Map0NightDischarging];

    [Fact]
    public async Task A_discharging_battery_at_night_reads_as_negative_battery_with_no_sun()
    {
        var reading = await CreateFactory().Create(new ReplayModbusClient(Map0NightDischarging), new InverterDriverOptions()).ReadAsync();

        Assert.True(reading.BatteryWatts < -IdleToleranceWatts, $"A discharging battery must read as negative; read {reading.BatteryWatts} W.");
        Assert.InRange(reading.PvWatts, 0, IdleToleranceWatts);
        Assert.Equal(78, reading.BatterySocPercent);
    }

    [Fact]
    public async Task Exporting_reads_as_negative_grid()
    {
        var reading = await CreateFactory().Create(new ReplayModbusClient(BatteryCharging), new InverterDriverOptions()).ReadAsync();

        Assert.Equal(-1200, reading.GridWatts);
        Assert.Equal(6200, reading.PvWatts);
        Assert.Equal(62.3, reading.BatterySocPercent, precision: 6);
    }

    [Fact]
    public async Task A_poll_is_three_reads_of_holding_registers()
    {
        var client = new ReplayModbusClient(NightImporting);

        await CreateFactory().Create(client, new InverterDriverOptions()).ReadAsync();

        Assert.Equal(3, client.Reads.Count);
        Assert.All(client.Reads, read => Assert.Equal(RegisterTable.Holding, read.Table));
    }

    [Fact]
    public async Task The_probe_reports_the_state_of_charge()
    {
        var probe = await CreateFactory().Create(new ReplayModbusClient(Map0NightDischarging), new InverterDriverOptions()).ProbeAsync();

        Assert.True(probe.Recognised);
        Assert.Equal(78, probe.Reading);
    }

    [Fact]
    public async Task The_probe_refuses_a_device_whose_model_name_is_not_a_sun2000()
    {
        var charger = NightImporting with
        {
            Holding = new Dictionary<ushort, ushort>(NightImporting.Holding)
            {
                // "SCharger-7KS-S0": Huawei's wallbox, which keeps a model name at the same address.
                [30000] = ('S' << 8) | 'C',
                [30001] = ('h' << 8) | 'a',
            },
        };

        var probe = await CreateFactory().Create(new ReplayModbusClient(charger), new InverterDriverOptions()).ProbeAsync();

        Assert.False(probe.Recognised);
    }
}
