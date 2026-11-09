using Gleanvolt.Devices.Testing;

namespace Gleanvolt.Devices.Huawei.Tests;

/// <summary>
/// Huawei's SCharger-22KT-S0 against the conformance tests every charger driver is held to (issue #351).
///
/// <para>Every fixture is synthetic, written from Huawei's protocol: see Fixtures/README.md. The other family is
/// the SUN2000 inverter, which installers sell with it.</para>
/// </summary>
public class HuaweiSCharger22KtConformanceTests : EvChargerDriverConformanceTests<HuaweiSChargerDriverFactory>
{
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    protected override HuaweiSChargerDriverFactory CreateFactory() => HuaweiSChargerDriverFactory.TwentyTwoKt();

    protected override RegisterDump Idle => Load(Fixture("huawei-scharger-22kt-idle.json"));

    protected override RegisterDump PluggedIn => Load(Fixture("huawei-scharger-22kt-plugged-in.json"));

    protected override RegisterDump Charging => Load(Fixture("huawei-scharger-22kt-charging.json"));

    protected override RegisterDump OtherFamily => Load(Fixture("huawei-night-importing.json"));

    [Fact]
    public async Task Charging_at_16_A_reads_11_kW_the_setpoint_and_allowed()
    {
        var driver = CreateDriver(new ReplayModbusClient(Charging));

        var reading = await driver.ReadAsync();
        var settings = await driver.ReadSettingsAsync();

        Assert.Equal(11000, reading.PowerWatts);
        Assert.Equal(16, reading.CurrentAmps);
        Assert.Null(reading.Mode);
        Assert.True(settings.ChargingAllowed);
        Assert.Equal(16, settings.ChargeCurrentAmps);
    }

    [Fact]
    public async Task The_22_kW_default_reads_as_the_highest_setpoint_and_standby_as_not_allowed()
    {
        var settings = await CreateDriver(new ReplayModbusClient(Idle)).ReadSettingsAsync();

        Assert.False(settings.ChargingAllowed);
        Assert.Equal(32, settings.ChargeCurrentAmps);
    }

    [Theory]
    [InlineData(16, 110)]   // 16 A x 3 x 230 V = 11.04 kW
    [InlineData(6, 41)]     // the IEC minimum: 4.14 kW
    [InlineData(32, 220)]   // 22.08 kW, held to the protocol's 22.0
    [InlineData(0, 0)]      // pause
    public async Task The_setpoint_is_written_as_tenths_of_a_kilowatt_high_word_first(int amps, ushort tenthsOfKilowatts)
    {
        var client = new ReplayModbusClient(Charging);
        var driver = CreateDriver(client);

        await driver.SetCurrentAsync(amps == 16 ? 10 : 16, amps, "test");

        var write = Assert.Single(client.Writes);
        Assert.Equal(SChargerRegisterMap.MaxChargePower, write.Start);
        Assert.Equal(new ushort[] { 0, tenthsOfKilowatts }, write.Values);
    }

    [Fact]
    public async Task Start_resumes_and_stop_pauses()
    {
        var client = new ReplayModbusClient(Charging);
        var driver = CreateDriver(client);

        await driver.SetChargingAllowedAsync(true, "start");
        await driver.SetChargingAllowedAsync(false, "stop");

        Assert.Equal(
            new[] { (SChargerRegisterMap.ChargingControl, (ushort)2), (SChargerRegisterMap.ChargingControl, (ushort)1) },
            client.Writes.Select(write => (write.Start, Assert.Single(write.Values))));
    }

    [Fact]
    public async Task A_one_phase_car_converts_at_one_phase_and_the_configured_voltage()
    {
        // ChargeControl:Phases 1 on a three-phase charger: the car draws on one phase, so 16 A is 3.7 kW, not 11.
        var client = new ReplayModbusClient(Charging);
        var driver = CreateFactory().Create(
            EvChargerConnection.Over(client), new EvChargerDriverOptions { Phases = 1, NominalVoltage = 240 });

        await driver.SetCurrentAsync(10, 16, "one phase");

        Assert.Equal(new ushort[] { 0, 38 }, Assert.Single(client.Writes).Values);  // 16 A x 240 V = 3.84 kW
        Assert.Equal(32, (await driver.ReadSettingsAsync()).ChargeCurrentAmps);    // the recorded 11.0 kW, on one phase, clamps to 32 A
    }

