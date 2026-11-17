using System.Net;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// Minimum battery SOC, SOC resume margin and min solar surplus over the API (issue #392): the runtime
/// values, read and changed by the same seams the web UI and the Home Assistant numbers use.
/// </summary>
public sealed class ChargingSettingsEndpointTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task AGetReadsTheRuntimeValues()
    {
        var client = await _host.StartAsync();
        _host.Settings.SetMinBatterySocFloorPercent(35, "Web UI");

        var answer = await (await client.GetAsync("/api/v1/charging/settings")).ReadAsync();

        Assert.Equal(35, answer.Number("minBatterySocPercent"));
        Assert.Equal(5, answer.Number("socResumeMarginPercent"));
        Assert.Equal(1400, answer.Number("minSolarSurplusWatts"));
    }

    [Fact]
    public async Task APutChangesOnlyTheFieldsGiven_NamingTheKey()
    {
        var client = await _host.StartAsync();

        var answer = await (await client.PutAsJsonAsync(
            "/api/v1/charging/settings", new { minSolarSurplusWatts = 2500 })).ReadAsync();

        Assert.Equal(2500, answer.Number("minSolarSurplusWatts"));
        Assert.Equal(20, answer.Number("minBatterySocPercent"));
        Assert.Equal(5, answer.Number("socResumeMarginPercent"));
        Assert.Equal(("MinSurplusWatts", 2500.0, $"API ({ApiTestHost.KeyName})"), Assert.Single(_host.Settings.Changes));
    }

    [Fact]
    public async Task APutCanChangeAllThree()
    {
        var client = await _host.StartAsync();

        var answer = await (await client.PutAsJsonAsync("/api/v1/charging/settings", new
        {
            minBatterySocPercent = 40,
            socResumeMarginPercent = 10,
            minSolarSurplusWatts = 0,
        })).ReadAsync();

        Assert.Equal(40, answer.Number("minBatterySocPercent"));
        Assert.Equal(10, answer.Number("socResumeMarginPercent"));
        Assert.Equal(0, answer.Number("minSolarSurplusWatts"));
        Assert.Equal(3, _host.Settings.Changes.Count);
    }

    [Fact]
    public async Task OutOfRangeIsA400ListingEveryProblem_AndNothingIsWritten()
    {
        var client = await _host.StartAsync();

        var response = await client.PutAsJsonAsync("/api/v1/charging/settings", new
        {
            minBatterySocPercent = 101,
            socResumeMarginPercent = 51,
            minSolarSurplusWatts = 2000, // valid, and still not written: the request is refused whole
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problems = (await response.ReadAsync()).GetProperty("problems").EnumerateArray().ToList();
        Assert.Equal(["minBatterySocPercent", "socResumeMarginPercent"], problems.Select(p => p.Text("field")));
        Assert.Contains("0–50", problems[1].Text("message"));
        Assert.Empty(_host.Settings.Changes);
    }

    [Theory]
    [InlineData("minBatterySocPercent", -1)]
    [InlineData("socResumeMarginPercent", -0.5)]
    [InlineData("minSolarSurplusWatts", 20001)]
    public async Task EachBoundIsChecked(string field, double value)
    {
        var client = await _host.StartAsync();

        var response = await client.PutAsJsonAsync(
            "/api/v1/charging/settings", new Dictionary<string, double> { [field] = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(field, (await response.ReadAsync()).GetProperty("problems")[0].Text("field"));
    }

    [Fact]
    public async Task TheStatusCarriesTheSettings_ChangedAtOnce()
    {
        var client = await _host.StartAsync();
        _host.Status.Set(Fixtures.Status());

        await client.PutAsJsonAsync("/api/v1/charging/settings", new { minBatterySocPercent = 55 });
        var status = await (await client.GetAsync("/api/v1/status")).ReadAsync();

        // No poll in between: the status reads the settings when it is served.
        Assert.Equal(55, status.GetProperty("settings").Number("minBatterySocPercent"));
        Assert.Equal(1400, status.GetProperty("settings").Number("minSolarSurplusWatts"));
    }

    [Fact]
    public async Task BothRoutesNeedAKey()
    {
        await _host.StartAsync();
        var anonymous = _host.Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/charging/settings")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync("/api/v1/charging/settings", new { minSolarSurplusWatts = 1 })).StatusCode);
        Assert.Empty(_host.Settings.Changes);
    }
}
