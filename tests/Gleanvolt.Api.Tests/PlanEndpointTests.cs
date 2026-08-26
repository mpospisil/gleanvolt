using System.Net;
using System.Text.Json;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// Quoting a targeted charge. The two things that matter: it composes the request exactly as the web
/// UI's form does — same conversions, same refusals — and it writes to nothing while doing it.
/// </summary>
public sealed class PlanEndpointTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static object EnergyAsk(double kWh = 22, int hoursAhead = 17) => new
    {
        energyKWh = kWh,
        departBy = Fixtures.Now.AddHours(hoursAhead).ToString("o"),
    };

    [Fact]
    public async Task Quotes_a_plan_for_an_energy_target()
    {
        var client = await _host.StartAsync();

        var body = await (await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", EnergyAsk())).ReadAsync();
        var plan = body.GetProperty("plan");

        Assert.Equal(22000, body.GetProperty("request").Number("requiredEnergyWh"));
        Assert.Equal("solarPlusGrid", plan.Text("strategy"));
        Assert.Equal(14600, plan.Number("solarEnergyWh"));
        Assert.Equal(7400, plan.Number("gridEnergyWh"));
        Assert.Equal(2, plan.GetProperty("blocks").GetArrayLength());
        Assert.Equal("solar", plan.GetProperty("blocks")[0].Text("source"));

        // The whole point of the endpoint: nothing was set and nothing was started.
        Assert.Empty(_host.Target.Sets);
        Assert.Empty(_host.Actions.Starts);
    }

    [Fact]
    public async Task Converts_a_state_of_charge_target_once_from_what_the_car_reported()
    {
        var client = await _host.StartAsync();
        _host.Vehicle.Set(new VehicleState(Fixtures.Now.AddHours(-2), SocPercent: 42));

        var body = await (await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", new
        {
            targetSocPercent = 80,
            departBy = Fixtures.Now.AddHours(17).ToString("o"),
        })).ReadAsync();

        var request = body.GetProperty("request");

        // (80 - 42)% of a 77 kWh pack, divided by 0.9 for what the charger meters but the cells never see.
        Assert.Equal(0.38 * 77000 / 0.9, request.Number("requiredEnergyWh"), 1);
        Assert.Equal(80, request.Number("targetSocPercent"));
        Assert.Equal(42, request.Number("vehicleSocPercentAtRequest"));
    }

    [Fact]
    public async Task Refuses_a_state_of_charge_target_the_car_is_already_past()
    {
        var client = await _host.StartAsync();
        _host.Vehicle.Set(new VehicleState(Fixtures.Now, SocPercent: 85));

        var response = await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", new
        {
            targetSocPercent = 80,
            departBy = Fixtures.Now.AddHours(17).ToString("o"),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("already at 85%", (await response.ReadAsync()).Text("detail"));
    }

    [Fact]
    public async Task Refuses_a_state_of_charge_target_with_no_reading_to_measure_from()
    {
        var client = await _host.StartAsync();

        var response = await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", new
        {
            targetSocPercent = 80,
            departBy = Fixtures.Now.AddHours(17).ToString("o"),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("kilowatt-hours", (await response.ReadAsync()).Text("detail"));
    }

    [Fact]
    public async Task Refuses_a_departure_in_the_past_and_one_past_the_horizon()
    {
        var client = await _host.StartAsync();

        var past = await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", EnergyAsk(hoursAhead: -1));
        Assert.Contains("in the past", (await past.ReadAsync()).Text("detail"));

        var far = await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", EnergyAsk(hoursAhead: 48));
        Assert.Contains("36 hours away", (await far.ReadAsync()).Text("detail"));
    }

    [Fact]
    public async Task Refuses_a_request_that_asks_both_ways_at_once()
    {
        var client = await _host.StartAsync();
        _host.Vehicle.Set(new VehicleState(Fixtures.Now, SocPercent: 42));

        var response = await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", new
        {
            energyKWh = 22,
            targetSocPercent = 80,
            departBy = Fixtures.Now.AddHours(17).ToString("o"),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not both", (await response.ReadAsync()).Text("detail"));
    }

    [Fact]
    public async Task Prices_what_holding_the_last_stretch_costs()
    {
        var client = await _host.StartAsync();
        _host.Vehicle.Set(new VehicleState(Fixtures.Now, SocPercent: 42));

        var body = await (await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", new
        {
            targetSocPercent = 90,
            departBy = Fixtures.Now.AddHours(17).ToString("o"),
            priority = "justInTime",
        })).ReadAsync();

        Assert.True(body.GetProperty("request").Number("tailEnergyWh") > 0);
        Assert.Equal(80, body.GetProperty("request").Number("restSocPercent"));

        // The counterfactual is what makes the choice informed: the same energy by the same time,
        // priced as cheaply as possible.
        Assert.Equal(11000, body.GetProperty("plan").Number("gridEnergyWh"));
        Assert.Equal(7400, body.GetProperty("cheapestPlan").Number("gridEnergyWh"));
    }

    [Fact]
    public async Task Does_not_price_a_counterfactual_when_nothing_is_held()
    {
        var client = await _host.StartAsync();

        var body = await (await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", EnergyAsk())).ReadAsync();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("cheapestPlan").ValueKind);
        Assert.Single(_host.Preview.Requests);
    }

    [Fact]
    public async Task Says_so_when_there_is_no_telemetry_to_plan_from()
    {
        var client = await _host.StartAsync();
        _host.Preview.HasTelemetry = false;

        var response = await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", EnergyAsk());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task The_quote_and_the_promise_are_composed_the_same_way()
    {
        var client = await _host.StartAsync();
        _host.Vehicle.Set(new VehicleState(Fixtures.Now, SocPercent: 42));

        var ask = new
        {
            targetSocPercent = 80,
            departBy = Fixtures.Now.AddHours(17).ToString("o"),
            priority = "justInTime",
        };

        var quoted = await (await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", ask)).ReadAsync();
        await client.PostAsJsonAsync("/api/v1/charging/start", new { mode = "targeted", target = ask });

        var started = _host.Target.Request!;

        Assert.Equal(quoted.GetProperty("request").Number("requiredEnergyWh"), started.RequiredEnergyWh, 6);
        Assert.Equal(quoted.GetProperty("request").Number("tailEnergyWh"), started.TailEnergyWh, 6);
        Assert.Equal(TargetedChargePriority.JustInTime, started.Priority);
    }

    // -- The round trip (#128): a quote carries what you may edit, and editing nothing changes nothing.

    [Fact]
    public async Task A_quote_carries_the_limits_you_may_edit()
    {
        var client = await _host.StartAsync();

        var body = await (await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", Target())).ReadAsync();
        var editable = body.GetProperty("editable");

        // Present, and empty in the sense that matters: nothing here narrows anything yet.
        Assert.NotEqual(JsonValueKind.Null, editable.ValueKind);
        Assert.NotEqual(JsonValueKind.Null, editable.GetProperty("planId").ValueKind);
        Assert.Equal(JsonValueKind.Null, editable.GetProperty("notBefore").ValueKind);
        Assert.Equal(JsonValueKind.Null, editable.GetProperty("maxGridEnergyWh").ValueKind);
    }

    [Fact]
    public async Task Sending_an_unedited_quote_back_is_a_no_op()
    {
        // The property the whole feature rests on: round-trip what you were shown and you get what you
        // were shown. Without it, "edit the plan" is not a thing anybody can reason about.
        var client = await _host.StartAsync();

        var first = await (await client.PostAsJsonAsync("/api/v1/plans/targeted/preview", Target())).ReadAsync();

        var again = await (await client.PostAsJsonAsync(
            "/api/v1/plans/targeted/preview",
            new
            {
                energyKWh = 22,
                departBy = Fixtures.Now.AddHours(17).ToString("o"),
                editable = JsonSerializer.Deserialize<JsonElement>(first.GetProperty("editable").GetRawText()),
            })).ReadAsync();

        Assert.Equal(first.GetProperty("plan").GetRawText(), again.GetProperty("plan").GetRawText());
        Assert.Equal(
            first.GetProperty("request").Number("requiredEnergyWh"),
            again.GetProperty("request").Number("requiredEnergyWh"));
    }

    [Fact]
    public async Task An_edited_quote_reaches_the_planner_as_constraints()
    {
        // Re-quoting under an edit is what lets somebody change a bound, see what it costs, and change
        // it again -- without having to start the charge to find out.
        var client = await _host.StartAsync();
        var notBefore = Fixtures.Now.AddHours(4);

        await client.PostAsJsonAsync(
            "/api/v1/plans/targeted/preview",
            new
            {
                energyKWh = 22,
                departBy = Fixtures.Now.AddHours(17).ToString("o"),
                editable = new { notBefore = notBefore.ToString("o"), maxGridEnergyWh = 5000 },
            });

        var quoted = _host.Preview.Requests[^1];

        Assert.NotNull(quoted.Constraints);
        Assert.Equal(notBefore, quoted.Constraints!.NotBefore);
        Assert.Equal(5000, quoted.Constraints.MaxGridEnergyWh);
    }

    [Fact]
    public async Task An_edited_quote_echoes_the_limits_back_so_the_next_edit_need_not_rebuild_them()
    {
        var client = await _host.StartAsync();
        var notBefore = Fixtures.Now.AddHours(4);

        var body = await (await client.PostAsJsonAsync(
            "/api/v1/plans/targeted/preview",
            new
            {
                energyKWh = 22,
                departBy = Fixtures.Now.AddHours(17).ToString("o"),
                editable = new { notBefore = notBefore.ToString("o") },
            })).ReadAsync();

        Assert.Equal(notBefore, body.GetProperty("editable").GetProperty("notBefore").GetDateTimeOffset());
    }

    private static object Target() => new
    {
        energyKWh = 22,
        departBy = Fixtures.Now.AddHours(17).ToString("o"),
    };
}
