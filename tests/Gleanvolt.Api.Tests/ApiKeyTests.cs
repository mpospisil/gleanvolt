using System.Net;
using System.Net.Http.Headers;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// The key check, which is the whole of this surface's authentication. It has to hold on every route
/// including the document, and the key's <em>name</em> has to reach the actions — an anonymous write
/// to a charger is exactly what this exists to prevent.
/// </summary>
public sealed class ApiKeyTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Rejects_a_call_with_no_key()
    {
        await _host.StartAsync();
        var response = await _host.Anonymous().GetAsync("/api/v1/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Rejects_a_call_with_the_wrong_key()
    {
        await _host.StartAsync();
        var client = _host.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-key");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/status")).StatusCode);
    }

    [Fact]
    public async Task Rejects_a_key_presented_without_the_bearer_scheme()
    {
        await _host.StartAsync();
        var client = _host.Anonymous();
        client.DefaultRequestHeaders.Add("Authorization", ApiTestHost.Key);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/status")).StatusCode);
    }

    [Fact]
    public async Task Accepts_a_configured_key()
    {
        var client = await _host.StartAsync();
        _host.Status.Set(Fixtures.Status());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/status")).StatusCode);
    }

    [Fact]
    public async Task Lets_the_index_and_the_document_be_read_without_one()
    {
        await _host.StartAsync();
        var anonymous = _host.Anonymous();

        // The two endpoints a browser can actually reach, and the two that carry nothing to act on:
        // what this is, and the shape a client is generated from.
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(GleanvoltApi.DocumentPath)).StatusCode);
    }

    [Fact]
    public async Task Names_the_key_as_the_source_of_an_action()
    {
        var client = await _host.StartAsync();

        await client.PostAsJsonAsync("/api/v1/charging/start", new { mode = "solar" });

        // Not "API": which client started a charge is the question a log is read to answer.
        Assert.Equal($"API ({ApiTestHost.KeyName})", Assert.Single(_host.Actions.Starts).Source);
    }

    [Fact]
    public void Refuses_to_start_when_switched_on_with_no_key()
    {
        var api = new ApiOptions { Enabled = true };

        var error = Assert.Throws<InvalidOperationException>(api.ValidateKeyConfig);
        Assert.Contains("Api__Keys", error.Message);
    }

    [Fact]
    public void Accepts_being_switched_off_with_no_key()
    {
        new ApiOptions().ValidateKeyConfig();
        new ApiOptions { Enabled = false }.ValidateKeyConfig();
    }

    [Fact]
    public async Task Maps_nothing_at_all_when_disabled()
    {
        await _host.StartAsync(new ApiOptions { Enabled = false });

        // Not 401, and not an index either: a disabled API is not a locked door, it is no door. Nothing
        // announces that a control surface exists here.
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Anonymous().GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Anonymous().GetAsync("/api/v1")).StatusCode);
    }
}
