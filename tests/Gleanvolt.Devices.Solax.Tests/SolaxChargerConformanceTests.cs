using Gleanvolt.Devices.Testing;

namespace Gleanvolt.Devices.Solax.Tests;

/// <summary>
/// The SolaX X3-HAC charger driver against the conformance tests every charger driver is held to (issue #350).
///
/// <para>The idle and plugged-in fixtures are real Settings → charger → "Save register snapshot"s of the
/// reference installation; a waiting car with the use-mode on Stop reads Finishing. The charging one is
/// synthetic until a real snapshot replaces it: the idle one with the state changed (see
/// <c>Fixtures/README.md</c>). The other family is the SolaX
/// inverter, real: the two answer Modbus on the same port, and a swapped pair of addresses once cost a day.</para>
/// </summary>
public class SolaxChargerConformanceTests : EvChargerDriverConformanceTests<SolaxChargerDriverFactory>
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    protected override SolaxChargerDriverFactory CreateFactory() => new();

    protected override RegisterDump Idle => Load(Fixture("solax-charger-idle.json"));

    protected override RegisterDump PluggedIn => Load(Fixture("solax-charger-plugged-in.json"));

    protected override RegisterDump Charging => Load(Fixture("solax-charger-charging.json"));

    protected override RegisterDump OtherFamily => Load(Fixture("solax-night-importing.json"));

    [Fact]
    public async Task Charging_at_16_A_reads_the_setpoint_and_fast()
    {
        var driver = CreateDriver(new ReplayModbusClient(Charging));

        var reading = await driver.ReadAsync();
        var settings = await driver.ReadSettingsAsync();

        Assert.Equal(16, reading.CurrentAmps);
        Assert.Equal("Fast", reading.Mode);
        Assert.True(settings.ChargingAllowed);
        Assert.Equal(16, settings.ChargeCurrentAmps);
    }

    [Fact]
    public async Task A_waiting_car_on_Stop_reads_finishing_and_not_allowed()
    {
        // The HAC's own word for a plugged-in car it is not charging: Finishing, not Preparing. The control
        // path already reads Finishing / Stop / 0 W as a stopped charger (#135, #280).
        var driver = CreateDriver(new ReplayModbusClient(PluggedIn));

        var reading = await driver.ReadAsync();
        var settings = await driver.ReadSettingsAsync();

        Assert.Equal(EvChargerStatus.Finishing, reading.Status);
        Assert.Equal("Stop", reading.Mode);
        Assert.False(settings.ChargingAllowed);
        Assert.Equal(16, settings.ChargeCurrentAmps);
    }

    [Fact]
    public async Task Every_SolaX_inverter_recording_is_refused_by_the_chargers_probe()
    {
        // Not only the night import the suite names: every state the inverter has been recorded in.
        foreach (var name in new[] { "solax-battery-charging.json", "solax-night-discharging.json", "solax-night-ev-charging.json" })
        {
            var probe = await CreateDriver(new ReplayModbusClient(Load(Fixture(name)))).ProbeAsync();

            Assert.False(probe.Recognised, $"{name} was taken for a charger (read {probe.Reading}).");
        }
    }
}
