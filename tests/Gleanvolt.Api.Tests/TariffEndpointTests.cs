using System.Net;
using System.Text.Json;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// The tariff over the API (issue #265): the timeline and the rate now, the strategies there are, and
/// the settings — refused by the same rules Settings uses, with each problem's field and row, and
/// never costing an unselected strategy its settings.
/// </summary>
public sealed class TariffEndpointTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    // The test clock is 14:00 on Thursday 15 January 2026, in Prague.
    private static object Night(string provider = "Table") => new
    {
        provider,
        lowPricePerKwh = 2.10m,
        highPricePerKwh = 4.80m,
        settings = new
        {
            table = new
            {
                weekday = new[] { new { start = "22:00", end = "24:00" }, new { start = "0:00", end = "06:00" } },
                weekend = Array.Empty<object>(),
            },
        },
    };

    [Fact]
    public async Task WithNoTariffTheTimelineIsEmptyAndThereIsNoRateNow()
    {
        var client = await _host.StartAsync();

        var tariff = await (await client.GetAsync("/api/v1/tariff")).ReadAsync();

        Assert.Equal("None", tariff.Text("provider"));
        Assert.Equal("CZK", tariff.Text("currency"));
        Assert.Equal(JsonValueKind.Null, tariff.GetProperty("now").ValueKind);
        Assert.Equal(0, tariff.GetProperty("windows").GetArrayLength());
    }

    [Fact]
    public async Task ListsTheStrategiesAndWhichIsSelected()
    {
        var client = await _host.StartAsync();

        var providers = await (await client.GetAsync("/api/v1/tariff/providers")).ReadAsync();

        Assert.Equal(["None", "Table"], providers.EnumerateArray().Select(provider => provider.Text("id")));
        Assert.True(providers[0].GetProperty("selected").GetBoolean());
    }

    [Fact]
    public async Task APutAppliesAtOnceAndTheTimelineSaysSo()
    {
        var client = await _host.StartAsync();

        var saved = await client.PutAsJsonAsync("/api/v1/tariff/settings", Night());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var tariff = await (await client.GetAsync("/api/v1/tariff")).ReadAsync();
        var now = tariff.GetProperty("now");

        Assert.Equal("Table", tariff.Text("provider"));
        Assert.Equal("high", now.Text("rate"));
        Assert.Equal(4.80, now.Number("pricePerKwh"));
        Assert.Equal(
            new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.FromHours(1)),
            now.GetProperty("until").GetDateTimeOffset());
        Assert.Equal("low", tariff.GetProperty("windows")[0].Text("rate"));
    }

    [Fact]
    public async Task TheStoredTableComesBackNormalized()
    {
        var client = await _host.StartAsync();
        await client.PutAsJsonAsync("/api/v1/tariff/settings", Night());

        var settings = await (await client.GetAsync("/api/v1/tariff/settings")).ReadAsync();
        var weekday = settings.GetProperty("settings").GetProperty("table").GetProperty("weekday");

        Assert.Equal("00:00", weekday[0].Text("start"));
        Assert.Equal("22:00", weekday[1].Text("start"));
    }

    [Fact]
    public async Task AnInvalidTableIsA400ListingEachProblemAndNothingIsStored()
    {
        var client = await _host.StartAsync();

        var response = await client.PutAsJsonAsync("/api/v1/tariff/settings", new
        {
            provider = "Table",
            settings = new
            {
                table = new
                {
                    weekday = new[] { new { start = "05:00", end = "07:00" }, new { start = "06:00", end = "08:00" } },
                    weekend = new[] { new { start = "22:00", end = "06:00" } },
                },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problems = (await response.ReadAsync()).GetProperty("problems").EnumerateArray().ToList();

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, problem => problem.Text("field") == "table.weekday" && problem.GetProperty("row").GetInt32() == 1);
        Assert.Contains(problems, problem => problem.Text("field") == "table.weekend" && problem.Text("message")!.Contains("Split a period"));
        Assert.Equal(0, _host.Tariff.Saves);
    }

    [Fact]
    public async Task AnUnknownStrategyIsA400OnTheProvider()
    {
        var client = await _host.StartAsync();

        var response = await client.PutAsJsonAsync("/api/v1/tariff/settings", new { provider = "Egd" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("provider", (await response.ReadAsync()).GetProperty("problems")[0].Text("field"));
    }

    [Fact]
    public async Task WarningsAreSavedThroughAndReturned()
    {
        var client = await _host.StartAsync();

        var response = await client.PutAsJsonAsync("/api/v1/tariff/settings", new
        {
            provider = "Table",
            lowPricePerKwh = 9m,
            highPricePerKwh = 4m,
            settings = new { table = new { weekday = new[] { new { start = "00:00", end = "06:00" } } } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var warning = Assert.Single((await response.ReadAsync()).GetProperty("warnings").EnumerateArray());
        Assert.Equal("lowPricePerKwh", warning.Text("field"));
    }

    [Fact]
    public async Task SelectingNoneKeepsTheTableForLater()
    {
        var client = await _host.StartAsync();
        await client.PutAsJsonAsync("/api/v1/tariff/settings", Night());

        var response = await client.PutAsJsonAsync("/api/v1/tariff/settings", new { provider = "None" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var settings = await (await client.GetAsync("/api/v1/tariff/settings")).ReadAsync();
        Assert.Equal("None", settings.Text("provider"));
        Assert.Equal(2, settings.GetProperty("settings").GetProperty("table").GetProperty("weekday").GetArrayLength());

        var tariff = await (await client.GetAsync("/api/v1/tariff")).ReadAsync();
        Assert.Equal(0, tariff.GetProperty("windows").GetArrayLength());
    }

    [Fact]
    public async Task AMissingProviderIsA400()
    {
        var client = await _host.StartAsync();

        var response = await client.PutAsJsonAsync("/api/v1/tariff/settings", new { lowPricePerKwh = 1m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EveryTariffRouteNeedsAKey()
    {
        await _host.StartAsync();
        var anonymous = _host.Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/tariff")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/tariff/settings")).StatusCode);
    }

    // -- Low tariff only (#274)

    [Fact]
    public async Task LowTariffOnlyIsOffByDefaultWithNoTariffToActOn()
    {
        var client = await _host.StartAsync();

        var flags = await (await client.GetAsync("/api/v1/charging/low-tariff-only")).ReadAsync();

        Assert.False(flags.GetProperty("fastNoBattery").GetBoolean());
        Assert.False(flags.GetProperty("solarGrid").GetBoolean());
        Assert.False(flags.GetProperty("tariffKnown").GetBoolean());
    }

    [Fact]
    public async Task APutSetsOnlyTheFlagsItNames()
    {
        var client = await _host.StartAsync();
        await client.PutAsJsonAsync("/api/v1/tariff/settings", Night());

        var first = await (await client.PutAsJsonAsync("/api/v1/charging/low-tariff-only", new { solarGrid = true })).ReadAsync();

        Assert.True(first.GetProperty("solarGrid").GetBoolean());
        Assert.False(first.GetProperty("fastNoBattery").GetBoolean());
        Assert.True(first.GetProperty("tariffKnown").GetBoolean());

        var second = await (await client.PutAsJsonAsync("/api/v1/charging/low-tariff-only", new { fastNoBattery = true })).ReadAsync();

        Assert.True(second.GetProperty("solarGrid").GetBoolean());
        Assert.True(second.GetProperty("fastNoBattery").GetBoolean());
        Assert.All(_host.LowTariff.Sources, source => Assert.Equal($"API ({ApiTestHost.KeyName})", source));
    }

    [Fact]
    public async Task AFlagSetWithNoTariffIsAcceptedAndTheAnswerSaysThereIsNothingToActOn()
    {
        var client = await _host.StartAsync();

        var response = await client.PutAsJsonAsync("/api/v1/charging/low-tariff-only", new { fastNoBattery = true });
        var flags = await response.ReadAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(flags.GetProperty("fastNoBattery").GetBoolean());
        Assert.False(flags.GetProperty("tariffKnown").GetBoolean());
    }

    [Fact]
    public async Task TheStatusSaysWhenAModeIsWaitingForLowTariff()
    {
        var client = await _host.StartAsync();
        var until = new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.FromHours(1));
        _host.Status.Set(Fixtures.Status() with
        {
            Mode = Gleanvolt.Core.Enums.ChargeControlMode.FastNoBattery,
            LowTariff = new Gleanvolt.Core.Models.LowTariffStatus(Waiting: true, Until: until, Ignored: false),
        });

        var status = await (await client.GetAsync("/api/v1/status")).ReadAsync();
        var low = status.GetProperty("lowTariff");

        Assert.True(low.GetProperty("waiting").GetBoolean());
        Assert.Equal(until, low.GetProperty("until").GetDateTimeOffset());
    }
}