    [Fact]
    public async Task A_phase_drawing_current_reads_charging_while_the_power_register_still_reads_0()
    {
        // The power register moves in 100 W steps; a car ramping up shows in the current first.
        var idle = Load(Fixture("huawei-scharger-22kt-idle.json"));
        var dump = idle with
        {
            Holding = new Dictionary<ushort, ushort>(idle.Holding) { [SChargerRegisterMap.PhaseL1Current + 1] = 12 },
        };

        var reading = await CreateDriver(new ReplayModbusClient(dump)).ReadAsync();

        Assert.Equal(EvChargerStatus.Charging, reading.Status);
        Assert.Equal(0, reading.PowerWatts);
    }

    [Theory]
    [InlineData("solax-inverter.json")]
    [InlineData("solax-charger-idle.json")]
    [InlineData("huawei-emma-night-importing.json")]
    public async Task Other_devices_at_the_address_are_not_taken_for_an_SCharger(string fixture)
    {
        var driver = CreateDriver(new ReplayModbusClient(Load(Fixture(fixture))));

        try
        {
            Assert.False((await driver.ProbeAsync()).Recognised, $"{fixture} was taken for an SCharger.");
        }
        catch (ModbusDeviceException)
        {
        }
    }
}

/// <summary>
/// Huawei's single-phase SCharger-7KS-S0 against the same tests. Its own driver id, because its phase count
/// differs (#351).
/// </summary>
public class HuaweiSCharger7KsConformanceTests : EvChargerDriverConformanceTests<HuaweiSChargerDriverFactory>
{
    private static string Fixture(string name) => HuaweiSCharger22KtConformanceTests.Fixture(name);

    protected override HuaweiSChargerDriverFactory CreateFactory() => HuaweiSChargerDriverFactory.SevenKs();

    protected override RegisterDump Idle => Load(Fixture("huawei-scharger-7ks-idle.json"));

    protected override RegisterDump PluggedIn => Load(Fixture("huawei-scharger-7ks-plugged-in.json"));

    protected override RegisterDump Charging => Load(Fixture("huawei-scharger-7ks-charging.json"));

    protected override RegisterDump OtherFamily => Load(Fixture("huawei-l1-night-importing.json"));

    [Fact]
    public async Task Charging_at_16_A_on_one_phase_reads_3_7_kW_and_16_A()
    {
        var driver = CreateDriver(new ReplayModbusClient(Charging));

        var reading = await driver.ReadAsync();
        var settings = await driver.ReadSettingsAsync();

        Assert.Equal(3700, reading.PowerWatts);
        Assert.Equal(16, reading.CurrentAmps);
        Assert.Equal(16, settings.ChargeCurrentAmps);
    }

    [Theory]
    [InlineData(16, 37)]    // 3.68 kW
    [InlineData(32, 74)]    // 7.36 kW, the model's 7.4
    [InlineData(7, 16)]     // one amp more than the minimum still moves the register: 1.61 kW
    public async Task The_setpoint_is_converted_on_one_phase(int amps, ushort tenthsOfKilowatts)
    {
        var client = new ReplayModbusClient(Charging);

        await CreateDriver(client).SetCurrentAsync(amps == 16 ? 10 : 16, amps, "test");

        Assert.Equal(new ushort[] { 0, tenthsOfKilowatts }, Assert.Single(client.Writes).Values);
    }

    [Fact]
    public void Every_amp_survives_the_round_trip_through_tenths_of_a_kilowatt()
    {
        // 230 W an amp against a 100 W step: close enough that rounding could lose one.
        var capabilities = HuaweiSChargerDriver.SevenKs;
        for (var amps = 0; amps <= capabilities.MaxCurrentAmps; amps++)
        {
            var power = HuaweiSChargerDriver.PowerForAmps(amps, capabilities.Phases, 230, capabilities.MaxCurrentAmps);
            Assert.Equal(amps, HuaweiSChargerDriver.AmpsForPower(power, capabilities.Phases, 230, capabilities.MaxCurrentAmps));
        }
    }
}
