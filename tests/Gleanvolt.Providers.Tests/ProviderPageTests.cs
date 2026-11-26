using System.Net;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Gleanvolt.Providers.Tests;

/// <summary>
/// A provider's own page (#410): the page URL in the hello, the ticket Gleanvolt signs with the provider
/// token, Gleanvolt's redirect that adds it, and the SDK's guard that admits only a browser Gleanvolt sent.
/// </summary>
public sealed class ProviderPageTests : IAsyncLifetime
{
    private const string Token = "gvp_the-shared-provider-token";

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly TestHost _host = new();

    public Task InitializeAsync() => _host.StartAsync();

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public void ATicketIsAcceptedUntilItExpires()
    {
        var ticket = ProviderPage.CreateTicket(Token, "volkswagen", Now.AddMinutes(2));

        Assert.True(ProviderPage.VerifyTicket(ticket, Token, "volkswagen", Now));
        Assert.True(ProviderPage.VerifyTicket(ticket, Token, "volkswagen", Now.AddMinutes(2).AddSeconds(-1)));
        Assert.False(ProviderPage.VerifyTicket(ticket, Token, "volkswagen", Now.AddMinutes(2)));
    }

    [Fact]
    public void AForgedTicketIsRefused()
    {
        var ticket = ProviderPage.CreateTicket(Token, "volkswagen", Now.AddMinutes(2));
        var later = Now.AddHours(1).ToUnixTimeSeconds() + ticket[ticket.IndexOf('.', StringComparison.Ordinal)..];

        Assert.False(ProviderPage.VerifyTicket(ProviderPage.CreateTicket("gvp_another-token", "volkswagen", Now.AddMinutes(2)), Token, "volkswagen", Now));
        Assert.False(ProviderPage.VerifyTicket(later, Token, "volkswagen", Now), "a moved expiry no longer matches its signature");
        Assert.False(ProviderPage.VerifyTicket(ticket[..^2] + "AA", Token, "volkswagen", Now));
        Assert.False(ProviderPage.VerifyTicket("not-a-ticket", Token, "volkswagen", Now));
        Assert.False(ProviderPage.VerifyTicket(null, Token, "volkswagen", Now));
    }

    [Fact]
    public void ATicketOpensOnlyItsOwnProvidersPage()
    {
        var ticket = ProviderPage.CreateTicket(Token, "volkswagen", Now.AddMinutes(2));

        Assert.False(ProviderPage.VerifyTicket(ticket, Token, "vehicle-sample", Now));
    }

    [Fact]
    public void AnExpiryTooFarAheadIsNotBelieved()
    {
        var ticket = ProviderPage.CreateTicket(Token, "volkswagen", Now.AddDays(30));

        Assert.False(ProviderPage.VerifyTicket(ticket, Token, "volkswagen", Now));
    }

    [Fact]
    public async Task ThePageUrlInTheHelloReachesTheStatus()
    {
        await using var connection = await _host.ConnectAsync();

        var welcome = await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello() with { PageUrl = new Uri("http://192.168.2.7:8095/") });

        Assert.True(welcome.Accepted);
        var status = _host.Registry.Find(EchoHub.Kind)!;
        Assert.Equal(new Uri("http://192.168.2.7:8095/"), status.PageUrl);
        Assert.True(status.HasPage);
        Assert.Equal($"/external/{EchoHub.Kind}/page", status.PagePath);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    public async Task APageThatIsNotHttpIsRefused(string page)
    {
        await using var connection = await _host.ConnectAsync();

        var welcome = await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello() with { PageUrl = new Uri(page) });

