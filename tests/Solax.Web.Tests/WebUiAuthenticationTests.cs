using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Web.Auth;

namespace Solax.Web.Tests;

/// <summary>
/// End-to-end coverage of phase 2 (#47) through the real ASP.NET Core pipeline -- cookie
/// authentication, the <c>Web:RequireAuthentication</c> toggle, and the login/logout endpoints --
/// using <see cref="WebUiHost"/>'s own extension methods, exactly as Solax.Worker's Program.cs calls
/// them. Nothing here boots a Modbus client, a session store or the MQTT worker: TestServer runs
/// only the UI wiring, in-memory.
/// </summary>
public sealed class WebUiAuthenticationTests : IAsyncDisposable
{
    private const string Password = "s3cret";

    private WebApplication? _app;

    private async Task<HttpClient> StartAsync(WebOptions web)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(Options.Create(web));
        builder.Services.AddSingleton(new WebBuildInfo("test"));
        builder.Services.AddSingleton(new ChargeControlStatusHolder());
        builder.Services.AddSingleton(TimeProvider.System);
        // Phase 3 (#48): the dashboard now also injects these to drive the runtime controls. Fakes
        // are enough here -- this suite is about the auth pipeline, not control behaviour, which
        // DashboardPageTests already covers.
        builder.Services.AddSingleton<IChargeControlModeSelector>(new FakeChargeControlModeSelector());
        builder.Services.AddSingleton<IBatteryHoldSelector>(new FakeBatteryHoldSelector());
        builder.Services.AddSingleton<IForecastRuntimeSettings>(new FakeForecastRuntimeSettings());
        builder.Services.AddSolaxWebUi(web);

        _app = builder.Build();
        _app.MapSolaxWebUi(web);

        await _app.StartAsync();
        return _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static WebOptions Protected(string password = Password) => new()
    {
        Enabled = true,
        RequireAuthentication = true,
        PasswordHash = WebPasswordHasher.Hash(password),
    };

    // Scrapes the antiforgery cookie and token off the rendered login form -- the same pair a real
    // browser would carry from GET to POST -- rather than fabricating either, so this test fails if
    // the real markup ever stops matching what Program.cs's pipeline actually requires.
    private static async Task<(string Cookie, string Token)> GetLoginFormAsync(HttpClient client)
    {
        var response = await client.GetAsync("/login");
        var body = await response.Content.ReadAsStringAsync();

        var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.First().Split(';')[0]
            : throw new InvalidOperationException("Login page did not set an antiforgery cookie.");

        var token = Regex.Match(body, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"") is { Success: true } match
            ? match.Groups[1].Value
            : throw new InvalidOperationException("Login page did not carry an antiforgery token.");

        return (cookie, token);
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client, string antiforgeryCookie, string antiforgeryToken, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "login",
                ["__RequestVerificationToken"] = antiforgeryToken,
                ["Input.Password"] = password,
            }),
        };
        request.Headers.Add("Cookie", antiforgeryCookie);

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Redirects_an_anonymous_visitor_to_login()
    {
        var client = await StartAsync(Protected());

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Serves_the_login_page_itself_without_a_login()
    {
        var client = await StartAsync(Protected());

        var response = await client.GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_the_wrong_password_and_grants_no_session()
    {
        var client = await StartAsync(Protected());
        var (cookie, token) = await GetLoginFormAsync(client);

        var response = await PostLoginAsync(client, cookie, token, "not the password");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Wrong password", body);

        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        Assert.DoesNotContain(setCookies, c => c.StartsWith("solax_auth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Accepts_the_correct_password_and_the_session_then_reaches_the_dashboard()
    {
        var client = await StartAsync(Protected());
        var (cookie, token) = await GetLoginFormAsync(client);

        var loginResponse = await PostLoginAsync(client, cookie, token, Password);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        var authCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("solax_auth=", StringComparison.Ordinal))
            .Split(';')[0];

        using var dashboardRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        dashboardRequest.Headers.Add("Cookie", authCookie);
        var dashboardResponse = await client.SendAsync(dashboardRequest);

        Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var client = await StartAsync(Protected());
        var (cookie, token) = await GetLoginFormAsync(client);
        var loginResponse = await PostLoginAsync(client, cookie, token, Password);
        var authCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("solax_auth=", StringComparison.Ordinal))
            .Split(';')[0];

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/logout");
        logoutRequest.Headers.Add("Cookie", authCookie);
        var logoutResponse = await client.SendAsync(logoutRequest);

        // What a real browser would now hold: the cleared cookie /logout just issued, not the one
        // that was signed in with -- cookie auth has no server-side revocation, so still presenting
        // the original value would prove nothing about /logout actually working.
        var clearedCookie = logoutResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("solax_auth=", StringComparison.Ordinal))
            .Split(';')[0];

        using var dashboardRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        dashboardRequest.Headers.Add("Cookie", clearedCookie);
        var dashboardResponse = await client.SendAsync(dashboardRequest);

        Assert.Equal(HttpStatusCode.Redirect, dashboardResponse.StatusCode);
    }

    [Fact]
    public async Task Requires_no_login_at_all_once_RequireAuthentication_is_off()
    {
        var client = await StartAsync(new WebOptions { Enabled = true, RequireAuthentication = false, PasswordHash = "" });

        var dashboard = await client.GetAsync("/");
        var health = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
