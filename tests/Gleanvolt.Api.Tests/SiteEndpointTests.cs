using System.Net;
using System.Text.Json;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// <c>/site</c> — what this controller is connected to (issue #111). Every other endpoint reports what
/// it is <i>doing</i>, and two installations answer those identically; this is the one that says whose
/// answers they are.
/// </summary>
public sealed class SiteEndpointTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<JsonElement> GetAsync(string url = "/api/v1/site")
    {
        var client = await _host.StartAsync();
        return await (await client.GetAsync(url)).ReadAsync();
    }

    [Fact]
    public async Task Reports_the_system_it_speaks_for()
    {
        var site = await GetAsync();

        Assert.Equal("home-roof", site.Text("id"));
        Assert.Equal("Home Roof", site.Text("name"));
        Assert.Equal("Krásného 12, Praha", site.Text("address"));
        Assert.Equal(50.0755, site.Number("latitude"), 4);
        Assert.Equal(14.4378, site.Number("longitude"), 4);
    }

    [Fact]
    public async Task Reports_what_the_array_is()
    {
        var site = await GetAsync();

        Assert.Equal(172, site.Number("azimuthDegrees"));
        Assert.Equal(35, site.Number("tiltDegrees"));
        Assert.Equal(9.2, site.Number("capacityKwp"), 2);
        Assert.Equal(8, site.Number("inverterCapacityKw"));
        Assert.Equal(0.9, site.Number("lossFactor"), 2);
        Assert.Equal("2024-05-01", site.Text("installDate"));
    }

    [Fact]
    public async Task Reports_what_it_is_made_of()
    {
        var site = await GetAsync();

        var inverter = site.GetProperty("inverter");
        Assert.Equal("SolaX X3-HYB-G4 PRO", inverter.Text("model"));
        Assert.Equal("192.168.2.10", inverter.Text("host"));
        Assert.Equal(502, inverter.Number("port"));

        var charger = Assert.Single(site.GetProperty("chargers").EnumerateArray().ToList());
        Assert.Equal("wallbox", charger.Text("id"));
        Assert.Equal("Garage wallbox", charger.Text("name"));
        Assert.Equal("SolaX X3-HAC", charger.Text("model"));
        Assert.Equal("192.168.2.6", charger.Text("host"));
        Assert.Equal(1, charger.Number("unitId"));
    }

    [Fact]
    public async Task Needs_a_key_like_everything_else()
    {
        // The configuration of somebody's house, down to the addresses of the two devices that can be
        // written to. The index is the endpoint that answers without a key; this is not it.
        var client = await _host.StartAsync();
        client.DefaultRequestHeaders.Remove("Authorization");

        var response = await client.GetAsync("/api/v1/site");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_says_which_installation_is_answering()
    {
        // A monitor polling several controllers gets otherwise identical payloads, and "is it working?"
        // is only useful once you know whose.
        var health = await GetAsync("/api/v1/health");

        Assert.Equal("home-roof", health.Text("systemId"));
        Assert.Equal("Home Roof", health.Text("systemName"));
    }
}
