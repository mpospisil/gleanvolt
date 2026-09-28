using System.Net;
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
/// The UI must not serve household telemetry, or the controls that drive the charger, to an
/// unauthenticated network client by default.
///
/// <para>Before this, the default was an open control surface: the UI is on, it binds every
/// interface, and a login was required only if somebody had already configured a password. The
/// warning in the startup log was accurate and protected nobody. These tests pin the replacement —
/// with no password, the only thing served is the page that sets one.</para>
/// </summary>
public sealed class WebUiSetupGateTests : IAsyncDisposable
{
    private WebApplication? _app;

    private async Task<HttpClient> StartAsync(WebOptions web, IWebPasswordStore? store = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(Options.Create(web));
        builder.Services.AddSingleton(new WebBuildInfo("test"));
        builder.Services.AddSingleton(new ChargeControlStatusHolder());
        builder.Services.AddSingleton(TimeProvider.System);
        var mode = new FakeChargeControlModeSelector();
        builder.Services.AddSingleton<IChargeControlModeSelector>(mode);
        builder.Services.AddSingleton<IChargeActions>(new FakeChargeActions(mode));
        builder.Services.AddSingleton<IBatteryHoldSelector>(new FakeBatteryHoldSelector());
        builder.Services.AddSingleton<IForecastRuntimeSettings>(new FakeForecastRuntimeSettings());
        builder.Services.AddSingleton<IServiceShutdown>(new FakeServiceShutdown());
        builder.Services.AddSingleton<ISecretStore>(new FakeSecretStore());
        builder.Services.AddSingleton<IVehicleTelemetry>(new VehicleStateHolder());
        builder.Services.AddSingleton<ISolarForecastService>(new FakeSolarForecastService());
        builder.Services.AddSingleton(EvInfo.Unknown);
        builder.Services.AddSingleton<IFastChargeSelector>(new FakeFastChargeSelector());
        builder.Services.AddSingleton(Sites.Home);
        builder.Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12)));

        if (store is not null)
        {
            builder.Services.AddSingleton(store);
        }

        builder.Services.AddGleanvoltWebUi(web);

        _app = builder.Build();
        _app.MapGleanvoltWebUi(web);

        await _app.StartAsync();

        // TestServer's handler does not follow redirects, which is what these assertions need: the
        // redirect *is* the behaviour under test. A real HttpClientHandler would try the network.
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

    private static WebOptions Unconfigured() => new() { Enabled = true };

    [Fact]
    public async Task With_no_password_the_dashboard_is_not_served_at_all()
    {
        using var client = await StartAsync(Unconfigured());

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(SetupGate.Path, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task With_no_password_the_setup_page_itself_answers()
    {
        using var client = await StartAsync(Unconfigured());

        var response = await client.GetAsync(SetupGate.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Set a password", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_password_set_in_this_process_lifts_the_gate_without_a_restart()
    {
        using var client = await StartAsync(Unconfigured(), new AcceptingPasswordStore());

        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).Headers is not null
            ? (await client.GetAsync("/")).StatusCode
            : HttpStatusCode.OK);

        // What the setup page does once it has persisted the hash.
        _app!.Services.GetRequiredService<WebPasswordState>().Set(WebPasswordHasher.Hash("s3cret-enough"));

        var after = await client.GetAsync("/");

        // No longer the setup page: the strict authorization policy takes over and asks for a sign-in.
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/login", after.Headers.Location?.OriginalString ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configured_password_never_shows_a_setup_page()
    {
        using var client = await StartAsync(new WebOptions
        {
            Enabled = true,
            PasswordHash = WebPasswordHasher.Hash("already-configured"),
        });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location?.OriginalString ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authentication_can_still_be_switched_off_on_purpose()
    {
        // The deliberate opt-out, for a network where an open UI is acceptable. It has to keep
        // working: the point of this change is that the *default* is closed, not that the choice is
        // taken away.
        using var client = await StartAsync(new WebOptions { Enabled = true, RequireAuthentication = false });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_api_is_not_redirected_into_the_setup_page()
    {
        // It carries its own key check and answers a program. A 307 to HTML would be a confusing
        // answer to a JSON client.
        using var client = await StartAsync(Unconfigured());

        var response = await client.GetAsync("/api/v1/health");

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public void A_host_that_cannot_persist_refuses_rather_than_pretending()
    {
        // A password that appeared to save and vanished on the next restart would reopen the UI with
        // nobody told, which is the failure this whole feature exists to prevent.
        Assert.False(new NoWebPasswordStore().Save(WebPasswordHasher.Hash("anything-at-all")));
    }

    private sealed class AcceptingPasswordStore : IWebPasswordStore
    {
        public bool Save(string passwordHash) => true;
    }
}
