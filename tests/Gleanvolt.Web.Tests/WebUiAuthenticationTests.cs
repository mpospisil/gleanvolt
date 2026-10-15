using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// End-to-end coverage of phase 2 (#47) through the real ASP.NET Core pipeline -- cookie
/// authentication, the <c>Web:RequireAuthentication</c> toggle, and the login/logout endpoints --
/// using <see cref="WebUiHost"/>'s own extension methods, exactly as Gleanvolt.Worker's Program.cs calls
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
        var mode = new FakeChargeControlModeSelector();
        builder.Services.AddSingleton<IChargeControlModeSelector>(mode);
        builder.Services.AddSingleton<IChargeActions>(new FakeChargeActions(mode));
        builder.Services.AddSingleton<IBatteryHoldSelector>(new FakeBatteryHoldSelector());
        builder.Services.AddSingleton<IForecastRuntimeSettings>(new FakeForecastRuntimeSettings());
        builder.Services.AddSingleton<IServiceShutdown>(new FakeServiceShutdown());
        builder.Services.AddSingleton<ISecretStore>(new FakeSecretStore());

        // Vehicle telemetry (#73): hand-registered here for the same reason WebBuildInfo is -- this test
        // builds a minimal host rather than calling AddGleanvolt, so it owns the UI's dependencies.
        var vehicle = new VehicleStateHolder();
        builder.Services.AddSingleton<IVehicleTelemetry>(vehicle);

        // The dashboard's whole-day forecast tile reads this. Holding nothing is the install before its
        // first refresh, which is all an auth suite needs.
        builder.Services.AddSingleton<ISolarForecastService>(new FakeSolarForecastService());

        // The car itself (#124), which the dashboard's vehicle card now names whether or not anything
        // reports on it (#140). Unknown is the install that has never described one.
        builder.Services.AddSingleton(EvInfo.Unknown);
        builder.Services.AddSingleton<IFastChargeSelector>(new FakeFastChargeSelector());

        // The installation (#111): the layout names it in the header, so every page needs it -- the
        // sign-in page included, which is what two of these tests are about.
        builder.Services.AddSingleton(Sites.Home);
        builder.Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12)));
        builder.Services.AddGleanvoltWebUi(web);

        _app = builder.Build();
        _app.MapGleanvoltWebUi(web);

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

    [Fact]
    public async Task The_page_says_which_installation_it_is()
    {
        // Two tabs onto two roofs is the case this exists for: the header carries the system's name,
        // and so does the browser tab, because "Gleanvolt" is the one thing both tabs already agree on.
        //
        // Explicitly opted out of authentication rather than merely unconfigured: since the setup gate
        // an unconfigured UI serves nothing but its setup page, and this test is about what a served
        // page says, not about whether one is served.
        var client = await StartAsync(new WebOptions { Enabled = true, RequireAuthentication = false });

        var html = await (await client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("Home Roof", html);
        Assert.Contains("<title>Overview — Home Roof</title>", html);
    }

    [Fact]
    public async Task The_sign_in_card_names_the_installation_and_offers_no_links_past_it()
    {
        // The sign-in card (#255) is where knowing which roof you are signing in to matters, so it
        // carries the name itself. The header's links are left off: with sign-in required every one of
        // them would only have bounced back here.
        var client = await StartAsync(Protected());

        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();

        Assert.Contains("<p class=\"sign-in-site\">Home Roof</p>", html);
        Assert.DoesNotContain("href=\"/charging-plan\"", html);
        Assert.DoesNotContain("id=\"menu-toggle\"", html);
    }

    [Fact]
    public async Task Keeps_the_links_when_sign_in_is_off()
    {
        // The other half of the rule above: with nobody to sign in, the links are the audience's.
        var client = await StartAsync(new WebOptions { Enabled = true, RequireAuthentication = false });

        var html = await (await client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("href=\"/charging-plan\"", html);
        Assert.Contains("id=\"menu-toggle\"", html);
    }

    [Fact]
    public async Task Names_seven_links_with_settings_in_place_of_pv_system_and_health()
    {
        // #255's navigation: Overview for Dashboard, and the two pages an owner opens least behind one
        // Settings link.
        var client = await StartAsync(new WebOptions { Enabled = true, RequireAuthentication = false });

        var html = await (await client.GetAsync("/")).Content.ReadAsStringAsync();

        var links = System.Text.RegularExpressions.Regex.Matches(html, "<nav>(.*?)</nav>", System.Text.RegularExpressions.RegexOptions.Singleline)[0].Groups[1].Value;
        Assert.Equal(
            ["Overview", "Charging plan", "Sessions", "Energy", "Forecast", "Car", "Settings"],
            System.Text.RegularExpressions.Regex.Matches(links, ">([^<]+)</a>").Select(m => m.Groups[1].Value));
    }

    [Fact]
    public async Task Ties_the_wrong_password_message_to_the_field()
    {
        // A screen reader reads the error with the field it is about, not somewhere above the form.
        var client = await StartAsync(Protected());
        var (cookie, token) = await GetLoginFormAsync(client);

        var body = await (await PostLoginAsync(client, cookie, token, "not the password")).Content.ReadAsStringAsync();

        Assert.Matches("<input[^>]*aria-describedby=\"password-error\"", body);
        Assert.Matches("<input[^>]*aria-invalid=\"true\"", body);
        Assert.Contains("id=\"password-error\">Wrong password.</p>", body);
    }

    [Fact]
    public async Task Serves_no_dashboard_at_all_with_no_authentication_configured()
    {
        // The out-of-the-box deployment, through the real pipeline: no password, no
        // RequireAuthentication, nothing. This used to assert that a dashboard was served, which is
        // exactly the open control surface a winget-pkgs review objected to -- anyone who could reach
        // the port could read the household's history and drive the charger. It now asserts the
        // opposite, and WebUiSetupGateTests covers the setup page that replaces it.
        var client = await StartAsync(new WebOptions { Enabled = true });

        var dashboard = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
        Assert.Equal(SetupGate.Path, dashboard.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Requires_a_login_when_only_a_password_hash_is_configured()
    {
        // The advanced path, and the half of the inference that matters for security: setting the
        // hash alone -- with RequireAuthentication left unset -- has to actually turn anyone away.
        var client = await StartAsync(new WebOptions { Enabled = true, PasswordHash = WebPasswordHasher.Hash(Password) });

        var dashboard = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
        Assert.Contains("/login", dashboard.Headers.Location?.OriginalString ?? "");
    }
}
