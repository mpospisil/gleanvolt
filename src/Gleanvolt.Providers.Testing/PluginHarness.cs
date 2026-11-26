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

    /// <summary>The plugin's token and address, once connected: where a paired token lands.</summary>
    public ProviderCredentials? Credentials { get; private set; }

    /// <summary>The plugin's pairing, once connected with <c>paired: true</c>.</summary>
    public ProviderPairing? Pairing { get; private set; }

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

    /// <summary>
    /// Connects <paramref name="charger"/> to a test hub whose charge control asks <paramref name="limits"/>, and waits for it to be attached.
    /// With <paramref name="paired"/> the plugin starts with no token, as a plugin installed before Gleanvolt does, finds the
    /// hub and is paired by it (#459); the poll that waits for the owner runs a hundred times faster than the SDK's 3 s.
    /// </summary>
    public async Task<ChargerTestHub> ConnectAsync(PluginCharger charger, ChargerLimits limits, bool attaches = true, bool paired = false)
    {
        Hub = await ChargerTestHub.StartAsync(new ChargerTestHubOptions { Limits = limits, Timeout = TimeSpan.FromSeconds(5) }).ConfigureAwait(false);
        var options = new ProviderOptions();
        // Paired, neither the token nor the address is set: it is the pairing that must bring them.
        if (!paired)
        {
            Hub.Configure(options);
        }

        options.ProviderId = Plugin.ProviderId;
        options.Name = Plugin.Name;
        options.InitialRetryDelay = TimeSpan.FromMilliseconds(100);
        options.RefusedRetryDelay = TimeSpan.FromMilliseconds(100);

        Credentials = new ProviderCredentials(Options.Create(options), Files);
        Connection = new ChargerProviderConnection(Options.Create(options), charger, NullLogger<ChargerProviderConnection>.Instance, Credentials);
        _ = new StatusReporter(Connection, charger, Session, NullLogger<StatusReporter>.Instance);
        await Connection.StartAsync(CancellationToken.None).ConfigureAwait(false);

        if (paired)
        {
            Pairing = new ProviderPairing(options, Credentials, new HubFinder(Hub.Endpoint), Files, null, new QuickTime(),
                NullLogger<ProviderPairing>.Instance, ChargerContract.HubPath);
            await Pairing.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }

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
        if (Pairing is not null)
        {
            await Pairing.StopAsync(CancellationToken.None);
            Pairing.Dispose();
        }

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

/// <summary>Yields the test hub's address, as Gleanvolt's finder yields Gleanvolt's.</summary>
internal sealed class HubFinder(Uri hub) : IGleanvoltFinder
{
    public async IAsyncEnumerable<Uri> CandidatesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return hub;
    }
}

/// <summary>
/// The system clock with every timer a hundred times shorter, so the pairing's 3 s poll takes 30 ms and a test
/// does not sleep for it.
/// </summary>
internal sealed class QuickTime : TimeProvider
{
    private const int Speedup = 100;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        base.CreateTimer(callback, state, Shorter(dueTime), Shorter(period));

    // Infinite stays infinite: -1 ms is how a timer says "never" or "once".
    private static TimeSpan Shorter(TimeSpan time) => time < TimeSpan.Zero ? time : time / Speedup;
}
