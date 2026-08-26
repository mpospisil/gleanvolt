using System.Text.Json;
using System.Net;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// The endpoints that only observe. What they must get right is the honest answer when there is
/// nothing to say — no poll yet, no forecast, no vehicle feed, a store that will not open — because
/// that is the case a caller cannot tell from a wrong number.
/// </summary>
public sealed class ReadEndpointTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Status_reports_the_last_poll()
    {
        var client = await _host.StartAsync();
        _host.Status.Set(Fixtures.Status());

        var body = await (await client.GetAsync("/api/v1/status")).ReadAsync();

        Assert.Equal("solar", body.Text("mode"));
        Assert.Equal("charging", body.Text("state"));
        Assert.Equal(5300, body.Number("solarPowerWatts"));
        Assert.Equal(-200, body.Number("gridPowerWatts"));
        Assert.Equal(78, body.Number("batterySocPercent"));
        Assert.True(body.GetProperty("batteryHold").GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("dayPlan").ValueKind);
    }

    [Fact]
    public async Task Status_says_so_rather_than_inventing_zeroes_before_the_first_poll()
    {
        var client = await _host.StartAsync();

        var response = await client.GetAsync("/api/v1/status");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("No poll has completed", (await response.ReadAsync()).Text("detail"));
    }

    [Fact]
    public async Task Health_is_not_ok_until_a_poll_has_landed()
    {
        var client = await _host.StartAsync();

        var body = await (await client.GetAsync("/api/v1/health")).ReadAsync();

        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("1.2.3-test", body.Text("version"));
        Assert.Equal("Europe/Prague", body.Text("timeZoneId"));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastPollAt").ValueKind);
        Assert.True(body.GetProperty("energyHistoryAvailable").GetBoolean());
    }

    [Fact]
    public async Task Health_is_ok_on_a_fresh_poll_and_not_on_a_stale_one()
    {
        var client = await _host.StartAsync();

        _host.Status.Set(Fixtures.Status(timestamp: Fixtures.Now.AddSeconds(-10)));
        Assert.True((await (await client.GetAsync("/api/v1/health")).ReadAsync()).GetProperty("ok").GetBoolean());

        _host.Status.Set(Fixtures.Status(timestamp: Fixtures.Now.AddMinutes(-30)));
        var stale = await (await client.GetAsync("/api/v1/health")).ReadAsync();

        Assert.False(stale.GetProperty("ok").GetBoolean());
        Assert.Equal(1800, stale.Number("lastPollAgeSeconds"));
    }

    [Fact]
    public async Task Health_reports_a_store_that_will_not_open_rather_than_failing()
    {
        var client = await _host.StartAsync();
        _host.Energy.Fails = true;

        var body = await (await client.GetAsync("/api/v1/health")).ReadAsync();

        Assert.False(body.GetProperty("energyHistoryAvailable").GetBoolean());
        Assert.True(body.GetProperty("sessionHistoryAvailable").GetBoolean());
    }

    [Fact]
    public async Task Energy_intervals_come_back_oldest_first_with_their_derived_columns()
    {
        var client = await _host.StartAsync();
        _host.Energy.Intervals.Add(Fixtures.Interval(Fixtures.Now.AddHours(-1), solarKwh: 1.2, evKwh: 0.8));
        _host.Energy.Intervals.Add(Fixtures.Interval(Fixtures.Now.AddHours(-2), solarKwh: 0.9));

        var body = await (await client.GetAsync("/api/v1/energy/intervals")).ReadAsync();
        var intervals = body.GetProperty("intervals");

        Assert.Equal(2, body.Number("count"));
        Assert.Equal(0.9, intervals[0].Number("solarKwh"));

        // House load is the residual of the energy balance, computed rather than stored -- if the API
        // shipped it as a column of its own it could disagree with the columns it came from.
        Assert.Equal(0.9 + 0.1 - 0.2 - 0.3, intervals[0].Number("houseLoadKwh"), 3);
        Assert.Equal(1, intervals[0].Number("coverage"));
    }

    [Fact]
    public async Task Energy_intervals_refuse_a_range_wider_than_the_cap()
    {
        var client = await _host.StartAsync();

        var response = await client.GetAsync(
            $"/api/v1/energy/intervals?from={Uri.EscapeDataString(Fixtures.Now.AddYears(-1).ToString("o"))}"
            + $"&to={Uri.EscapeDataString(Fixtures.Now.ToString("o"))}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("several requests", (await response.ReadAsync()).Text("detail"));
    }

    [Fact]
    public async Task Energy_day_sums_the_local_day_in_the_sites_own_zone()
    {
        var client = await _host.StartAsync();
        var localMidnight = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.FromHours(1));

        _host.Energy.Intervals.Add(Fixtures.Interval(localMidnight.AddHours(10), solarKwh: 2, evKwh: 1));
        _host.Energy.Intervals.Add(Fixtures.Interval(localMidnight.AddHours(11), solarKwh: 3, evKwh: 2));

        // The day before, which a UTC-based day boundary would have swept in.
        _host.Energy.Intervals.Add(Fixtures.Interval(localMidnight.AddMinutes(-30), solarKwh: 9));

        var body = await (await client.GetAsync("/api/v1/energy/days/2026-01-15")).ReadAsync();

        Assert.Equal(5, body.Number("solarKwh"));
        Assert.Equal(3, body.Number("evKwh"));
        Assert.Equal(2, body.Number("intervalCount"));
        Assert.Equal("Europe/Prague", body.Text("timeZoneId"));
    }

    [Fact]
    public async Task Energy_reports_a_disabled_store_as_unavailable()
    {
        var client = await _host.StartAsync();
        _host.Energy.Fails = true;

        var response = await client.GetAsync("/api/v1/energy/intervals");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("disabled in configuration", (await response.ReadAsync()).Text("detail"));
    }

    [Fact]
    public async Task Sessions_list_newest_first_and_say_when_they_were_truncated()
    {
        var client = await _host.StartAsync(new ApiOptions
        {
            Enabled = true,
            Keys = new Dictionary<string, string> { [ApiTestHost.KeyName] = ApiTestHost.Key },
            MaxSessions = 1,
        });

        _host.Sessions.Sessions.Add(Fixtures.Session(Guid.NewGuid(), Fixtures.Now.AddDays(-2)));
        _host.Sessions.Sessions.Add(Fixtures.Session(Guid.NewGuid(), Fixtures.Now.AddDays(-1)));

        var body = await (await client.GetAsync("/api/v1/sessions")).ReadAsync();

        Assert.Equal(1, body.Number("count"));
        Assert.True(body.GetProperty("truncated").GetBoolean());

        var session = body.GetProperty("sessions")[0];
        Assert.Equal(Fixtures.Now.AddDays(-1), session.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal("targeted", session.Text("startMode"));
        Assert.Equal(14600d / 22000, session.Number("solarFraction"), 4);
    }

    [Fact]
    public async Task A_session_that_does_not_exist_is_a_404_not_an_empty_document()
    {
        var client = await _host.StartAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/sessions/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_session_comes_back_with_its_samples_and_events()
    {
        var client = await _host.StartAsync();
        var id = Guid.NewGuid();
        var session = Fixtures.Session(id, Fixtures.Now.AddHours(-4));

        _host.Sessions.Documents[id] = ChargingSessionDocument.Create(
            session,
            [],
            [new ChargingSessionEvent(id, Fixtures.Now.AddHours(-4), ChargingSessionEventKind.SessionStarted, "took control")]);

        var body = await (await client.GetAsync($"/api/v1/sessions/{id}")).ReadAsync();

        Assert.Equal(ChargingSessionDocument.CurrentSchemaVersion, body.Number("schemaVersion"));
        Assert.Equal(22000, body.GetProperty("session").Number("energyDeliveredWh"));
        Assert.Equal("took control", body.GetProperty("events")[0].Text("detail"));
    }

    [Fact]
    public async Task Vehicle_reports_the_age_of_a_reading_and_whether_it_is_stale()
    {
        var client = await _host.StartAsync();
        _host.Vehicle.Set(new VehicleState(
            CapturedAt: Fixtures.Now.AddHours(-13),
            SocPercent: 42,
            RangeKm: 176,
            PlugState: VehiclePlugState.Connected));

        var body = await (await client.GetAsync("/api/v1/vehicle")).ReadAsync();

        Assert.True(body.GetProperty("available").GetBoolean());
        Assert.Equal(42, body.Number("socPercent"));
        Assert.Equal(13 * 3600, body.Number("ageSeconds"));

        // Past the configured 12 hours: the number is still reported, and so is the fact that the feed
        // has gone quiet, because a caller that cannot see the clock will otherwise treat it as current.
        Assert.True(body.GetProperty("stale").GetBoolean());
        Assert.True(body.GetProperty("canTargetSoc").GetBoolean());
    }

    [Fact]
    public async Task Vehicle_with_no_feed_is_a_supported_installation_not_a_fault()
    {
        var client = await _host.StartAsync();

        var response = await client.GetAsync("/api/v1/vehicle");
        var body = await response.ReadAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.False(body.GetProperty("canTargetSoc").GetBoolean());
    }

    [Fact]
    public async Task Vehicle_cannot_target_soc_without_a_configured_pack()
    {
        var client = await _host.StartAsync(batteryCapacityKWh: 0);
        _host.Vehicle.Set(new VehicleState(Fixtures.Now, SocPercent: 42));

        var body = await (await client.GetAsync("/api/v1/vehicle")).ReadAsync();

        Assert.False(body.GetProperty("canTargetSoc").GetBoolean());
    }

    [Fact]
    public async Task Forecast_reports_having_none_rather_than_reporting_darkness()
    {
        var client = await _host.StartAsync();

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("retrievedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("todayExpectedWh").ValueKind);
        Assert.Empty(body.GetProperty("periods").EnumerateArray());
    }

    [Fact]
    public async Task Forecast_does_not_spend_the_weather_quota_unless_asked()
    {
        var client = await _host.StartAsync();
        _host.Weather.IsConfigured = true;
        _host.Weather.Reading = new WeatherReading(
            new WeatherObservation(Fixtures.Now, 3.5, 1013, 80, 40, 10000, "Clouds", "scattered clouds"),
            null,
            null);

        var plain = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("weather").ValueKind);
        Assert.Equal(0, _host.Weather.Calls);

        var asked = await (await client.GetAsync("/api/v1/forecast?weather=true")).ReadAsync();
        Assert.Equal("Clouds", asked.GetProperty("weather").Text("condition"));
        Assert.Equal(1, _host.Weather.Calls);
    }

    [Fact]
    public async Task Forecast_carries_the_periods_and_the_bands()
    {
        var client = await _host.StartAsync();
        var periodEnd = Fixtures.Now.AddHours(1);
        _host.Forecast.Forecast = new SolarForecast(
            Fixtures.Now.AddMinutes(-20),
            [new SolarForecastPeriod(periodEnd, TimeSpan.FromMinutes(30), 4000, 2500, 5200)]);

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();
        var period = body.GetProperty("periods")[0];

        Assert.Equal(4000, period.Number("estimatedPowerWatts"));
        Assert.Equal(2500, period.Number("estimatedPowerWattsP10"));
        Assert.Equal(2000, period.Number("energyWh"));
    }

    [Fact]
    public async Task The_vehicle_endpoint_reports_the_car_as_configured_not_only_as_reported()
    {
        _host.Car = EvInfo.Unknown with
        {
            Id = "id4",
            Name = "The ID.4",
            Make = "Volkswagen",
            Model = "ID.4 Pro",
            BatteryCapacityKWh = 77,
            Phases = 3,
            MinChargingCurrentAmps = 6,
            MaxChargingCurrentAmps = 16,
        };

        var client = await _host.StartAsync();
        _host.Vehicle.Set(new VehicleState(Fixtures.Now, SocPercent: 42));

        var body = (await (await client.GetAsync("/api/v1/vehicle")).ReadAsync()).GetProperty("vehicle");

        Assert.Equal("id4", body.Text("id"));
        Assert.Equal("Volkswagen", body.Text("make"));
        Assert.Equal(77, body.Number("batteryCapacityKWh"));
        Assert.Equal(3, body.GetProperty("phases").GetInt32());
    }

    [Fact]
    public async Task The_vehicle_endpoint_says_nothing_about_a_car_nobody_described()
    {
        var client = await _host.StartAsync();
        _host.Vehicle.Set(new VehicleState(Fixtures.Now, SocPercent: 42));

        var body = await (await client.GetAsync("/api/v1/vehicle")).ReadAsync();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("vehicle").ValueKind);
        Assert.True(body.GetProperty("available").GetBoolean());
    }
}
