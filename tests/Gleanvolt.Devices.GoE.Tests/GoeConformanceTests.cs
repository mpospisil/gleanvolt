using System.Net;
using System.Text.Json.Nodes;
using Gleanvolt.Devices.Testing;

namespace Gleanvolt.Devices.GoE.Tests;

/// <summary>
/// The go-e Charger against the conformance tests every charger driver is held to (issue #352), and what is
/// go-e's own: the two keys it writes, the pause as a force state, and the 11 kW unit's ceiling.
///
/// <para>Every fixture is synthetic, written from go-e's API v2 key list: see Fixtures/README.md. The other
/// family is a web server that does not have the API: a go-e with it switched off looks the same.</para>
/// </summary>
public class GoeConformanceTests : HttpEvChargerDriverConformanceTests<GoeChargerDriverFactory>
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    protected override GoeChargerDriverFactory CreateFactory() => new();

    protected override HttpDump Idle => Load(Fixture("goe-idle.json"));

    protected override HttpDump PluggedIn => Load(Fixture("goe-plugged-in.json"));

    protected override HttpDump Charging => Load(Fixture("goe-charging.json"));

    protected override HttpDump OtherFamily => Load(Fixture("goe-api-off.json"));

    private (IEvChargerDriver Driver, HttpChargerReplay Replay) Create(HttpDump dump, bool dryRun = false)
    {
        var replay = new HttpChargerReplay(new ReplayHttpHandler(dump));
        return (CreateDriver(replay, dryRun), replay);
    }

    // A recording whose status has the given keys changed.
    private static HttpDump With(HttpDump dump, Action<JsonObject> change)
    {
        var status = (JsonObject)dump.Responses[GoeChargerDriver.StatusRequest]!.DeepClone();
        change(status);
        return dump with { Responses = new Dictionary<string, JsonNode?> { [GoeChargerDriver.StatusRequest] = status } };
    }

    [Fact]
    public async Task Charging_reads_the_total_power_the_setpoint_and_allowed()
    {
        var (driver, _) = Create(Charging);

        var reading = await driver.ReadAsync();
        var settings = await driver.ReadSettingsAsync();

        Assert.Equal(EvChargerStatus.Charging, reading.Status);
        Assert.Equal(11050, reading.PowerWatts);
        Assert.Equal(16, reading.CurrentAmps);
        Assert.Null(reading.Mode);
        Assert.True(settings.ChargingAllowed);
        Assert.Equal(16, settings.ChargeCurrentAmps);
    }

    [Fact]
    public async Task Neutral_is_not_the_state_a_start_leaves()
    {
        // The owner's own logic (schedule, PV surplus, access control) is in charge in neutral: not ours.
        var settings = await Create(Idle).Driver.ReadSettingsAsync();

        Assert.False(settings.ChargingAllowed);
        Assert.Equal(16, settings.ChargeCurrentAmps);
    }

    [Theory]
    [InlineData(1, true, EvChargerStatus.Available)]
    [InlineData(2, true, EvChargerStatus.Charging)]
    [InlineData(3, true, EvChargerStatus.Preparing)]
    [InlineData(3, false, EvChargerStatus.SuspendedEvse)]
    [InlineData(4, true, EvChargerStatus.SuspendedEv)]
    [InlineData(4, false, EvChargerStatus.SuspendedEvse)]   // our own stop is never "the car is full"
    [InlineData(5, true, EvChargerStatus.Faulted)]
    [InlineData(0, true, EvChargerStatus.Unknown)]
    [InlineData(null, true, EvChargerStatus.Unknown)]       // the charger's internal error: never "no car"
    public void Car_state_and_allowed_map_to_the_contract(int? car, bool allowed, EvChargerStatus expected) =>
        Assert.Equal(expected, GoeChargerDriver.StatusOf(car, allowed));

    [Fact]
    public async Task The_setpoint_writes_amp_and_start_and_stop_write_the_force_state()
    {
        var (driver, replay) = Create(Charging);

        await driver.SetCurrentAsync(16, 10, "down");
        await driver.SetChargingAllowedAsync(true, "start");
        await driver.SetChargingAllowedAsync(false, "stop");

        Assert.Equal(["api/set?amp=10", "api/set?frc=2", "api/set?frc=1"], replay.Writes);
    }

    [Fact]
    public async Task Only_the_setpoint_and_the_force_state_are_ever_written()
    {
        // Both are settings the charger keeps; anything else it keeps (phase switching, the maximum, the logic
        // mode) is the owner's and is never written, whatever the loop asks for.
        var (driver, replay) = Create(Charging);

        for (var cycle = 0; cycle < 5; cycle++)
        {
            await driver.SetChargingAllowedAsync(true, "start");
            await driver.SetCurrentAsync(16, 6, "low");
            await driver.SetCurrentAsync(6, 0, "pause");
            await driver.SetCurrentAsync(0, 32, "resume");
            await driver.SetCurrentAsync(32, 16, "settle");
            await driver.SetChargingAllowedAsync(false, "stop");
        }

        var keys = replay.Writes
            .SelectMany(write => write[(write.IndexOf('?', StringComparison.Ordinal) + 1)..].Split('&'))
            .Select(pair => pair.Split('=')[0])
            .Distinct();
        Assert.All(replay.Writes, write => Assert.StartsWith("api/set?", write, StringComparison.Ordinal));
        Assert.Subset(GoeChargerDriver.WrittenKeys.ToHashSet(), keys.ToHashSet());
    }

    [Fact]
    public async Task A_pause_is_the_force_state_off_and_the_next_setpoint_resumes_with_it()
    {
        var (driver, replay) = Create(Charging);

        await driver.SetChargingAllowedAsync(true, "start");
        await driver.SetCurrentAsync(16, 0, "pause");
        var paused = await driver.ReadSettingsAsync();   // the replay still says frc=2; the pause is the driver's
        await driver.SetCurrentAsync(0, 0, "still paused");
        await driver.SetCurrentAsync(0, 10, "resume");

        Assert.Equal(["api/set?frc=2", "api/set?frc=1", "api/set?amp=10&frc=2"], replay.Writes);
        Assert.True(paused.ChargingAllowed);
        Assert.Equal(0, paused.ChargeCurrentAmps);
    }

    [Fact]
    public async Task A_paused_charger_reads_back_its_pause_from_the_device()
    {
        // What the device says after our pause: force state Off. Still "allowed at 0 A", because the driver paused it.
        var (driver, _) = Create(With(Charging, status => status["frc"] = 1));

        await driver.SetChargingAllowedAsync(true, "start");
        await driver.SetCurrentAsync(16, 0, "pause");
        var settings = await driver.ReadSettingsAsync();

        Assert.True(settings.ChargingAllowed);
        Assert.Equal(0, settings.ChargeCurrentAmps);
        Assert.Equal(0, (await driver.ReadAsync()).CurrentAmps);
    }

    [Fact]
    public async Task Off_that_the_driver_did_not_make_reads_as_stopped()
    {
        // After a restart the driver remembers no pause: Off is the owner's, or a stop.
        var settings = await Create(With(Charging, status => status["frc"] = 1)).Driver.ReadSettingsAsync();

        Assert.False(settings.ChargingAllowed);
        Assert.Equal(16, settings.ChargeCurrentAmps);
    }

    [Fact]
    public async Task A_pause_under_a_stop_writes_nothing_and_a_start_under_a_pause_stays_off()
    {
        var (driver, replay) = Create(Charging);

        await driver.SetChargingAllowedAsync(false, "stop");
        await driver.SetCurrentAsync(16, 0, "pause on shutdown");
        await driver.SetChargingAllowedAsync(true, "start");
        await driver.SetCurrentAsync(0, 8, "first setpoint");

        Assert.Equal(["api/set?frc=1", "api/set?frc=1", "api/set?amp=8&frc=2"], replay.Writes);
    }

    [Fact]
    public async Task An_11_kW_unit_is_never_asked_for_more_than_16_A()
    {
        var (driver, replay) = Create(With(Charging, status => status["var"] = 11));

        await driver.ReadAsync();
        await driver.SetCurrentAsync(10, 32, "full");

        Assert.Equal(["api/set?amp=16"], replay.Writes);
    }

    [Fact]
    public async Task A_key_the_charger_refuses_throws()
    {
        var (driver, replay) = Create(Charging);
        replay.Handler.AnswerWrite = _ => ReplayHttpHandler.Json("""{"amp":"value out of range"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.SetCurrentAsync(16, 10, "down"));
        Assert.Contains("value out of range", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_api_off_a_read_throws_and_says_where_to_switch_it_on()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(OtherFamily).Driver.ReadAsync());

        Assert.Contains("HTTP API v2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_probe_reports_a_404_as_not_recognised()
    {
        var probe = await Create(OtherFamily).Driver.ProbeAsync();

        Assert.False(probe.Recognised);
        Assert.Equal((double)(int)HttpStatusCode.NotFound, probe.Reading);
    }

    [Fact]
    public async Task The_probe_does_not_take_another_json_api_for_a_go_e()
    {
        var other = With(Charging, status =>
        {
            status.Remove("car");
            status.Remove("fwv");
        });

        Assert.False((await Create(other).Driver.ProbeAsync()).Recognised);
    }

    [Fact]
    public void The_snapshot_asks_for_no_serial_number_and_no_network()
    {
        Assert.Equal([GoeChargerDriver.StatusRequest], CreateFactory().HttpSnapshot.Requests);
        Assert.DoesNotContain("sse", GoeChargerDriver.StatusKeys);
        Assert.DoesNotContain("wss", GoeChargerDriver.StatusKeys);
        Assert.DoesNotContain("scan", GoeChargerDriver.StatusKeys);
    }
}
