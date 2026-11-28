using Gleanvolt.Cars;
using Gleanvolt.Plugins;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts.Vehicle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Testing;

/// <summary>
/// A car plugin built on <c>Gleanvolt.Cars.Hosting</c>, as its host wires it, without the page (#473): its settings in
/// a temporary data directory, and its car connected to a <see cref="VehicleTestHub"/> with the clock and the status
/// reporter, so a plugin's own tests run without Gleanvolt and without the car's maker.
/// </summary>
public sealed class CarPluginHarness : IAsyncDisposable
{
    /// <param name="plugin">What the plugin is.</param>
    /// <param name="configuration">The plugin's configuration, e.g. <c>Skoda:Vin</c>.</param>
    public CarPluginHarness(PluginInfo plugin, IDictionary<string, string?>? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        Plugin = plugin;
        Directory = Path.Combine(Path.GetTempPath(), "gleanvolt-cars-" + Guid.NewGuid().ToString("N"));
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()).Build();
        Files = new ProviderSecretStore(Directory);
        Settings = new PluginSettings(Files, Configuration, plugin);
    }

    public PluginInfo Plugin { get; }

    /// <summary>The plugin's data directory, deleted with the harness.</summary>
    public string Directory { get; }

    public IConfiguration Configuration { get; }

    /// <summary>The plugin's secret store, in <see cref="Directory"/>.</summary>
    public ProviderSecretStore Files { get; }

    public PluginSettings Settings { get; }

    /// <summary>The hub, after <see cref="ConnectAsync"/>.</summary>
    public VehicleTestHub? Hub { get; private set; }

    public ProviderCredentials? Credentials { get; private set; }

    public ProviderPairing? Pairing { get; private set; }

    public VehicleProviderConnection? Connection { get; private set; }

    /// <summary>The car's clock, after <c>StartAsync</c> or <see cref="ConnectAsync"/>.</summary>
    public CarPoller? Poller { get; private set; }

    /// <summary>
    /// Starts the car's clock with a connection that has nowhere to go: a plugin that runs before the owner has accepted
    /// it. It reads the car but publishes nothing.
    /// </summary>
    public Task<CarPoller> StartAsync(PluginCar car, TimeProvider? time = null) =>
        StartAsync(car, new ProviderOptions { ProviderId = Plugin.ProviderId, Name = Plugin.Name }, time);

    /// <summary>
    /// Starts a hub and connects the car to it, with its clock and its status reporter, as the host wires them; waits
    /// for the welcome. <paramref name="paired"/>: neither the token nor the address is set, and pairing must bring them.
    /// </summary>
    public async Task<VehicleTestHub> ConnectAsync(PluginCar car, VehicleTestHubOptions? options = null, bool paired = false, TimeProvider? time = null)
    {
        options ??= new VehicleTestHubOptions();
        if (options.Timeout == new VehicleTestHubOptions().Timeout)
        {
            options.Timeout = TimeSpan.FromSeconds(5);
        }

        Hub = await VehicleTestHub.StartAsync(options).ConfigureAwait(false);
        var provider = new ProviderOptions();
        if (!paired)
        {
            Hub.Configure(provider);
        }

        provider.ProviderId = Plugin.ProviderId;
        provider.Name = Plugin.Name;
        provider.InitialRetryDelay = TimeSpan.FromMilliseconds(100);
        provider.RefusedRetryDelay = TimeSpan.FromMilliseconds(100);

        await StartAsync(car, provider, time).ConfigureAwait(false);

        if (paired)
        {
            Pairing = new ProviderPairing(provider, Credentials!, new HubFinder(Hub.Endpoint), Files, null, new QuickTime(),
                NullLogger<ProviderPairing>.Instance, VehicleContract.HubPath);
            await Pairing.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await Hub.WaitForHelloAsync().ConfigureAwait(false);
        await PluginHarness.Eventually(() => Connection!.IsAttached, "the plugin should have its welcome").ConfigureAwait(false);
        return Hub;
    }

    /// <summary>A client factory whose every client answers from <paramref name="handler"/>: the car's maker, faked.</summary>
    public IHttpClientFactory FakeClients(HttpMessageHandler handler) => new FakeClientFactory(handler);

    public async ValueTask DisposeAsync()
    {
        if (Poller is not null)
        {
            await Poller.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Poller.Dispose();
        }

        if (Pairing is not null)
        {
            await Pairing.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Pairing.Dispose();
        }

        if (Connection is not null)
        {
            await Connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Connection.Dispose();
        }

        if (Hub is not null)
        {
            await Hub.DisposeAsync().ConfigureAwait(false);
        }

        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private async Task<CarPoller> StartAsync(PluginCar car, ProviderOptions provider, TimeProvider? time)
    {
        ArgumentNullException.ThrowIfNull(car);
        Credentials = new ProviderCredentials(Options.Create(provider), Files);
        Connection = new VehicleProviderConnection(Options.Create(provider), car, NullLogger<VehicleProviderConnection>.Instance, Credentials);
        _ = new PluginStatusReporter<VehicleWelcome>(Connection, car, NullLogger.Instance);
        Poller = new CarPoller(Connection, car, time ?? TimeProvider.System, NullLogger<CarPoller>.Instance);

        await Connection.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await Poller.StartAsync(CancellationToken.None).ConfigureAwait(false);
        return Poller;
    }

    private sealed class FakeClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
