using System.Net;
using System.Text.Json.Nodes;
using Gleanvolt.Devices.Testing;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Devices.Shelly.Tests;

/// <summary>
/// The Shelly TopAC Portable EV Charger against the conformance tests every charger driver is held to (issue
/// #429), and what is its own: the two components it writes, the pause as start_charging off, power's unit, and the
/// warning about Shelly's own load balancing.
///
/// <para>Every fixture is synthetic, written from Shelly's API documentation: see Fixtures/README.md. The other
/// family is a web server without the TopAC's service: another Shelly looks the same.</para>
/// </summary>
public class ShellyTopAcConformanceTests : HttpEvChargerDriverConformanceTests<ShellyTopAcChargerDriverFactory>
{
    private const string LimitTo = "rpc/Number.Set?owner=%22service:0%22&role=%22current_limit%22&value=";
    private const string StartOn = "rpc/Boolean.Set?owner=%22service:0%22&role=%22start_charging%22&value=true";
    private const string StartOff = "rpc/Boolean.Set?owner=%22service:0%22&role=%22start_charging%22&value=false";

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    protected override ShellyTopAcChargerDriverFactory CreateFactory() => new();

    protected override HttpDump Idle => Load(Fixture("shelly-idle.json"));

    protected override HttpDump PluggedIn => Load(Fixture("shelly-plugged-in.json"));

    protected override HttpDump Charging => Load(Fixture("shelly-charging.json"));

    protected override HttpDump OtherFamily => Load(Fixture("shelly-other.json"));

    private (IEvChargerDriver Driver, HttpChargerReplay Replay) Create(HttpDump dump, bool dryRun = false)
    {
        var replay = new HttpChargerReplay(new ReplayHttpHandler(dump));
        return (CreateDriver(replay, dryRun), replay);
    }

