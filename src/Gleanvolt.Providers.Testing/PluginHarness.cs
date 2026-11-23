using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Charger;
using Gleanvolt.Providers.Contracts.Charger;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Gleanvolt.Chargers;

namespace Gleanvolt.Providers.Testing;

/// <summary>
/// A charger plugin built on <c>Gleanvolt.Chargers.Hosting</c>, as its host wires it, without the page (#443): its
/// settings in a data directory of its own, the recorder, the session, and, once connected, its charger behind the
/// ordinary SDK on <see cref="ChargerTestHub"/>. Make the plugin's charger from <see cref="Settings"/>,
/// <see cref="Session"/> and <see cref="Modbus"/> or <see cref="FakeClients"/>, then <see cref="ConnectAsync"/>.
/// </summary>
public sealed class PluginHarness : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _owned = [];

    /// <param name="plugin">What the plugin is, as its Program.cs names it.</param>
    /// <param name="configuration">The plugin's configuration, e.g. <c>Solax:Host</c>.</param>
    public PluginHarness(PluginInfo plugin, IDictionary<string, string?>? configuration = null)
    {
        Plugin = plugin;
        Directory = Path.Combine(Path.GetTempPath(), "gleanvolt-chargers-" + Guid.NewGuid().ToString("N"));
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()).Build();
        Files = new ProviderSecretStore(Directory);
        Settings = new PluginSettings(Files, Configuration, plugin);
    }

    /// <summary>The plugin.</summary>
    public PluginInfo Plugin { get; }

    /// <summary>Its data directory, deleted on dispose.</summary>
    public string Directory { get; }

    /// <summary>Its configuration.</summary>
    public IConfiguration Configuration { get; }

    /// <summary>The SDK's store over the data directory.</summary>
    public ProviderSecretStore Files { get; }

    /// <summary>The settings its page saves.</summary>
    public PluginSettings Settings { get; }

    /// <summary>The recording for testers.</summary>
    public DeviceRecorder Recorder { get; } = new(TimeProvider.System);

    /// <summary>What Gleanvolt said in the welcome, kept by the status reporter.</summary>
    public GleanvoltSession Session { get; } = new();

    /// <summary>The hub, once connected.</summary>
    public ChargerTestHub? Hub { get; private set; }

    /// <summary>The plugin's connection, once connected.</summary>
    public ChargerProviderConnection? Connection { get; private set; }

    /// <summary>A Modbus device at the settings' address and unit id (502 and 1 by default), as a Modbus plugin registers it.</summary>
    public ModbusDevice Modbus(TimeSpan? interval = null)
    {
        var device = new ModbusDevice(
            Settings.Address, Settings.Get("unitId"), 502, 1, interval ?? TimeSpan.Zero, Recorder, NullLogger<ModbusDevice>.Instance);
        _owned.Add(device);
        return device;
    }

    /// <summary>Connects <paramref name="charger"/> to a test hub whose charge control asks <paramref name="limits"/>, and waits for it to be attached.</summary>
    public async Task<ChargerTestHub> ConnectAsync(PluginCharger charger, ChargerLimits limits, bool attaches = true)
    {
        Hub = await ChargerTestHub.StartAsync(new ChargerTestHubOptions { Limits = limits, Timeout = TimeSpan.FromSeconds(5) }).ConfigureAwait(false);
        var options = new ProviderOptions();
        Hub.Configure(options);
        options.ProviderId = Plugin.ProviderId;
        options.Name = Plugin.Name;
        options.InitialRetryDelay = TimeSpan.FromMilliseconds(100);
        options.RefusedRetryDelay = TimeSpan.FromMilliseconds(100);

        Connection = new ChargerProviderConnection(Options.Create(options), charger, NullLogger<ChargerProviderConnection>.Instance);
        _ = new StatusReporter(Connection, charger, Session, NullLogger<StatusReporter>.Instance);
        await Connection.StartAsync(CancellationToken.None).ConfigureAwait(false);

        if (attaches)
        {
            await Hub.WaitForHelloAsync().ConfigureAwait(false);
            await Eventually(() => Connection.IsAttached, "the plugin should have its welcome");
        }

        return Hub;
    }

    /// <summary>Waits up to 5 seconds for <paramref name="condition"/>; throws with <paramref name="because"/> when it never holds.</summary>
    public static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25).ConfigureAwait(false);
        }

        if (!condition())
        {
            throw new TimeoutException(because);
        }
    }

    /// <summary>An HTTP client factory whose every client reaches <paramref name="handler"/>, recorded as a plugin's are.</summary>
    public IHttpClientFactory FakeClients(HttpMessageHandler handler) => new RecordedClients(handler, Recorder);

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            await Connection.StopAsync(CancellationToken.None);
            Connection.Dispose();
        }

        if (Hub is not null)
        {
            await Hub.DisposeAsync();
        }

        foreach (var owned in _owned)
        {
            await owned.DisposeAsync();
        }

        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

/// <summary>An HTTP client factory whose every client goes through <paramref name="handler"/>, recorded as a plugin's are.</summary>
internal sealed class RecordedClients(HttpMessageHandler handler, DeviceRecorder recorder) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(new RecordingHandler(recorder) { InnerHandler = handler }, disposeHandler: false);
}
