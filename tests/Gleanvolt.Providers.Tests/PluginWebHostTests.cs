using System.Net;
using System.Runtime.CompilerServices;
using Gleanvolt.Plugins;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Charger;
using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;
using Gleanvolt.Providers.Contracts.Vehicle;
using Gleanvolt.Providers.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Providers.Tests;

/// <summary>
/// Gleanvolt.Plugins.Hosting (#472), the host every plugin kind shares: a plugin of a kind other than the charger
/// waits to be paired and is framed as the pairing says, and the status reporter any kind uses.
/// </summary>
public sealed class PluginWebHostTests
{
    private const string Id = "test-car";

    [Fact]
    public async Task AnyKindWaitsToBePairedWithoutAToken()
    {
        await using var plugin = await CarApp.StartAsync();

        var response = await plugin.Client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var code = plugin.App.Services.GetRequiredService<ProviderPairing>().Status.Code;
        Assert.NotEmpty(code);
        Assert.Contains(WebUtility.HtmlEncode(code), await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyKindsFramePolicyFollowsThePairing()
    {
        await using var plugin = await CarApp.StartAsync();

        Assert.Equal("frame-ancestors 'self' http://*:8090 https://*:8090", await PolicyAsync(plugin));

        // TEST-NET-1: the connection that starts with the token dials nothing real.
        plugin.App.Services.GetRequiredService<ProviderCredentials>().Save(new Uri("http://192.0.2.5:8091"), "gvp_paired");

        Assert.Equal("frame-ancestors 'self' http://*:8091 https://*:8091", await PolicyAsync(plugin));
    }

    [Fact]
    public void AMachineWhoseAddressesCannotBeListedShowsNone() =>
        Assert.Empty(PluginWebHost.AddressesOrNone(() => throw new System.Net.NetworkInformation.NetworkInformationException()));

    [Fact]
    public async Task TheStatusIsReportedAfterEveryHelloAndOnEachChange()
    {
        await using var hub = await ChargerTestHub.StartAsync(new ChargerTestHubOptions { Timeout = TimeSpan.FromSeconds(5) });
        var status = new FakeStatus();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGleanvoltChargerProvider<IdleCharger>(options =>
        {
            hub.Configure(options);
            options.ProviderId = "fake-charger";
            options.Name = "Fake charger";
            options.InitialRetryDelay = TimeSpan.FromMilliseconds(100);
        });
        builder.Services.AddHostedService(provider => new PluginStatusReporter<ChargerWelcome>(
            provider.GetRequiredService<ChargerProviderConnection>(), status, NullLogger.Instance));

        using var plugin = builder.Build();
        await plugin.StartAsync();
        try
        {
            await hub.WaitForHelloAsync();
            await PluginHarness.Eventually(() => hub.Statuses.Count == 1, "the status is reported after the hello");

            status.Raise();
            await Task.Delay(200);
            Assert.Single(hub.Statuses);

            status.Status = new ProviderStatus(false, "Something is wrong.");
            status.Raise();
            await PluginHarness.Eventually(() => hub.Statuses.Count == 2, "a changed status is reported");
            Assert.False(hub.Statuses[^1].Healthy);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    private static async Task<string> PolicyAsync(CarApp plugin)
    {
        var response = await plugin.Client.GetAsync("/");
        return Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
    }

    // A plugin of the vehicle kind on the shared host alone, on a test server with no token and nothing to find.
    private sealed class CarApp : IAsyncDisposable
    {
        private readonly string _directory;

        private CarApp(WebApplication app, string directory)
        {
            App = app;
            _directory = directory;
            Client = app.GetTestClient();
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public static async Task<CarApp> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "gleanvolt-plugin-" + Guid.NewGuid().ToString("N"));
            var plugin = new PluginInfo(Id, "Test car", "Test", 8111) { PageAssembly = typeof(TestPluginPage).Assembly };
            var builder = PluginWebHost.CreateBuilder(
                [$"--DataDirectory={directory}", "--applicationName=Gleanvolt.Plugins.Hosting", "--environment=Development"],
                plugin,
                (services, configure) => services.AddGleanvoltVehicleProvider<NoCar>(configure));
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IGleanvoltFinder, NoGleanvolt>();
            var app = PluginWebHost.Build(builder);
            await app.StartAsync();
            return new CarApp(app, directory);
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class NoCar : IVehicleSource
    {
        public Task<VehicleReading?> GetReadingAsync(VehicleInfo car, CancellationToken cancellationToken) =>
            Task.FromResult<VehicleReading?>(null);
    }

    private sealed class NoGleanvolt : IGleanvoltFinder
    {
        public async IAsyncEnumerable<Uri> CandidatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class FakeStatus : IPluginStatus
    {
        public ProviderStatus? Status { get; set; } = new(true);

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();
    }

    private sealed class IdleCharger : IChargerSource
    {
        public ChargerCapabilities Capabilities { get; } = ChargerCapabilities.Default;

        public Task<ChargerReading> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ChargerReading(DateTimeOffset.UtcNow, ChargerState.Available, 0));

        public Task SetCurrentAsync(int amps, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetChargingAllowedAsync(bool allowed, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
