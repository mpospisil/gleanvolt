using Gleanvolt.Providers.Client;
using Microsoft.Extensions.Configuration;

using System.Net;
using System.Runtime.CompilerServices;
using Gleanvolt.Chargers;
using Gleanvolt.Chargers.Components;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;
using Gleanvolt.Providers.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Gleanvolt.Providers.Tests;

/// <summary>
/// Gleanvolt.Chargers.Hosting (#443), what every charger plugin shares: the address, the settings, the search's
/// networks, the recording; and the testing package's fake Modbus charger.
/// </summary>
public sealed class ChargersHostingTests
{
    [Theory]
    [InlineData("192.168.1.6", 502, "192.168.1.6:502")]
    [InlineData(" charger.local:8080/ ", 80, "charger.local:8080")]
    [InlineData("http://192.168.1.30", 80, "192.168.1.30:80")]
    [InlineData("192.168.1.6:80", 502, "192.168.1.6:80")]
    [InlineData("http://192.168.1.6/rpc", 80, null)]
    [InlineData("ftp://192.168.1.6", 80, null)]
    [InlineData("http://admin:pw@192.168.1.6", 80, null)]
    [InlineData("", 80, null)]
    public void An_address_is_a_host_with_an_optional_port(string text, int defaultPort, string? expected)
    {
        Assert.Equal(expected, DeviceAddress.Parse(text, defaultPort)?.ToString());
    }

    [Fact]
    public void A_saved_setting_wins_over_the_configuration_and_survives_a_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gleanvolt-chargers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Solax:Host"] = "192.168.1.6" })
                .Build();
            var plugin = new PluginInfo("solax-charger", "SolaX", "Solax", 8098, 502);
            var settings = new PluginSettings(new ProviderSecretStore(directory), configuration, plugin);

            Assert.Equal(SettingSource.Configuration, settings.Address.Source);
            settings.Address.Save("192.168.1.7");

            var restarted = new PluginSettings(new ProviderSecretStore(directory), configuration, plugin);
            Assert.Equal("192.168.1.7", restarted.Address.Current);
            Assert.Equal(SettingSource.Page, restarted.Address.Source);

