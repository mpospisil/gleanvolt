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

    /// <summary>A single-phase L1 with a DDSU666-H meter, at night, importing (#321).</summary>
    private static RegisterDump L1NightImporting => Load(Fixture("huawei-l1-night-importing.json"));

    /// <summary>A single-phase LC0 with a DDSU666-H meter, the battery charging while exporting (#321).</summary>
    private static RegisterDump Lc0BatteryCharging => Load(Fixture("huawei-lc0-battery-charging.json"));

    protected override IEnumerable<RegisterDump> Dumps =>
        [NightImporting, BatteryCharging, Map0NightDischarging, L1NightImporting, Lc0BatteryCharging];

    [Fact]
    public async Task A_single_phase_hybrid_reads_import_and_charging_as_positive()
    {
        var night = await CreateFactory().Create(new ReplayModbusClient(L1NightImporting), new InverterDriverOptions()).ReadAsync();
        var charging = await CreateFactory().Create(new ReplayModbusClient(Lc0BatteryCharging), new InverterDriverOptions()).ReadAsync();

        Assert.Equal(620, night.GridWatts);
        Assert.InRange(night.PvWatts, 0, IdleToleranceWatts);
        Assert.Equal(2500, charging.BatteryWatts);
        Assert.Equal(-900, charging.GridWatts);
        Assert.Equal(45.5, charging.BatterySocPercent, precision: 6);
    }

    [Theory]
    [InlineData("huawei-l1-night-importing.json")]
    [InlineData("huawei-lc0-battery-charging.json")]
    public void A_single_phase_meter_reports_its_one_phase_as_the_total(string file)
    {
        // What a tester's L1/LC0 dump has to show for the driver to need no single-phase case (#321): a
        // DDSU666-H says "single-phase" in its meter type, and the total the driver reads is phase A's
        // power, with B and C empty.
        var holding = Load(Fixture(file)).Holding;
        int Int32(ushort address) => unchecked((int)(((uint)holding[address] << 16) | holding[(ushort)(address + 1)]));

        Assert.Equal(0, holding[37125]);
        Assert.Equal(Int32(37132), Int32(HuaweiRegisterMap.MeterActivePower));
        Assert.Equal(0, Int32(37134));
        Assert.Equal(0, Int32(37136));
    }

    [Theory]
    [InlineData("SUN2000-10KTL-M1")]
    [InlineData("SUN2000-8K-MAP0")]
    [InlineData("SUN2000-5KTL-L1")]
    [InlineData("SUN2000-6K-LC0")]
    public void Every_supported_model_is_a_sun2000(string model) =>
        Assert.True(HuaweiSun2000Driver.IsSun2000(model));

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