    // A recording with one request's answer changed.
    private static HttpDump With(HttpDump dump, string request, Action<JsonObject> change)
    {
        var responses = dump.Responses.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone());
        change((JsonObject)responses[request]!);
        return dump with { Responses = responses };
    }

    private static HttpDump WithValue(HttpDump dump, string request, JsonNode? value) =>
        With(dump, request, answer => answer["value"] = value);

    [Fact]
    public async Task Charging_reads_the_total_power_the_setpoint_and_allowed()
    {
        var (driver, _) = Create(Charging);

        var reading = await driver.ReadAsync();
        var settings = await driver.ReadSettingsAsync();

        Assert.Equal(EvChargerStatus.Charging, reading.Status);
        Assert.Equal(11040, reading.PowerWatts, precision: 6);
        Assert.Equal(16, reading.CurrentAmps);
        Assert.Null(reading.Mode);
        Assert.True(settings.ChargingAllowed);
        Assert.Equal(16, settings.ChargeCurrentAmps);
    }

    [Fact]
    public async Task Not_started_is_not_the_state_a_start_leaves()
    {
        var settings = await Create(Idle).Driver.ReadSettingsAsync();

        Assert.False(settings.ChargingAllowed);
        Assert.Equal(16, settings.ChargeCurrentAmps);
    }

    [Theory]
    [InlineData("charger_free", true, EvChargerStatus.Available)]
    [InlineData("charger_insert", false, EvChargerStatus.Preparing)]
    [InlineData("charger_wait", true, EvChargerStatus.Preparing)]
    [InlineData("charger_wait", false, EvChargerStatus.SuspendedEvse)]
    [InlineData("charger_charging", true, EvChargerStatus.Charging)]
    [InlineData("charger_pause", true, EvChargerStatus.SuspendedEvse)]     // the charger's pause, whoever asked
    [InlineData("charger_complete", true, EvChargerStatus.SuspendedEv)]
    [InlineData("charger_complete", false, EvChargerStatus.SuspendedEvse)] // our own stop is never "the car is full"
    [InlineData("charger_end", true, EvChargerStatus.SuspendedEv)]
    [InlineData("charger_end", false, EvChargerStatus.SuspendedEvse)]
    [InlineData("charger_error", true, EvChargerStatus.Faulted)]
    [InlineData("something_new", true, EvChargerStatus.Unknown)]           // never "no car"
    [InlineData(null, true, EvChargerStatus.Unknown)]
    public void Work_state_and_start_map_to_the_contract(string? state, bool allowed, EvChargerStatus expected) =>
        Assert.Equal(expected, ShellyTopAcChargerDriver.StatusOf(ShellyTopAcChargerDriver.WorkStateOf(state), allowed));

    [Theory]
    [InlineData(11.04, 230, 16, 11040)]   // kilowatts, as evcc reads it
    [InlineData(11040, 230, 16, 11040)]   // watts: volts times amps says so
    [InlineData(4.2, 0, 0, 4200)]         // nothing to check against: kilowatts
    [InlineData(0.003, 230, 0, 3)]        // standby
    [InlineData(-0.01, 230, 0, 0)]        // never negative
    public void Total_power_is_read_in_the_unit_volts_times_amps_agrees_with(double total, double volts, double amps, double watts)
    {
        var phase = () => new JsonObject { ["voltage"] = volts, ["current"] = amps };
        var info = new JsonObject { ["total_power"] = total, ["phase_a"] = phase(), ["phase_b"] = phase(), ["phase_c"] = phase() };

        Assert.Equal(watts, ShellyTopAcChargerDriver.WattsOf(info), precision: 6);
    }

    [Fact]
    public async Task The_setpoint_writes_current_limit_and_start_and_stop_write_start_charging()
    {
        var (driver, replay) = Create(Charging);

        await driver.SetCurrentAsync(16, 10, "down");
        await driver.SetChargingAllowedAsync(true, "start");
        await driver.SetChargingAllowedAsync(false, "stop");

        Assert.Equal([LimitTo + "10", StartOn, StartOff], replay.Writes);
    }

    [Fact]
    public async Task Only_the_setpoint_and_start_charging_are_ever_written()
    {
        // Anything else the charger keeps (auto charge, auto balance, the charge and time limits) is the owner's and
        // is never written, whatever the loop asks for.
        var (driver, replay) = Create(Charging);

        for (var cycle = 0; cycle < 5; cycle++)
        {
            await driver.SetChargingAllowedAsync(true, "start");
            await driver.SetCurrentAsync(16, 6, "low");
            await driver.SetCurrentAsync(6, 0, "pause");
            await driver.SetCurrentAsync(0, 16, "resume");
            await driver.SetCurrentAsync(16, 10, "settle");
            await driver.SetChargingAllowedAsync(false, "stop");
        }

        Assert.All(replay.Writes, write => Assert.True(
            ShellyTopAcChargerDriver.WrittenRoles.Any(role => write.Contains($"&role=%22{role}%22&", StringComparison.Ordinal))
                && (write.StartsWith("rpc/Number.Set?", StringComparison.Ordinal) || write.StartsWith("rpc/Boolean.Set?", StringComparison.Ordinal)),
            write));
    }

    [Fact]
    public async Task A_pause_is_start_charging_off_and_the_next_setpoint_resumes_with_the_limit_first()
    {
        var (driver, replay) = Create(Charging);

        await driver.SetChargingAllowedAsync(true, "start");
        await driver.SetCurrentAsync(16, 0, "pause");
        var paused = await driver.ReadSettingsAsync();   // the replay still says started; the pause is the driver's
        await driver.SetCurrentAsync(0, 0, "still paused");
        await driver.SetCurrentAsync(0, 10, "resume");

        Assert.Equal([StartOn, StartOff, LimitTo + "10", StartOn], replay.Writes);
        Assert.True(paused.ChargingAllowed);
        Assert.Equal(0, paused.ChargeCurrentAmps);
    }

    [Fact]
    public async Task A_paused_charger_reads_back_its_pause_from_the_device()
    {
        // What the device says after our pause: not started. Still "allowed at 0 A", because the driver paused it.
        var (driver, _) = Create(WithValue(Charging, ShellyTopAcChargerDriver.StartChargingRequest, false));

        await driver.SetChargingAllowedAsync(true, "start");
        await driver.SetCurrentAsync(16, 0, "pause");
        var settings = await driver.ReadSettingsAsync();

        Assert.True(settings.ChargingAllowed);
        Assert.Equal(0, settings.ChargeCurrentAmps);
        Assert.Equal(0, (await driver.ReadAsync()).CurrentAmps);
    }

    [Fact]
    public async Task Not_started_that_the_driver_did_not_make_reads_as_stopped()
    {
        // After a restart the driver remembers no pause: not started is the owner's, or a stop.
        var settings = await Create(WithValue(Charging, ShellyTopAcChargerDriver.StartChargingRequest, false)).Driver.ReadSettingsAsync();

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

        Assert.Equal([StartOff, StartOff, LimitTo + "8", StartOn], replay.Writes);
    }

    [Fact]
    public async Task A_fractional_limit_reads_as_whole_amps()
    {
        // The charger regulates in milliamps; the contract's setpoint is whole amps.
        var (driver, _) = Create(WithValue(Charging, ShellyTopAcChargerDriver.CurrentLimitRequest, 9.6));

        Assert.Equal(10, (await driver.ReadSettingsAsync()).ChargeCurrentAmps);
    }

    [Fact]
    public async Task A_refused_set_throws_with_the_chargers_reason()
    {
        var (driver, replay) = Create(Charging);
        replay.Handler.AnswerWrite = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"code":-103,"message":"Invalid argument 'value': out of range"}""", System.Text.Encoding.UTF8, "application/json"),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.SetCurrentAsync(16, 10, "down"));
        Assert.Contains("out of range", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_frame_with_a_200_still_throws()
    {
        var (driver, replay) = Create(Charging);
        replay.Handler.AnswerWrite = _ => ReplayHttpHandler.Json("""{"code":-105,"message":"Not found"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.SetChargingAllowedAsync(true, "start"));
        Assert.Contains("Not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_set_answered_with_null_is_taken()
    {
        var (driver, replay) = Create(Charging);
        replay.Handler.AnswerWrite = _ => ReplayHttpHandler.Json("null");

        await driver.SetCurrentAsync(16, 10, "down");
        await driver.SetChargingAllowedAsync(false, "stop");

        Assert.Equal(2, replay.Writes.Count);
    }

    [Fact]
    public async Task A_password_says_to_switch_it_off()
    {
        using var http = new HttpClient(new UnauthorizedHandler()) { BaseAddress = new Uri("http://charger.test/") };
        var driver = CreateFactory().Create(EvChargerConnection.Over(http), new EvChargerDriverOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ReadAsync());
        Assert.Contains("password", ex.Message, StringComparison.Ordinal);

        var probe = await driver.ProbeAsync();
        Assert.False(probe.Recognised);
        Assert.Equal((double)(int)HttpStatusCode.Unauthorized, probe.Reading);
    }

    [Fact]
    public async Task Without_the_service_a_read_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(OtherFamily).Driver.ReadAsync());
    }

    [Fact]
    public async Task The_probe_reports_a_404_as_not_recognised()
    {
        var probe = await Create(OtherFamily).Driver.ProbeAsync();

        Assert.False(probe.Recognised);
        Assert.Equal((double)(int)HttpStatusCode.NotFound, probe.Reading);
    }

    [Fact]
    public async Task The_probe_reports_the_work_state_as_its_number()
    {
        var probe = await Create(Charging).Driver.ProbeAsync();

        Assert.True(probe.Recognised);
        Assert.Equal((double)(int)ShellyWorkState.Charging, probe.Reading);
    }

    [Fact]
    public async Task The_probe_does_not_take_another_enum_for_the_chargers()
    {
        var other = WithValue(Charging, ShellyTopAcChargerDriver.WorkStateRequest, "on");

        Assert.False((await Create(other).Driver.ProbeAsync()).Recognised);
    }

    [Fact]
    public async Task Auto_balance_on_is_warned_about_once()
    {
        var dump = With(Charging, ShellyTopAcChargerDriver.ServiceConfigRequest, config => config["auto_balance"]!["enable"] = true);
        var logger = new ListLogger();
        var driver = new ShellyTopAcChargerDriver(new HttpChargerReplay(new ReplayHttpHandler(dump)).Connection.Http!, logger);

        await driver.ReadAsync();
        await driver.ReadAsync();

        Assert.Single(logger.Warnings, warning => warning.Contains("auto balance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_charger_without_the_configuration_is_still_read()
    {
        var dump = Charging with
        {
            Responses = Charging.Responses.Where(pair => pair.Key != ShellyTopAcChargerDriver.ServiceConfigRequest)
                .ToDictionary(pair => pair.Key, pair => pair.Value),
        };

        Assert.Equal(EvChargerStatus.Charging, (await Create(dump).Driver.ReadAsync()).Status);
    }

    [Fact]
    public void The_snapshot_asks_for_no_device_id_and_no_network()
    {
        Assert.Equal(ShellyTopAcChargerDriver.ReadRequests, CreateFactory().HttpSnapshot.Requests);
        Assert.All(CreateFactory().HttpSnapshot.Requests, request =>
        {
            Assert.DoesNotContain("GetDeviceInfo", request, StringComparison.Ordinal);
            Assert.DoesNotContain("Shelly.", request, StringComparison.Ordinal);
            Assert.DoesNotContain("WiFi", request, StringComparison.Ordinal);
        });
    }

    private sealed class UnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"code":401,"message":"Unauthorized"}"""),
            });
    }

    private sealed class ListLogger : ILogger<ShellyTopAcChargerDriver>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