            restarted.Address.Clear();
            Assert.Equal("192.168.1.6", restarted.Address.Current);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("192.168.1.0/24", 254)]
    [InlineData("10.0.4.0/22", 1022)]
    [InlineData("10.0.0.0/21", -1)]
    [InlineData("8.8.8.0/24", -1)]
    [InlineData("nonsense", -1)]
    public void The_search_takes_a_private_network_no_larger_than_a_22(string network, int hosts)
    {
        Assert.Equal(hosts, LanScan.Hosts(network)?.Count ?? -1);
    }

    [Fact]
    public async Task The_recording_keeps_the_path_and_never_the_authorization_header()
    {
        var recorder = new DeviceRecorder(TimeProvider.System);
        recorder.Start();
        using var http = new HttpClient(new RecordingHandler(recorder) { InnerHandler = new Answers() });
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://192.168.1.50/rpc/Enum.GetStatus?owner=%22service:0%22&role=%22work_state%22");
        request.Headers.Authorization = new("Digest", "secret-response");

        await http.SendAsync(request);

        var json = recorder.ToJson("shelly-topac", "test", null);
        Assert.Contains("GET /rpc/Enum.GetStatus", json);
        Assert.Contains("charger_free", json);
        Assert.DoesNotContain("secret-response", json);
    }

    [Fact]
    public async Task Nothing_is_recorded_until_a_recording_starts()
    {
        var recorder = new DeviceRecorder(TimeProvider.System);
        using var http = new HttpClient(new RecordingHandler(recorder) { InnerHandler = new Answers() });

        await http.GetAsync("http://192.168.1.50/rpc/Enum.GetStatus?owner=%22service:0%22&role=%22work_state%22");

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task The_fake_Modbus_charger_answers_its_map_refuses_the_rest_and_keeps_every_write()
    {
        await using var charger = new FakeModbusServer();
        charger.Input[0x001D] = 2;
        var recorder = new DeviceRecorder(TimeProvider.System);
        var settings = new PluginSettings(
            new Gleanvolt.Providers.Client.ProviderSecretStore(Path.Combine(Path.GetTempPath(), "gleanvolt-hosting-" + Guid.NewGuid().ToString("N"))),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new PluginInfo("test", "Test", "Test", 8110, 502));
        await using var device = new ModbusDevice(
            settings.Address, settings.Get("unitId"), 502, 1, TimeSpan.Zero, recorder,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModbusDevice>.Instance);
        settings.Address.Save(charger.Address.ToString());

        var runMode = await device.UseAsync(client => client.ReadInputRegistersAsync(0x001D, 1), CancellationToken.None);
        await device.UseAsync(async client =>
        {
            await client.WriteSingleRegisterAsync(0x0628, 1000);
            return true;
        }, CancellationToken.None);

        Assert.Equal([(ushort)2], runMode);
        Assert.Equal(["0x0628=1000"], charger.Writes);
        await Assert.ThrowsAnyAsync<Exception>(() => device.UseAsync(client => client.ReadInputRegistersAsync(0x0100, 1), CancellationToken.None));
    }

    [Fact]
    public async Task APluginStartsWithoutAToken()
    {
        await using var plugin = await PluginApp.StartAsync();

        var response = await plugin.Client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var status = plugin.App.Services.GetRequiredService<ProviderPairing>().Status;
        Assert.NotEmpty(status.Code);
        Assert.Contains(WebUtility.HtmlEncode(status.Code), body, StringComparison.Ordinal);
        Assert.Contains("Pairing code", body, StringComparison.Ordinal);
        Assert.Contains("""<meta http-equiv="refresh" content="10">""", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AMachineWhoseAddressesCannotBeListedShowsNone() =>
        Assert.Empty(PluginHost.AddressesOrNone(() => throw new System.Net.NetworkInformation.NetworkInformationException()));

    [Theory]
    [InlineData("cs")]
    [InlineData("cs-CZ")]
    public async Task TheWaitingViewIsInCzechForCzech(string culture)
    {
        await using var plugin = await PluginApp.StartAsync();

        var body = await plugin.Client.GetStringAsync($"/?{ProviderPage.CultureParameter}={culture}");

        Assert.Contains("Kód pro spárování", body, StringComparison.Ordinal);
        Assert.Contains("""<html lang="cs" """, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownLanguageInTheQueryIsEnglishNotTheCookies()
    {
        await using var plugin = await PluginApp.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/?{ProviderPage.CultureParameter}=de");
        request.Headers.Add("Cookie", ".AspNetCore.Culture=c%3Dcs%7Cuic%3Dcs");

        var body = await (await plugin.Client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("Pairing code", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Kód pro spárování", body, StringComparison.Ordinal);
        Assert.Contains("""<html lang="en" """, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWaitingViewEscapesThePluginsName()
    {
        await using var plugin = await PluginApp.StartAsync(name: "<script>alert(1)</script>");

        var body = await plugin.Client.GetStringAsync("/");

        Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFramePolicyFollowsThePairedEndpoint()
    {
        await using var plugin = await PluginApp.StartAsync();
        var credentials = plugin.App.Services.GetRequiredService<ProviderCredentials>();

        Assert.Equal("frame-ancestors 'self' http://*:8090 https://*:8090", await PolicyAsync(plugin, "/"));

        // Paired over mDNS at an IP, while the owner opens Gleanvolt at gleanvolt.local: any host on the paired port.
        // TEST-NET-1, so the connection that starts with the token dials nothing real.
        credentials.Save(new Uri("http://192.0.2.5:8091"), "gvp_paired");

        // Every response carries it: the refusal of a request without a ticket, the page, and a static file.
        const string Paired = "frame-ancestors 'self' http://*:8091 https://*:8091";
        Assert.Equal(Paired, await PolicyAsync(plugin, "/"));
        Assert.Equal(Paired, await PolicyAsync(plugin, "/?" + ProviderPage.TicketParameter + "=" + Ticket("gvp_paired")));
        Assert.Equal(Paired, await PolicyAsync(plugin, "/app.css"));
    }

    [Fact]
    public async Task AConfiguredEndpointIsFramedByItsOwnOriginEvenWhenPaired()
    {
        await using var plugin = await PluginApp.StartAsync(extra: ["--Gleanvolt:Endpoint=http://192.0.2.5:8090"]);

        plugin.App.Services.GetRequiredService<ProviderCredentials>().Save(new Uri("http://192.0.2.6:8091"), "gvp_paired");

        Assert.Equal("frame-ancestors 'self' http://192.0.2.5:8090", await PolicyAsync(plugin, "/"));
    }

    [Fact]
    public async Task ConfiguredFrameAncestorsWinOverAPairedEndpoint()
    {
        await using var plugin = await PluginApp.StartAsync(extra: ["--Gleanvolt:FrameAncestors=http://gleanvolt.local:8090"]);

        plugin.App.Services.GetRequiredService<ProviderCredentials>().Save(new Uri("http://192.0.2.5:8091"), "gvp_paired");

        Assert.Equal("frame-ancestors 'self' http://gleanvolt.local:8090", await PolicyAsync(plugin, "/"));
    }

    [Fact]
    public async Task ThePageSaysWhereTheTokenCameFrom()
    {
        await using var plugin = await PluginApp.StartAsync();
        // An endpoint nothing listens on: the connection that starts with the token fails at once and keeps it.
        plugin.App.Services.GetRequiredService<ProviderCredentials>().Save(new Uri("http://127.0.0.1:9"), "gvp_paired");

        var response = await plugin.Client.GetAsync("/?" + ProviderPage.TicketParameter + "=" + Ticket("gvp_paired"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(SharedText.TokenFromPairing.En, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static string Ticket(string token) =>
        Uri.EscapeDataString(ProviderPage.CreateTicket(token, PluginApp.Id, DateTimeOffset.UtcNow.AddMinutes(2)));

    private static async Task<string> PolicyAsync(PluginApp plugin, string path)
    {
        var response = await plugin.Client.GetAsync(path);
        return Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
    }

    // A plugin's whole app, as PluginHost builds it, on a test server with no token and nothing to find.
    private sealed class PluginApp : IAsyncDisposable
    {
        public const string Id = "test-charger";

        private readonly string _directory;

        private PluginApp(WebApplication app, string directory)
        {
            App = app;
            _directory = directory;
            Client = app.GetTestClient();
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public static async Task<PluginApp> StartAsync(string name = "Test charger", string[]? extra = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "gleanvolt-plugin-" + Guid.NewGuid().ToString("N"));
            var plugin = new PluginInfo(Id, name, "Test", 8110, 502) { PageAssembly = typeof(TestPluginPage).Assembly };
            // The host's own static assets stand in for the plugin's: a test project has no manifest of its own, and
            // the host's serves its stylesheet at /app.css, from the source in Development as a plugin run from its
            // project does.
            var builder = PluginHost.CreateBuilder<TestCharger>(
                [$"--DataDirectory={directory}", "--applicationName=Gleanvolt.Chargers.Hosting", "--environment=Development", .. extra ?? []], plugin);
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IGleanvoltFinder, NoGleanvolt>();
            var app = PluginHost.Build(builder);
            await app.StartAsync();
            return new PluginApp(app, directory);
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class NoGleanvolt : IGleanvoltFinder
    {
        public async IAsyncEnumerable<Uri> CandidatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class TestCharger : PluginCharger
    {
        public override ChargerCapabilities Capabilities { get; } = new(6, 16, 1);

        public override Task<ChargerReading> ReadAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Nothing to read.");

        public override Task SetCurrentAsync(int amps, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task SetChargingAllowedAsync(bool allowed, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // A device that answers every request with a work state.
    private sealed class Answers : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":"charger_free"}""") });
    }
}

/// <summary>The test plugin's page: the shared frame and nothing of its own.</summary>
[Route("/")]
public sealed class TestPluginPage : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<PluginFrame>(0);
        builder.CloseComponent();
    }
}
