using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// What the server answers once it has started to stop. Kestrel keeps accepting connections while it
/// drains, and a browser on a live page or a provider plugin reconnects the moment its connection is
/// closed; a reconnect that was let in was then waited out for the whole 30 s shutdown timeout.
/// </summary>
public class ShutdownGateTests
{
    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseGleanvoltShutdownGate();
        app.MapGet("/anything", () => "here");
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task WhileRunningEveryRequestGoesThrough()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync("/anything");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("here", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OnceStoppingEveryNewRequestIsTurnedAwayAndItsConnectionClosed()
    {
        await using var app = await StartAsync();

        app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        var response = await app.GetTestClient().GetAsync("/anything");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.ConnectionClose);
        Assert.NotNull(response.Headers.RetryAfter);
    }
}
