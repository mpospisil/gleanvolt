using System.Net;
using System.Text.Json;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// The three endpoints that write. Each is a button the web UI already has, so what is tested here is
/// the order things happen in and what happens when the hardware refuses — not the control logic,
/// which lives behind these seams and is tested there.
/// </summary>
public sealed class ControlEndpointTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static object Target(double kWh = 22) => new
    {
        energyKWh = kWh,
        departBy = Fixtures.Now.AddHours(17).ToString("o"),
    };

    [Fact]
    public async Task Starts_a_mode()
    {
        var client = await _host.StartAsync();
        _host.Status.Set(Fixtures.Status());

        var body = await (await client.PostAsJsonAsync("/api/v1/charging/start", new { mode = "forecasted" })).ReadAsync();

        Assert.True(body.GetProperty("succeeded").GetBoolean());
        Assert.Equal(ChargeControlMode.Forecasted, Assert.Single(_host.Actions.Starts).Mode);

        // The caller gets the state back with the answer, so it never has to poll to find out.
        Assert.Equal(78, body.GetProperty("status").Number("batterySocPercent"));
    }

    [Fact]
    public async Task Refuses_off_as_a_mode_to_start()
    {
        var client = await _host.StartAsync();

        var response = await client.PostAsJsonAsync("/api/v1/charging/start", new { mode = "off" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("/charging/stop", (await response.ReadAsync()).Text("detail"));
        Assert.Empty(_host.Actions.Starts);
    }

    [Fact]
    public async Task Refuses_targeted_with_no_target()
    {
        var client = await _host.StartAsync();

        var response = await client.PostAsJsonAsync("/api/v1/charging/start", new { mode = "targeted" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Actions.Starts);
    }

    [Fact]
    public async Task Refuses_a_target_on_a_mode_that_has_no_use_for_one()
    {
        var client = await _host.StartAsync();

        var response = await client.PostAsJsonAsync("/api/v1/charging/start", new { mode = "solar", target = Target() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Actions.Starts);
    }

    [Fact]
    public async Task Sets_the_request_before_selecting_the_targeted_mode()
    {
        var client = await _host.StartAsync();

        var body = await (await client.PostAsJsonAsync(
            "/api/v1/charging/start", new { mode = "targeted", target = Target() })).ReadAsync();

        Assert.True(body.GetProperty("succeeded").GetBoolean());
        Assert.Equal(22000, body.GetProperty("target").Number("requiredEnergyWh"));
        Assert.Equal(22000, _host.Target.Request!.RequiredEnergyWh);
        Assert.Single(_host.Target.Sets);
        Assert.Equal(ChargeControlMode.Targeted, Assert.Single(_host.Actions.Starts).Mode);
    }

    [Fact]
    public async Task Drops_the_request_again_when_the_charger_refuses()
    {
        var client = await _host.StartAsync();
        _host.Actions.NextResult = ChargeActionResult.Failed("the charger did not accept Fast");

        var response = await client.PostAsJsonAsync("/api/v1/charging/start", new { mode = "targeted", target = Target() });
        var body = await response.ReadAsync();

        // 200 with succeeded=false: the call was understood, and the controller is in exactly the state
        // it was in before. A failed hardware write is an answer, not a protocol error.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.GetProperty("succeeded").GetBoolean());
        Assert.Contains("did not accept Fast", body.Text("message"));

        // A promise nobody is keeping is worse than no promise.
        Assert.Null(_host.Target.Request);
        Assert.Single(_host.Target.Clears);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("target").ValueKind);
    }

    [Fact]
    public async Task Stopping_clears_a_standing_target()
    {
        var client = await _host.StartAsync();
        await client.PostAsJsonAsync("/api/v1/charging/start", new { mode = "targeted", target = Target() });

        var body = await (await client.PostAsJsonAsync("/api/v1/charging/stop", new { })).ReadAsync();

        Assert.True(body.GetProperty("succeeded").GetBoolean());
        Assert.Equal($"API ({ApiTestHost.KeyName})", Assert.Single(_host.Actions.Stops));
        Assert.Null(_host.Target.Request);
    }

    [Fact]
    public async Task Arms_and_releases_the_battery_hold()
    {
        var client = await _host.StartAsync();
        _host.Status.Set(Fixtures.Status());

        await client.PutAsJsonAsync("/api/v1/battery-hold", new { hold = true });
        Assert.True(_host.Hold.Hold);

        await client.PutAsJsonAsync("/api/v1/battery-hold", new { hold = false });
        Assert.False(_host.Hold.Hold);

        Assert.All(_host.Hold.Sets, set => Assert.Equal($"API ({ApiTestHost.KeyName})", set.Source));
    }

    [Fact]
    public async Task Refuses_the_hold_when_the_feature_is_switched_off()
    {
        var client = await _host.StartAsync();
        _host.Status.Set(Fixtures.Status(batteryHoldEnabled: false));

        var response = await client.PutAsJsonAsync("/api/v1/battery-hold", new { hold = true });

        // Accepting it would record an intent that silently does nothing -- the one outcome an operator
        // cannot tell apart from a hold that is working.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(_host.Hold.Sets);
    }

    // -- The fast charge's amount (#119).

    [Fact]
    public async Task Starts_a_fast_charge_with_no_amount_at_all()
    {
        // Omitting 'fast' is Full: the mode as it behaved before it could be given one.
        var client = await _host.StartAsync();
        _host.Status.Set(Fixtures.Status());

        var body = await (await client.PostAsJsonAsync(
            "/api/v1/charging/start", new { mode = "fastNoBattery" })).ReadAsync();

        Assert.True(body.GetProperty("succeeded").GetBoolean());
        Assert.Equal(ChargeControlMode.FastNoBattery, Assert.Single(_host.Actions.Starts).Mode);
        Assert.Null(_host.Fast.Limit);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("fast").ValueKind);
    }

    [Fact]
    public async Task Takes_an_energy_amount_and_reports_it_back()
    {
        var client = await _host.StartAsync();
        _host.Status.Set(Fixtures.Status());

        var body = await (await client.PostAsJsonAsync(
            "/api/v1/charging/start",
            new { mode = "fastNoBattery", fast = new { basis = "energy", energyKWh = 20 } })).ReadAsync();

        Assert.True(body.GetProperty("succeeded").GetBoolean());
        Assert.Equal(20_000, _host.Fast.Limit!.RequiredEnergyWh);
        Assert.Equal(20_000, body.GetProperty("fast").Number("requiredEnergyWh"));

        // Set before the mode is selected, so the controller never sees a cycle of one without the other.
        Assert.Single(_host.Fast.Sets);
    }

    [Fact]
    public async Task Converts_a_state_of_charge_amount_once_and_records_what_it_was_asked_in()
    {
        var client = await _host.StartAsync();
        _host.Status.Set(Fixtures.Status());
        _host.Vehicle.Set(new VehicleState(Fixtures.Now, SocPercent: 42));

        var body = await (await client.PostAsJsonAsync(
            "/api/v1/charging/start",
            new { mode = "fastNoBattery", fast = new { basis = "soc", targetSocPercent = 60 } })).ReadAsync();

        Assert.True(body.GetProperty("succeeded").GetBoolean());

        // (60 - 42) / 100 * 77000 / 0.9
        Assert.Equal(15_400, _host.Fast.Limit!.RequiredEnergyWh, 0);
        Assert.Equal(60, body.GetProperty("fast").Number("targetSocPercent"));
        Assert.Equal(42, body.GetProperty("fast").Number("vehicleSocPercentAtRequest"));
    }

    [Fact]
    public async Task Refuses_a_state_of_charge_amount_the_car_cannot_answer_for()
    {
        var client = await _host.StartAsync();

        var response = await client.PostAsJsonAsync(
            "/api/v1/charging/start",
            new { mode = "fastNoBattery", fast = new { basis = "soc", targetSocPercent = 60 } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("has not reported", (await response.ReadAsync()).Text("detail"));
        Assert.Empty(_host.Actions.Starts);
    }

    [Fact]
    public async Task Refuses_a_car_already_past_the_amount_asked_for()
    {
        var client = await _host.StartAsync();
        _host.Vehicle.Set(new VehicleState(Fixtures.Now, SocPercent: 64));

        var response = await client.PostAsJsonAsync(
            "/api/v1/charging/start",
            new { mode = "fastNoBattery", fast = new { basis = "soc", targetSocPercent = 60 } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("already at 64%", (await response.ReadAsync()).Text("detail"));
        Assert.Empty(_host.Actions.Starts);
    }

    [Fact]
    public async Task Refuses_an_amount_on_any_other_mode()
    {
        var client = await _host.StartAsync();

        var response = await client.PostAsJsonAsync(
            "/api/v1/charging/start",
            new { mode = "solar", fast = new { basis = "energy", energyKWh = 20 } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("fastNoBattery", (await response.ReadAsync()).Text("detail"));
        Assert.Empty(_host.Actions.Starts);
    }

    [Fact]
    public async Task A_refused_charger_leaves_no_amount_standing()
    {
        // Otherwise the limit sits there with nothing driving it, ready to end somebody else's charge.
        var client = await _host.StartAsync();
        _host.Actions.NextResult = ChargeActionResult.Failed("the charger did not accept Fast");

        var body = await (await client.PostAsJsonAsync(
            "/api/v1/charging/start",
            new { mode = "fastNoBattery", fast = new { basis = "energy", energyKWh = 20 } })).ReadAsync();

        Assert.False(body.GetProperty("succeeded").GetBoolean());
        Assert.Null(_host.Fast.Limit);
    }

    [Fact]
    public async Task A_full_start_clears_an_amount_left_over_from_an_earlier_charge()
    {
        var client = await _host.StartAsync();
        _host.Status.Set(Fixtures.Status());
        _host.Fast.Set(new FastChargeLimit(20_000, Fixtures.Now.AddHours(-3)), "earlier");

        await client.PostAsJsonAsync("/api/v1/charging/start", new { mode = "fastNoBattery" });

        Assert.Null(_host.Fast.Limit);
    }

    [Fact]
    public async Task Stopping_clears_a_standing_amount()
    {
        var client = await _host.StartAsync();
        _host.Fast.Set(new FastChargeLimit(20_000, Fixtures.Now), "test");

        await client.PostAsync("/api/v1/charging/stop", null);

        Assert.Null(_host.Fast.Limit);
    }
}
