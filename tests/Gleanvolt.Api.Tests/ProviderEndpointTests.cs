using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Api.Tests;

/// <summary>GET /providers (#386): each kind of external provider and its session, behind the API key.</summary>
public sealed class ProviderEndpointTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task ListsEachKindWithItsSession()
    {
        _host.Providers.Statuses.Add(new ExternalProviderStatus(
            "tariff", "Tariff", "/providers/tariff", HasToken: true, ExternalProviderState.Connected,
            ProviderId: "spot", ProviderName: "Spot prices", ProviderVersion: "1.0",
            Since: Fixtures.Now, LastDataAt: Fixtures.Now, LastProblem: ["A problem."]));
        var client = await _host.StartAsync();

        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/providers"));
        var tariff = body.RootElement[0];

        Assert.Equal("tariff", tariff.GetProperty("kind").GetString());
        Assert.Equal("connected", tariff.GetProperty("state").GetString());
        Assert.Equal("spot", tariff.GetProperty("providerId").GetString());
        Assert.Equal("/providers/tariff", tariff.GetProperty("hubPath").GetString());
        Assert.Equal("A problem.", tariff.GetProperty("lastProblems")[0].GetString());
    }

    [Fact]
    public async Task NeedsTheKey()
    {
        await _host.StartAsync();

        var response = await _host.Anonymous().GetAsync("/api/v1/providers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
