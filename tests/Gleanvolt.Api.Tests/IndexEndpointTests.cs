using System.Net;
using System.Text.Json;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// The base URL, which is where anybody handed this API looks first — and which, before it was a
/// route, answered with an empty 404 while every real endpoint answered 401, because a browser cannot
/// send an <c>Authorization</c> header. A working API that looks broken is a bug in the API.
/// </summary>
public sealed class IndexEndpointTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Says_what_this_is_and_how_to_authenticate_without_a_key()
    {
        await _host.StartAsync();

        var response = await _host.Anonymous().GetAsync("/api/v1");
        var body = await response.ReadAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Gleanvolt", body.Text("name"));
        Assert.Equal("v1", body.Text("version"));
        Assert.Equal("1.2.3-test", body.Text("build"));
        Assert.Equal(GleanvoltApi.DocumentPath, body.Text("documentation"));
        Assert.Contains("Authorization: Bearer", body.Text("authentication"));
    }

    [Fact]
    public async Task Answers_the_same_with_or_without_a_trailing_slash()
    {
        await _host.StartAsync();

        // The form a person types is the one with the slash, because that is what a base URL looks like.
        Assert.Equal(HttpStatusCode.OK, (await _host.Anonymous().GetAsync("/api/v1/")).StatusCode);
    }

    [Fact]
    public async Task Lists_the_operations_this_build_serves()
    {
        var client = await _host.StartAsync();

        var operations = (await (await client.GetAsync("/api/v1")).ReadAsync()).GetProperty("operations");

        var paths = operations.EnumerateArray().Select(o => o.Text("path")).ToList();

        Assert.Contains("/api/v1/status", paths);
        Assert.Contains("/api/v1/plans/targeted/preview", paths);
        Assert.Contains("/api/v1/charging/start", paths);

        // Read from the routing table rather than a hand-kept list, so it cannot go stale: the index
        // and the document describe the same build.
        Assert.Contains("/api/{documentName}/openapi.json", paths);

        var status = operations.EnumerateArray().First(o => o.Text("path") == "/api/v1/status");
        Assert.Equal("GET", status.Text("method"));
        Assert.Equal("The live state of the site", status.Text("summary"));

        var start = operations.EnumerateArray().First(o => o.Text("path") == "/api/v1/charging/start");
        Assert.Equal("POST", start.Text("method"));
    }

    [Fact]
    public async Task Carries_nothing_an_unauthenticated_caller_could_act_on()
    {
        await _host.StartAsync();
        _host.Status.Set(Fixtures.Status());

        var body = await (await _host.Anonymous().GetAsync("/api/v1")).ReadAsync();

        // Names of operations and where the document is -- no telemetry, and nothing about the site.
        Assert.Equal(
            new[] { "authentication", "build", "documentation", "name", "operations", "version" },
            body.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.DoesNotContain("batterySoc", JsonSerializer.Serialize(body), StringComparison.OrdinalIgnoreCase);
    }
}