        Assert.False(welcome.Accepted);
        Assert.Equal(RefusalCodes.Invalid, welcome.ReasonCode);
    }

    [Fact]
    public async Task GleanvoltsRedirectAddsAFreshTicketTheThemeAndTheCulture()
    {
        await using var connection = await _host.ConnectAsync();
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello() with { PageUrl = new Uri("http://192.168.2.7:8095/?view=car") });

        var response = await _host.Server.CreateClient().GetAsync($"/external/{EchoHub.Kind}/page");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("192.168.2.7", location.Host);
        Assert.Equal(8095, location.Port);

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("car", query["view"].ToString());
        Assert.Equal("auto", query[ProviderPage.ThemeParameter].ToString());
        Assert.True(query.ContainsKey(ProviderPage.CultureParameter));
        Assert.True(ProviderPage.VerifyTicket(query[ProviderPage.TicketParameter], _host.Token, "echo-one", _host.Time.GetUtcNow()));
        Assert.False(ProviderPage.VerifyTicket(query[ProviderPage.TicketParameter], _host.Token, "echo-one", _host.Time.GetUtcNow() + ProviderPage.TicketLifetime));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task APluginOnThisMachineIsOpenedAtTheNameTheBrowserUsed(string from)
    {
        // A plugin container on the host's network (#426): it says localhost, which to the browser is the
        // viewer's own machine.
        await using var connection = await _host.ConnectAsync(from: IPAddress.Parse(from));
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello() with { PageUrl = new Uri("http://localhost:8096/?view=key") });

        var location = await RedirectAsync("gleanvolt.local:8090");

        Assert.Equal("gleanvolt.local", location.Host);
        Assert.Equal(8096, location.Port);
        Assert.Contains("view=key", location.Query);
        Assert.Equal(new Uri("http://localhost:8096/?view=key"), _host.Registry.Find(EchoHub.Kind)!.PageUrl);

        Assert.Equal("192.168.2.7", (await RedirectAsync("192.168.2.7:8090")).Host);
    }

    [Fact]
    public async Task ALocalhostPageFromAnotherMachineIsLeftAsItSaid()
    {
        // A plugin elsewhere that says localhost means the developer's own browser.
        await using var connection = await _host.ConnectAsync(from: IPAddress.Parse("192.168.2.30"));
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello() with { PageUrl = new Uri("http://localhost:8096/") });

        Assert.Equal("localhost", (await RedirectAsync("gleanvolt.local:8090")).Host);
    }

    [Fact]
    public async Task APageAtAnAddressIsLeftAsItSaidEvenFromThisMachine()
    {
        await using var connection = await _host.ConnectAsync(from: IPAddress.Loopback);
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello() with { PageUrl = new Uri("http://192.168.2.7:8096/") });

        Assert.Equal("192.168.2.7", (await RedirectAsync("gleanvolt.local:8090")).Host);
    }

    [Theory]
    [InlineData("http://localhost:8090", null, "http://*:8090 https://*:8090")]
    [InlineData("http://127.0.0.1:8090", null, "http://*:8090 https://*:8090")]
    [InlineData("http://192.168.2.7:8090", null, "http://192.168.2.7:8090")]
    [InlineData("http://localhost:8090", " http://gleanvolt.local:8090 ", "http://gleanvolt.local:8090")]
    public void ThePageMayBeFramedByGleanvoltAtTheNamesItCanHave(string endpoint, string? configured, string expected) =>
        Assert.Equal(expected, ProviderPageFraming.Sources(new Uri(endpoint), configured));

    private async Task<Uri> RedirectAsync(string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/external/{EchoHub.Kind}/page");
        request.Headers.Host = host;
        var response = await _host.Server.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!;
    }

    [Fact]
    public async Task WithoutAProviderWithAPageThereIsNothingToRedirectTo()
    {
        await using var connection = await _host.ConnectAsync();
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello());

        var response = await _host.Server.CreateClient().GetAsync($"/external/{EchoHub.Kind}/page");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ThePluginPageAnswers403WithoutATicket()
    {
        await using var plugin = await PluginAsync();

        var response = await plugin.GetTestClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AValidTicketOpensThePageAndLeavesACookieForTheNextRequest()
    {
        await using var plugin = await PluginAsync();
        var ticket = ProviderPage.CreateTicket(Token, "plugin", Now.AddMinutes(2));
        var client = plugin.GetTestClient();

        var opened = await client.GetAsync($"/?{ProviderPage.TicketParameter}={Uri.EscapeDataString(ticket)}");

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var cookie = Assert.Single(opened.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("gleanvolt-page-plugin=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        using var reload = new HttpRequestMessage(HttpMethod.Get, "/");
        reload.Headers.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(reload)).StatusCode);
    }

    [Fact]
    public async Task AnExpiredOrForgedTicketIsRefused()
    {
        await using var plugin = await PluginAsync();
        var client = plugin.GetTestClient();
        var expired = ProviderPage.CreateTicket(Token, "plugin", Now.AddSeconds(-1));
        var forged = ProviderPage.CreateTicket("gvp_guessed", "plugin", Now.AddMinutes(2));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/?{ProviderPage.TicketParameter}={Uri.EscapeDataString(expired)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/?{ProviderPage.TicketParameter}={Uri.EscapeDataString(forged)}")).StatusCode);
    }

    [Fact]
    public async Task ATicketIsNotACookie()
    {
        await using var plugin = await PluginAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", "gleanvolt-page-plugin=" + ProviderPage.CreateTicket(Token, "plugin", Now.AddMinutes(2)));

        Assert.Equal(HttpStatusCode.Forbidden, (await plugin.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task StaticFilesAndTheBlazorCircuitNeedNoTicket()
    {
        await using var plugin = await PluginAsync();
        var client = plugin.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/app.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_blazor/negotiate")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/sign-in.json", null)).StatusCode);
    }

    [Fact]
    public async Task UnpairedTheRootShowsTheWaitingView()
    {
        await using var plugin = await PluginAsync(token: "");

        var response = await plugin.GetTestClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(plugin.Services.GetRequiredService<ProviderPairing>().Status.Code, body, StringComparison.Ordinal);
        Assert.DoesNotContain("the page", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnpairedEverythingElseIsRefused()
    {
        await using var plugin = await PluginAsync(token: "");
        var client = plugin.GetTestClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/recording")).StatusCode);
    }

    [Fact]
    public async Task UnpairedTheFrameworkIsStillServed()
    {
        await using var plugin = await PluginAsync(token: "");

        var response = await plugin.GetTestClient().GetAsync("/_framework/blazor.web.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("the page", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AStaleCookieWhileUnpairedSeesTheWaitingView()
    {
        await using var plugin = await PluginAsync(token: "");
        var credentials = plugin.Services.GetRequiredService<ProviderCredentials>();
        credentials.Save(new Uri("http://gleanvolt.test"), Token);
        var client = plugin.GetTestClient();
        var ticket = ProviderPage.CreateTicket(Token, "plugin", Now.AddMinutes(2));
        var opened = await client.GetAsync($"/?{ProviderPage.TicketParameter}={Uri.EscapeDataString(ticket)}");
        var cookie = Assert.Single(opened.Headers.GetValues("Set-Cookie")).Split(';')[0];

        credentials.ForgetPaired();

        using var reload = new HttpRequestMessage(HttpMethod.Get, "/");
        reload.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(reload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("the page", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var elsewhere = new HttpRequestMessage(HttpMethod.Get, "/recording");
        elsewhere.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(elsewhere)).StatusCode);
    }

    [Fact]
    public async Task OncePairedATicketSignedWithTheNewTokenOpensThePage()
    {
        await using var plugin = await PluginAsync(token: "");
        var client = plugin.GetTestClient();
        var ticket = ProviderPage.CreateTicket(Token, "plugin", Now.AddMinutes(2));
        var url = $"/?{ProviderPage.TicketParameter}={Uri.EscapeDataString(ticket)}";
        Assert.DoesNotContain("the page", await (await client.GetAsync(url)).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        plugin.Services.GetRequiredService<ProviderCredentials>().Save(new Uri("http://gleanvolt.test"), Token);

        Assert.Equal("the page", await (await client.GetAsync(url)).Content.ReadAsStringAsync());
    }

    // A plugin's web host with the SDK's guard in front of an endpoint that answers every path.
    private static async Task<WebApplication> PluginAsync(string token = Token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        builder.Services.Configure<ProviderOptions>(options =>
        {
            options.Token = token;
            options.ProviderId = "plugin";
            options.Name = "Plugin";
        });
        builder.Services.AddOptions<ProviderPageOptions>();
        builder.Services.AddSingleton<ProviderCredentials>();
        builder.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ProviderOptions>>();
            return new ProviderPairing(options.Value, provider.GetRequiredService<ProviderCredentials>(),
                new GleanvoltFinder(options), null, null, TimeProvider.System, NullLogger<ProviderPairing>.Instance, "/hub");
        });

        var app = builder.Build();
        app.UseGleanvoltProviderPage();
        app.Run(context => context.Response.WriteAsync("the page"));
        await app.StartAsync();
        return app;
    }
}
