using Gleanvolt.Devices.Testing;

namespace Gleanvolt.Devices.Solax.Tests;

/// <summary>
/// The SolaX driver against the conformance tests every driver is held to (issue #319).
///
/// <para><c>solax-night-importing.json</c> and <c>solax-night-discharging.json</c> are real snapshots of the
/// reference installation. The battery-charging and EV-charger fixtures are <b>synthetic</b> for now: written
/// from the installation's figures, and labelled so in their <c>note</c>. Replace each with a Settings →
/// "Save register snapshot" in the state its name describes; the tests do not change.</para>
/// </summary>
public class SolaxConformanceTests : InverterDriverConformanceTests<SolaxInverterDriverFactory>
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    protected override SolaxInverterDriverFactory CreateFactory() => new();

    protected override RegisterDump NightImporting => Load(Fixture("solax-night-importing.json"));

    protected override RegisterDump BatteryCharging => Load(Fixture("solax-battery-charging.json"));

    protected override RegisterDump OtherFamily => Load(Fixture("solax-ev-charger.json"));

    /// <summary>A real snapshot: the reference installation at night, the battery carrying the house.</summary>
    private static RegisterDump NightDischarging => Load(Fixture("solax-night-discharging.json"));

    protected override IEnumerable<RegisterDump> Dumps => [NightImporting, BatteryCharging, NightDischarging];

    [Fact]
    public async Task A_discharging_battery_at_night_reads_as_negative_battery_with_no_sun()
    {
        var reading = await CreateFactory().Create(new ReplayModbusClient(NightDischarging), new InverterDriverOptions()).ReadAsync();

        Assert.True(reading.BatteryWatts < -IdleToleranceWatts, $"A discharging battery must read as negative; read {reading.BatteryWatts} W.");
        Assert.InRange(reading.PvWatts, 0, IdleToleranceWatts);
        Assert.InRange(reading.BatterySocPercent, 1, 100);
    }
}
